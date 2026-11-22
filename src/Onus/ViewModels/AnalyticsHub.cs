using System.Windows.Threading;
using Onus.Core;
using Onus.Services;

namespace Onus.ViewModels;

/// <summary>
/// App-lifetime owner of the audio tap and the analytics-cache recorder. Capture runs while the analyzer panel is
/// open, while a stream-only clip plays through suno.com's player (so every such play can be cached), and until a
/// capture in progress ends. The panel only draws from <see cref="Tap"/>.
/// </summary>
public sealed class AnalyticsHub : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly SunoAuth _auth;
    private readonly Settings _settings;
    private readonly Downloader _downloader;
    private readonly Func<string, ClipInfo?> _findClip;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _feed;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _panelOpen, _starting, _failed;

    public AudioTap? Tap { get; private set; }
    public AnalyticsRecorder? Recorder { get; private set; }

    /// <summary>Why capture isn't available (shown in the panel), else null.</summary>
    public string? Error { get; private set; }

    /// <summary>Clip id + cached file path, on the UI thread.</summary>
    public event Action<string, string>? Saved;

    public AnalyticsHub(PlayerViewModel player, SunoAuth auth, Settings settings, Downloader downloader,
        Func<string, ClipInfo?> findClip, Dispatcher ui)
    {
        (_player, _auth, _settings, _downloader, _findClip, _ui) = (player, auth, settings, downloader, findClip, ui);
        player.TrackChanged += _ => { Tap?.ResetTrack(); _failed = false; };
        _feed = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => Feed(), ui);
        _feed.Start();
    }

    public void PanelOpened() { _panelOpen = true; _failed = false; Feed(); }

    public void PanelClosed() => _panelOpen = false;

    private bool Wanted => _panelOpen || _player.ViaSuno || Recorder?.IsRecording == true;

    private void Feed()
    {
        if (!Wanted) { Stop(); return; }
        if (Tap is null) { _ = StartAsync(); return; }
        var (local, web) = _player.AppliedVolume;
        Tap.SetVolumes(local, web);
        Recorder?.Tick(_player.Current?.Key, _player.ViaSuno, _player.IsPlaying, _player.Position, _player.Duration,
            _clock.Elapsed.TotalSeconds);
    }

    private async Task StartAsync()
    {
        if (_starting || _failed) return;
        _starting = true;
        try
        {
            var tap = await AudioTap.StartAsync(_auth.BrowserProcessId);
            if (!Wanted) { tap.Dispose(); return; }
            Tap = tap;
            Recorder = new AnalyticsRecorder(tap, () => _settings.DownloadFolder, TagAsync);
            Recorder.Saved += (id, path) => Saved?.Invoke(id, path);
            Error = null;
        }
        catch (Exception e)
        {
            _failed = true; // retried on the next song or when the panel opens
            Error = $"Live analysis isn't available on this PC: {e.Message}";
            Log.Warn($"Audio tap unavailable: {e.Message}");
        }
        finally { _starting = false; }
    }

    /// <summary>Same tags as a download; clip details come from the Suno page's row (or a refetch).</summary>
    private async Task TagAsync(string clipId, string path)
    {
        var clip = await _ui.InvokeAsync(() => _findClip(clipId)) ?? new ClipInfo { Id = clipId };
        await _downloader.TagCaptureAsync(path, clip);
    }

    private void Stop()
    {
        Recorder?.Dispose();
        Recorder = null;
        Tap?.Dispose();
        Tap = null;
    }

    public void Dispose()
    {
        _feed.Stop();
        Stop();
    }
}
