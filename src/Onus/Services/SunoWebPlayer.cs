using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Onus.Core;

namespace Onus.Services;

/// <summary>
/// Streams clips whose audio Suno withholds (<see cref="ClipInfo.AudioLocked"/>) through suno.com's own player.
/// A second hidden WebView2 (same signed-in profile as <see cref="SunoAuth"/>) opens the song page and presses
/// its Play button; Suno's code fetches and decrypts the stream as in any browser. Onus never touches the
/// audio data: it only drives the page's &lt;audio&gt; element (play/pause/seek/volume) and listens to its events.
/// ponytail: depends on suno.com's song page (button[aria-label="Play"]); a redesign surfaces as a Failed message.
/// </summary>
public sealed class SunoWebPlayer(SunoAuth auth)
{
    // Runs in every document before the page's own scripts. Reports the main <audio> (not Suno's silent
    // "sil-100.mp3" unlocker) to the host and, on a /song/ page, presses Play until playback starts.
    private const string HookScript = """
        (() => {
          if (window.top !== window || !window.chrome?.webview) return;
          const post = m => window.chrome.webview.postMessage(m);
          const main = () => [...document.querySelectorAll('audio')].find(a => a.currentSrc && !/\/sil-\d+\./.test(a.currentSrc));
          let vol = 1, started = false, done = false, last = 0, ours = false;
          // Suno puts its own curve on the volume (0.8 → 0.46). Only Onus may set it, so the analyzer knows the
          // exact gain to undo the moment it changes.
          const volume$ = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'volume');
          Object.defineProperty(HTMLMediaElement.prototype, 'volume', { configurable: true, enumerable: true,
            get() { return volume$.get.call(this); }, set(v) { if (ours) volume$.set.call(this, v); } });
          const setVolume = a => { ours = true; try { a.volume = vol; } finally { ours = false; } };
          const report = a => post({ t: 'time', pos: a.currentTime, dur: a.duration || 0, playing: !a.paused });
          window.__onus = {
            // AbortError = a play() overtaken by a pause or a new load: harmless, the next event tells the truth
            play: () => main()?.play().catch(e => post({ t: e.name === 'AbortError' ? 'log' : 'error', m: 'play(): ' + e.name })),
            pause: () => main()?.pause(),
            seek: s => { const a = main(); if (a) a.currentTime = s; },
            volume: v => { vol = v; const a = main(); if (a) setVolume(a); },
            stop: () => { done = true; main()?.pause(); },
          };
          const on = (type, fn) => document.addEventListener(type, e => { if (e.target === main()) fn(e.target); }, true);
          on('playing', a => {
            if (done) { a.pause(); return; }   // Suno moving on to its own next song after ours ended
            if (!started) post({ t: 'log', m: 'playing' });
            started = true; setVolume(a); report(a);
          });
          for (const type of ['pause', 'durationchange', 'seeked']) on(type, report);
          on('timeupdate', a => { if (Date.now() - last > 240) { last = Date.now(); report(a); } });
          on('ended', () => { if (!done) { done = true; post({ t: 'ended' }); } });
          on('error', a => post({ t: 'error', m: 'media error ' + (a.error?.code ?? '') }));
          if (!location.pathname.startsWith('/song/')) return;
          let ticks = 0;
          const press = setInterval(() => {
            if (started || done) { clearInterval(press); return; }
            const button = document.querySelector('button[aria-label="Play"]');
            const a = main();
            if (a && a.paused && a.readyState >= 1 && ticks % 4 === 2) {
              // Suno loaded our song but didn't start it (no real click): start the loaded element ourselves
              a.play().catch(e => post({ t: 'log', m: 'direct play(): ' + e.name + ' ' + e.message }));
            } else if (button && ticks % 4 === 0) {   // re-press each second until React has hydrated
              if (ticks < 4) post({ t: 'log', m: 'pressing Play' });
              button.click();
            }
            if (++ticks > 80) {
              clearInterval(press);
              post({ t: 'error', m: 'the song page never started playing (button ' + !!button + ', audio ' + (a ? a.readyState + '/' + a.paused : 'none') + ')' });
            }
          }, 250);
        })();
        """;

    private CoreWebView2Controller? _view;
    private Task? _init;

    /// <summary>Position, duration (s), playing.</summary>
    public event Action<double, double, bool>? Progress;

    public event Action? Ended;
    public event Action<string>? Failed;

    /// <summary>A song page link (<see cref="ClipInfo.SongPage"/>) means "play through Suno's player".</summary>
    public static bool Handles(Uri source) =>
        source.Host.Equals("suno.com", StringComparison.OrdinalIgnoreCase) && source.AbsolutePath.StartsWith("/song/", StringComparison.Ordinal);

    public async Task PlayAsync(Uri songPage, double volume)
    {
        try
        {
            await (_init ??= CreateAsync());
            _pendingVolume = volume;
            _view!.CoreWebView2.Navigate(songPage.AbsoluteUri);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            _init = null;
            Failed?.Invoke($"Suno's player could not start: {e.Message}");
        }
    }

    private double _pendingVolume = 1;

    public void Pause() => Run("pause()");
    public void Resume() => Run("play()");
    public void Seek(double seconds) => Run($"seek({seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    public void SetVolume(double v) { _pendingVolume = v; Run($"volume({v.ToString(System.Globalization.CultureInfo.InvariantCulture)})"); }

    /// <summary>Stop and unload the page, so nothing keeps streaming once Onus plays something else.</summary>
    public void Stop()
    {
        Run("stop()");
        try { _view?.CoreWebView2.Navigate("about:blank"); }
        catch (Exception e) when (e is InvalidOperationException or COMException) { } // already disposed at app exit
    }

    private void Run(string call)
    {
        try { _ = _view?.CoreWebView2.ExecuteScriptAsync($"window.__onus && window.__onus.{call}"); }
        catch (Exception e) when (e is InvalidOperationException or COMException) { }
    }

    private async Task CreateAsync()
    {
        _view = await (await auth.EnvironmentAsync()).CreateCoreWebView2ControllerAsync(auth.Hwnd);
        // Chromium won't load media in a page that was never shown, so the view is "shown" at 1×1 px.
        _view.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
        _view.IsVisible = true;
        var web = _view.CoreWebView2;
        web.Settings.AreDefaultScriptDialogsEnabled = false;
        await web.AddScriptToExecuteOnDocumentCreatedAsync(HookScript);
        web.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        web.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && web.Source.StartsWith("https://suno.com/song/", StringComparison.Ordinal))
                Failed?.Invoke($"suno.com didn't load ({e.WebErrorStatus})");
            else Run($"volume({_pendingVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        };
    }

    internal void OnMessage(string json)
    {
        if (Parse(json) is not { } m) return;
        switch (m.Type)
        {
            case "time":
                Progress?.Invoke(m.Position, m.Duration, m.Playing);
                break;
            case "ended": Ended?.Invoke(); break;
            case "log": Log.Info($"Suno web player: {m.Text}"); break;
            case "error":
                Log.Warn($"Suno web player: {m.Text}");
                Failed?.Invoke($"Suno's player: {m.Text}");
                break;
        }
    }

    internal sealed record Message(string Type, double Position, double Duration, bool Playing, string Text);

    /// <summary>Page → host message; null for anything malformed (the page is not trusted to be well-formed).</summary>
    internal static Message? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return null;
            double Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : 0;
            return new Message(t.GetString()!, Num("pos"), Num("dur"),
                r.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True,
                r.TryGetProperty("m", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString()! : "");
        }
        catch (JsonException) { return null; }
    }
}
