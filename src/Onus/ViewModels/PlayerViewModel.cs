using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Onus.Core;
using Onus.Services;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Onus.ViewModels;

/// <summary>
/// The player. Uses Windows' own media engine (Windows.Media.Playback) instead of VLC — nothing to install,
/// plays local files and Suno streams alike, and hooks into the system media controls so keyboard media keys
/// and the Windows volume flyout work. See ADR 0003. Clips Suno only streams encrypted play through suno.com's own
/// player instead (<see cref="SunoWebPlayer"/>); then the media keys and flyout are driven by hand.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly MediaPlayer _mp = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly Dispatcher _ui;
    private readonly TagStore _tags;
    private readonly Settings _settings;
    private readonly DispatcherTimer _clock;
    private readonly Random _rng = new();
    private readonly SunoWebPlayer _web;
    private bool _viaWeb;
    private List<PlayItem> _queue = [];
    private int _index = -1;

    [ObservableProperty] private PlayItem? _current;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private double _position;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SeekMax))] private double _duration;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private RepeatMode _repeat;
    [ObservableProperty] private string? _currentTag;

    /// <summary>The current song plays through suno.com's own player (stream-only clip).</summary>
    public bool ViaSuno => _viaWeb;

    /// <summary>Volume both players actually apply: the slider, but never below <see cref="AudioTap.MinVolume"/>, so
    /// the analyzer can still divide it out at 0 (−100 dB is silent to the ear).</summary>
    public double AppliedVolume => Math.Max(AudioTap.MinVolume, Volume);

    /// <summary>True while the user drags the seek bar, so the clock doesn't fight them.</summary>
    public bool IsSeeking { get; set; }

    public event Action<PlayItem?>? TrackChanged;
    public event Action<string, string?>? TagChanged; // key, new tag
    public event Action<string>? Problem;

    public PlayerViewModel(Dispatcher ui, TagStore tags, Settings settings, SunoWebPlayer web)
    {
        _ui = ui;
        _web = web;
        _tags = tags;
        _settings = settings;
        _volume = settings.Volume;
        _mp.Volume = AppliedVolume;

        _mp.MediaEnded += (_, _) => _ui.InvokeAsync(() => Advance(auto: true));
        _mp.MediaFailed += (_, e) => _ui.InvokeAsync(() => OnFailed(string.IsNullOrWhiteSpace(e.ErrorMessage) // empty for HTTP refusals
            ? $"{e.Error} (0x{e.ExtendedErrorCode?.HResult:X8})" : e.ErrorMessage));
        _mp.PlaybackSession.PlaybackStateChanged += (s, _) => _ui.InvokeAsync(() =>
        {
            IsPlaying = s.PlaybackState == MediaPlaybackState.Playing;
            IsBuffering = s.PlaybackState is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
        });
        _mp.PlaybackSession.NaturalDurationChanged += (s, _) => _ui.InvokeAsync(() =>
        {
            if (s.NaturalDuration.TotalSeconds > 0) Duration = s.NaturalDuration.TotalSeconds;
        });

        // System media controls: media keys, Windows flyout, lock screen.
        var cm = _mp.CommandManager;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; _ui.InvokeAsync(() => Advance(auto: false)); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; _ui.InvokeAsync(Previous); };

        // Suno's page reports from its own thread; the media keys only reach us by hand while it plays.
        web.Progress += (pos, dur, playing) => _ui.InvokeAsync(() =>
        {
            if (!_viaWeb) return;
            if (!IsSeeking) Position = pos;
            if (dur > 0) Duration = dur;
            IsPlaying = playing;
            IsBuffering = false;
            _mp.SystemMediaTransportControls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        });
        web.Ended += () => _ui.InvokeAsync(() => { if (_viaWeb) Advance(auto: true); });
        web.Failed += m => _ui.InvokeAsync(() => { if (_viaWeb) OnFailed(m); });
        _mp.SystemMediaTransportControls.ButtonPressed += (_, e) => _ui.InvokeAsync(() =>
        {
            if (!_viaWeb) return; // CommandManager handles them otherwise
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: _web.Resume(); break;
                case SystemMediaTransportControlsButton.Pause: _web.Pause(); break;
                case SystemMediaTransportControlsButton.Next: Advance(auto: false); break;
                case SystemMediaTransportControlsButton.Previous: Previous(); break;
            }
        });

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsSeeking && Current is not null && !_viaWeb) Position = _mp.PlaybackSession.Position.TotalSeconds;
        }, ui);
        _clock.Start();
    }

    /// <summary>Seek bar maximum; 1 while nothing is loaded so the thumb rests at the left.</summary>
    public double SeekMax => Duration > 0 ? Duration : 1;

    /// <summary>Replace the queue and start playing at <paramref name="start"/>.</summary>
    public void PlayQueue(IReadOnlyList<PlayItem> items, int start)
    {
        if (items.Count == 0) return;
        _queue = [.. items];
        PlayAt(Math.Clamp(start, 0, _queue.Count - 1));
    }

    public void Seek(double seconds)
    {
        if (_viaWeb) _web.Seek(Math.Max(0, seconds));
        else _mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (Current is null) { if (_queue.Count > 0) PlayAt(0); return; }
        if (_viaWeb) { if (IsPlaying) _web.Pause(); else _web.Resume(); }
        else if (IsPlaying) _mp.Pause(); else _mp.Play();
    }

    [RelayCommand] private void Next() => Advance(auto: false);

    [RelayCommand]
    private void Previous()
    {
        var i = QueueNav.Previous(_index, _queue.Count, Position, Repeat);
        if (i == _index) Seek(0); else if (i >= 0) PlayAt(i);
    }

    [RelayCommand] private void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand] private void CycleRepeat() => Repeat = (RepeatMode)(((int)Repeat + 1) % 3);

    [RelayCommand]
    private void ToggleTag(string tag)
    {
        if (Current is null) return;
        CurrentTag = _tags.Toggle(Current.Key, tag);
        TagChanged?.Invoke(Current.Key, CurrentTag);
    }

    /// <summary>Called by the library when a mark is set from a row, so the bar stays in sync.</summary>
    public void OnExternalTag(string key, string? tag)
    {
        if (Current?.Key == key) CurrentTag = tag;
    }

    partial void OnVolumeChanged(double value)
    {
        _mp.Volume = AppliedVolume;
        if (_viaWeb) _web.SetVolume(AppliedVolume);
        _settings.Volume = Math.Round(value, 2);
    }

    private void Advance(bool auto)
    {
        var i = QueueNav.Next(_index, _queue.Count, Shuffle, Repeat, auto, _rng);
        if (i >= 0) PlayAt(i);
        else if (auto && _viaWeb) { _web.Stop(); IsPlaying = false; Position = 0; }
        else if (auto) { _mp.Pause(); Seek(0); Position = 0; }
    }

    private void PlayAt(int i)
    {
        _index = i;
        var item = _queue[i];
        Current = item;
        CurrentTag = _tags.Get(item.Key);
        Position = 0;
        Duration = item.Duration;

        if (SunoWebPlayer.Handles(item.Source))
        {
            PlayViaSuno(item);
            TrackChanged?.Invoke(item);
            return;
        }
        if (_viaWeb)
        {
            _viaWeb = false;
            _web.Stop();
            _mp.CommandManager.IsEnabled = true;
        }

        var playback = new MediaPlaybackItem(MediaSource.CreateFromUri(item.Source));
        var props = playback.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = item.Title;
        props.MusicProperties.Artist = item.Artist;
        playback.ApplyDisplayProperties(props);
        _ = SetThumbnailAsync(playback, item.Art);

        _mp.Source = playback;
        _mp.Play();
        TrackChanged?.Invoke(item);
    }

    /// <summary>Hand the clip to Suno's player and take over the media keys/flyout from the CommandManager.</summary>
    private void PlayViaSuno(PlayItem item)
    {
        _mp.Pause();
        _mp.Source = null;
        _mp.CommandManager.IsEnabled = false;
        _viaWeb = true;
        IsPlaying = false;
        IsBuffering = true;

        var smtc = _mp.SystemMediaTransportControls;
        smtc.IsEnabled = smtc.IsPlayEnabled = smtc.IsPauseEnabled = smtc.IsNextEnabled = smtc.IsPreviousEnabled = true;
        smtc.PlaybackStatus = MediaPlaybackStatus.Changing;
        var d = smtc.DisplayUpdater;
        d.Type = MediaPlaybackType.Music;
        d.MusicProperties.Title = item.Title;
        d.MusicProperties.Artist = item.Artist;
        d.Thumbnail = item.Art?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true
            ? RandomAccessStreamReference.CreateFromUri(new Uri(item.Art)) : null;
        d.Update();

        _ = _web.PlayAsync(item.Source, AppliedVolume);
    }

    private static async Task SetThumbnailAsync(MediaPlaybackItem playback, string? art)
    {
        if (art is null) return;
        try
        {
            var props = playback.GetDisplayProperties();
            props.Thumbnail = art.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? RandomAccessStreamReference.CreateFromUri(new Uri(art))
                : RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(art));
            playback.ApplyDisplayProperties(props);
        }
        catch (Exception e) { Log.Warn($"Media-controls thumbnail skipped: {e.Message}"); }
    }

    private void OnFailed(string message)
    {
        var title = Current?.Title ?? "track";
        Log.Error($"Playback failed for {title}: {message}");
        Problem?.Invoke($"Can't play \"{title}\" — {message}");
        IsPlaying = false;
    }

    public void Dispose()
    {
        _clock.Stop();
        _web.Stop();
        _mp.Dispose();
    }
}
