using System.Windows;
using System.Windows.Threading;
using Onus.Core;
using Onus.Services;

namespace Onus.Views;

/// <summary>Visible suno.com in the same WebView2 profile as the hidden token browser.
/// Polls for a Clerk session and closes itself once one exists.</summary>
public partial class SignInWindow : Window
{
    private readonly SunoAuth _auth;
    private readonly DispatcherTimer _poll;
    private bool _done;

    public SignInWindow(SunoAuth auth)
    {
        InitializeComponent();
        _auth = auth;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) => await CheckAsync();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _poll.Stop();
    }

    private async Task StartAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async(await _auth.EnvironmentAsync());
            Web.CoreWebView2.Navigate(SunoAuth.Home);
            _poll.Start();
        }
        catch (Exception e)
        {
            Log.Error($"Sign-in browser failed: {e.Message}");
            StatusText.Text = "Couldn't start the built-in browser (WebView2). Use the cookie option in Settings instead.";
        }
    }

    private async Task CheckAsync()
    {
        if (_done || Web.CoreWebView2 is null) return;
        string state;
        try { state = await Web.ExecuteScriptAsync("window.Clerk && window.Clerk.session ? 'yes' : 'no'"); }
        catch (Exception) { return; } // navigating; try again next tick
        if (state != "\"yes\"" && await SunoAuth.SessionCookieAsync(Web.CoreWebView2) is null) return;

        _done = true;
        _poll.Stop();
        StatusText.Text = "Signed in — finishing up…";
        await _auth.OnSignedInAsync();
        DialogResult = true;
    }
}
