using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Onus.Core;

namespace Onus.Services;

/// <summary>
/// Suno session via an embedded, hidden Edge (WebView2) that stays on suno.com.
/// Clerk's JWT lives ~60 s, so instead of storing a token we ask Clerk for a fresh one when needed
/// (<c>Clerk.session.getToken()</c>, which Clerk caches and refreshes itself). Sign-in happens once in
/// <see cref="Views.SignInWindow"/>; cookies persist in %LocalAppData%\Onus\WebView. See ADR 0002.
/// </summary>
public sealed class SunoAuth(Dispatcher ui)
{
    public const string Home = "https://suno.com/";

    // Waits for Clerk to load (≤ 15 s, ~3 s when the loaded page has none), then returns a JWT or a sentinel.
    private const string TokenScript = """
        (async () => {
          for (let i = 0; i < 30 && !(window.Clerk && window.Clerk.loaded); i++) {
            if (i >= 6 && !window.Clerk && document.readyState === 'complete') break; // signed-out pages never load Clerk
            await new Promise(r => setTimeout(r, 500));
          }
          if (!window.Clerk) return 'NO_CLERK';
          if (!window.Clerk.session) return 'NO_SESSION';
          return (await window.Clerk.session.getToken()) || 'NO_SESSION';
        })()
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<CoreWebView2Environment>? _env;
    private CoreWebView2Controller? _hidden;
    private string? _token;
    private DateTimeOffset _expires;

    public Func<string> ManualToken { get; set; } = () => "";
    public bool IsSignedIn { get; private set; }
    public string Status { get; private set; } = "Connecting…";
    public event Action? Changed;

    /// <summary>The main window the hidden browsers are parented to (set by <see cref="InitAsync"/>).</summary>
    public IntPtr Hwnd { get; private set; }

    /// <summary>The shared WebView2 browser process (parent of its audio service), once the hidden view exists.</summary>
    public uint? BrowserProcessId => _hidden?.CoreWebView2?.BrowserProcessId;

    // Autoplay without a click: SunoWebPlayer starts Suno's player from script. No occlusion tracking: a minimized
    // Onus must not count as "hidden", or the next stream-only song's page would never load its audio. No media-key/SMTC hookup:
    // Onus's own player owns the media keys and the Windows media flyout, also while Suno's page plays.
    public Task<CoreWebView2Environment> EnvironmentAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebView, new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling,MediaSessionService,CalculateNativeWinOcclusion"));

    /// <summary>Create the hidden browser on the main window and check whether we are already signed in.</summary>
    public async Task InitAsync(IntPtr hwnd)
    {
        Hwnd = hwnd;
        try
        {
            _hidden = await (await EnvironmentAsync()).CreateCoreWebView2ControllerAsync(hwnd);
            _hidden.IsVisible = false;
            _hidden.CoreWebView2.IsMuted = true;
            var loaded = new TaskCompletionSource();
            _hidden.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            _hidden.CoreWebView2.Navigate(Home);
            // DevTools calls fail with E_INVALIDARG until the first page has committed.
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException)
        {
            SetState(false, "Microsoft Edge WebView2 runtime is missing — paste a token in Settings.");
            Log.Error($"WebView2 unavailable: {e.Message}");
            return;
        }
        await GetTokenAsync(false, CancellationToken.None);
    }

    /// <summary>A bearer token for the studio API, or null if not signed in. Safe from any thread.</summary>
    public async Task<string?> GetTokenAsync(bool refresh, CancellationToken ct)
    {
        var manual = Jwt.Clean(ManualToken());
        if (manual.Length > 0 && !refresh && (Jwt.Expiry(manual) is not { } mexp || mexp > DateTimeOffset.UtcNow.AddSeconds(5)))
            return manual;

        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _token is not null && _expires > DateTimeOffset.UtcNow.AddSeconds(15)) return _token;
            var result = await ui.InvokeAsync(EvaluateAsync).Task.Unwrap();
            if (result is null or "NO_SESSION" or "NO_CLERK")
            {
                // Signed-out suno.com doesn't load Clerk at all, so NO_CLERK normally just means "not signed in".
                // If Clerk isn't exposed but a session exists, its __session cookie still carries a fresh JWT.
                result = _hidden is null ? null : await ui.InvokeAsync(() => SessionCookieAsync(_hidden.CoreWebView2)).Task.Unwrap();
                if (result is null)
                {
                    _token = null;
                    SetState(false, "Not signed in");
                    return null;
                }
            }
            _token = result;
            _expires = Jwt.Expiry(result) ?? DateTimeOffset.UtcNow.AddSeconds(50);
            SetState(true, "Signed in");
            return _token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Called after the sign-in window saw a session: reload the hidden page so it picks up the cookies.</summary>
    public async Task OnSignedInAsync()
    {
        _token = null;
        _hidden?.CoreWebView2.Navigate(Home);
        await GetTokenAsync(true, CancellationToken.None);
    }

    /// <summary>Fallback when interactive sign-in is blocked: import Clerk's <c>__client</c> cookie value.</summary>
    public async Task ImportClientCookieAsync(string value)
    {
        if (_hidden is null) return;
        var cm = _hidden.CoreWebView2.CookieManager;
        var cookie = cm.CreateCookie("__client", value.Trim(), ".suno.com", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        cm.AddOrUpdateCookie(cookie);
        await OnSignedInAsync();
    }

    public async Task SignOutAsync()
    {
        _token = null;
        if (_hidden is not null)
        {
            _hidden.CoreWebView2.CookieManager.DeleteAllCookies();
            _hidden.CoreWebView2.Navigate(Home);
        }
        SetState(false, "Signed out");
        await Task.CompletedTask;
    }

    private async Task<string?> EvaluateAsync()
    {
        if (_hidden is null) return null;
        var args = JsonSerializer.Serialize(new { expression = TokenScript, awaitPromise = true, returnByValue = true });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // ExecuteScriptAsync can't await a promise; the DevTools protocol can.
                var json = await _hidden.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch (Exception e) when (e is COMException or JsonException or InvalidOperationException or ArgumentException)
            {
                Log.Warn($"Token lookup failed (page probably navigating): {e.Message}");
            }
            await Task.Delay(1500); // the page was mid-navigation; give it a moment
        }
        return null;
    }

    /// <summary>Clerk's <c>__session</c> cookie on suno.com: the current session JWT, if it is still valid.</summary>
    public static async Task<string?> SessionCookieAsync(CoreWebView2 web)
    {
        try
        {
            var cookies = await web.CookieManager.GetCookiesAsync(Home);
            var jwt = cookies.FirstOrDefault(c => c.Name == "__session")?.Value;
            return Jwt.Expiry(jwt) is { } exp && exp > DateTimeOffset.UtcNow.AddSeconds(5) ? jwt : null;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { return null; }
    }

    private void SetState(bool signedIn, string status)
    {
        if (IsSignedIn == signedIn && Status == status) return;
        IsSignedIn = signedIn;
        Status = status;
        ui.InvokeAsync(() => Changed?.Invoke());
    }
}
