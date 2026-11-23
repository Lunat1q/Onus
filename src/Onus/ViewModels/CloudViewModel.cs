using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Onus.Core;
using Onus.Services;

namespace Onus.ViewModels;

/// <summary>The Suno page: browse a source, stream anything, download selected/new, or Sync (fetch + download as pages arrive).</summary>
public sealed partial class CloudViewModel : ObservableObject
{
    private readonly SunoClient _suno;
    private readonly Downloader _downloader;
    private readonly LibraryIndex _index;
    private readonly Settings _settings;
    private readonly PlayerViewModel _player;
    private readonly Dispatcher _ui;
    private readonly HashSet<string> _ids = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<SourceOption> Sources { get; } = [.. FixedSources];
    private static SourceOption[] FixedSources =>
        [new(FeedSource.Library, "My Library", null), new(FeedSource.Search, "Search all of Suno", null)];
    public SearchRank[] SearchRanks => SearchRank.All;
    public ObservableCollection<CloudClip> Clips { get; } = [];
    public ObservableCollection<LogLine> Activity { get; } = [];
    public ClipKind[] Kinds { get; } = Enum.GetValues<ClipKind>();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSearch)), NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private SourceOption _selectedSource;
    [ObservableProperty] private string _searchTerm = "";
    [ObservableProperty] private SearchRank _searchRank = SearchRank.All[0];
    public bool IsSearch => SelectedSource.Source.Kind == SourceKind.Search;
    private const int SearchPages = 5; // ponytail: 100 results per Fetch; Start page (Settings) reaches further
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StopCommand))] private bool _isBusy;
    [ObservableProperty] private string _status = "Pick a source, then Fetch to browse or Sync to download what's new.";
    [ObservableProperty] private string _counts = Tagline;
    private const string Tagline = "Browse, stream and download your songs";

    // Filter (mirrors Settings.Filter; every change is saved)
    [ObservableProperty] private bool _likedOnly;
    [ObservableProperty] private bool _hideDisliked;
    [ObservableProperty] private bool _hideStems;
    [ObservableProperty] private bool _stemsOnly;
    [ObservableProperty] private bool _hideStudioClips;
    [ObservableProperty] private bool _publicOnly;
    [ObservableProperty] private bool _includeTrashed;
    [ObservableProperty] private ClipKind _kind;
    [ObservableProperty] private string _tagsInclude;
    [ObservableProperty] private string _tagsExclude;
    [ObservableProperty] private string _search;

    public event Action<string>? Toast;
    public event Action? SignInNeeded;
    public event Action? Downloaded;

    public CloudViewModel(SunoClient suno, Downloader downloader, LibraryIndex index, Settings settings,
        PlayerViewModel player, Dispatcher ui)
    {
        (_suno, _downloader, _index, _settings, _player, _ui) = (suno, downloader, index, settings, player, ui);
        _selectedSource = Sources[0];
        var f = settings.Filter;
        (_likedOnly, _hideDisliked, _hideStems, _stemsOnly) = (f.LikedOnly, f.HideDisliked, f.HideStems, f.StemsOnly);
        (_hideStudioClips, _publicOnly, _includeTrashed, _kind) = (f.HideStudioClips, f.PublicOnly, f.IncludeTrashed, f.Kind);
        (_tagsInclude, _tagsExclude, _search) = (f.TagsInclude, f.TagsExclude, f.Search);

        player.TrackChanged += item => { foreach (var c in Clips) c.IsPlaying = c.Is(item); };
        Log.Written += (level, msg) => _ui.InvokeAsync(() =>
        {
            Activity.Insert(0, new LogLine(DateTime.Now.ToString("HH:mm:ss"), level, msg));
            if (Activity.Count > 300) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    public ClipFilter Filter => new()
    {
        LikedOnly = LikedOnly, HideDisliked = HideDisliked, HideStems = HideStems, StemsOnly = StemsOnly,
        HideStudioClips = HideStudioClips, PublicOnly = PublicOnly, IncludeTrashed = IncludeTrashed, Kind = Kind,
        TagsInclude = TagsInclude, TagsExclude = TagsExclude, Search = Search,
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(LikedOnly) or nameof(HideDisliked) or nameof(HideStems) or nameof(StemsOnly)
            or nameof(HideStudioClips) or nameof(PublicOnly) or nameof(IncludeTrashed) or nameof(Kind)
            or nameof(TagsInclude) or nameof(TagsExclude) or nameof(Search))
            _settings.Filter = Filter;
    }

    [RelayCommand]
    private Task LoadSourcesAsync() => RunAsync("Loading workspaces and playlists…", async ct =>
    {
        var workspaces = await _suno.GetWorkspacesAsync(ct);
        var playlists = await _suno.GetPlaylistsAsync(ct);
        var keep = SelectedSource;
        Sources.Clear();
        foreach (var f in FixedSources) Sources.Add(f);
        foreach (var w in workspaces) Sources.Add(w with { Label = $"Workspace · {w.Label}" });
        foreach (var p in playlists) Sources.Add(p with { Label = $"Playlist · {p.Label}" });
        SelectedSource = Sources.FirstOrDefault(s => s.Source == keep.Source) ?? Sources[0];
        Status = $"{workspaces.Count} workspaces, {playlists.Count} playlists.";
    });

    [RelayCommand] private Task FetchAsync() => ScanAsync(download: false);

    /// <summary>Sync downloads everything new: pointless over all of Suno, so not offered for search.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncAsync() => ScanAsync(download: true);
    private bool CanSync => !IsSearch;

    [RelayCommand]
    private Task DownloadSelectedAsync()
    {
        var picked = Clips.Where(c => c.IsSelected && !c.OnDisk).ToList();
        if (picked.Count == 0) { Toast?.Invoke("Select some songs that aren't on disk yet"); return Task.CompletedTask; }
        return RunAsync($"Downloading {picked.Count} songs…", ct => DownloadPipelineAsync(async w =>
        {
            foreach (var c in picked) await w.WriteAsync(c, ct);
        }, ct));
    }

    [RelayCommand]
    private void SelectNew()
    {
        foreach (var c in Clips) c.IsSelected = !c.OnDisk && !c.Info.AudioLocked;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var c in Clips) c.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop()
    {
        _cts?.Cancel();
        Status = "Stopping…";
    }

    /// <summary>Stream from Suno, or play the local copy when it's already downloaded.</summary>
    [RelayCommand]
    private void Play(CloudClip? clip)
    {
        if (clip is null) return;
        var local = _index.Tracks.Where(t => t.SunoId is not null)
            .GroupBy(t => t.SunoId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var queue = Clips.Select(c => local.TryGetValue(c.Info.Id, out var t) ? t.ToPlayItem() : c.ToPlayItem())
            .OfType<PlayItem>().ToList();
        var start = queue.FindIndex(p => p.Key == clip.Info.Id);
        if (start < 0) { Toast?.Invoke("Suno has no audio for this clip yet"); return; }
        _player.PlayQueue(queue, start);
    }

    private Task ScanAsync(bool download) => RunAsync(download ? "Syncing…" : "Fetching…", async ct =>
    {
        Clips.Clear();
        _ids.Clear();
        var onDisk = _index.SunoIds();
        var source = IsSearch
            ? SelectedSource with { Source = SelectedSource.Source with { Id = SearchTerm, Rank = SearchRank.Key },
                                    Label = SearchTerm.Trim() is { Length: > 0 } t ? $"search \"{t}\"" : "public songs" }
            : SelectedSource;
        var filter = IsSearch ? Filter with { Search = "" } : Filter; // search: the server matched the term, the filter box is hidden
        var opt = new ScanOptions(_settings.StartPage, IsSearch ? SearchPages : _settings.MaxPages, download && _settings.SmartResume,
            Settings.SmartResumePages(onDisk.Count), TimeSpan.FromSeconds(download ? 1 : 0.3));

        async Task Scan(ChannelWriter<CloudClip>? sink)
        {
            var pages = await FeedScan.RunAsync(_suno.GetPageAsync, source.Source, filter, opt, onDisk, (page, kept) =>
            {
                foreach (var info in kept.Where(k => _ids.Add(k.Id)))
                {
                    var row = new CloudClip(info, onDisk.Contains(info.Id), AnalyticsCache.Find(_settings.DownloadFolder, info.Id));
                    row.IsPlaying = row.Is(_player.Current);
                    Clips.Add(row);
                    if (sink is not null && !row.OnDisk && !info.AudioLocked) sink.TryWrite(row);
                }
                Status = $"Page {page} · {Clips.Count} songs";
                UpdateCounts();
            }, ct);
            Log.Info($"Scanned {pages} pages of {source.Label}: {Clips.Count} songs.");
        }

        if (download) await DownloadPipelineAsync(Scan, ct);
        else await Scan(null);
        Status = download ? "Sync finished." : $"{Clips.Count} songs. Play any of them, or select and download.";
    });

    /// <summary>Producer writes clips; up to N workers download them while the producer keeps scanning.</summary>
    private async Task DownloadPipelineAsync(Func<ChannelWriter<CloudClip>, Task> produce, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CloudClip>();
        var limiter = new RateLimiter(TimeSpan.FromSeconds(_settings.DownloadDelaySeconds));
        var sourceName = SelectedSource.Source.Kind == SourceKind.Library ? null : SelectedSource.Source.Name;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_settings.ParallelDownloads, 1, 8), CancellationToken = ct };
        var ok = 0;
        var workers = Parallel.ForEachAsync(channel.Reader.ReadAllAsync(ct), options, async (clip, c) =>
        {
            if (await DownloadOneAsync(clip, sourceName, limiter, c)) Interlocked.Increment(ref ok);
        });
        try { await produce(channel.Writer); }
        finally { channel.Writer.Complete(); }
        await workers;
        if (ok > 0)
        {
            Toast?.Invoke($"Downloaded {ok} song{(ok == 1 ? "" : "s")}");
            Downloaded?.Invoke();
        }
    }

    private async Task<bool> DownloadOneAsync(CloudClip clip, string? sourceName, RateLimiter limiter, CancellationToken ct)
    {
        await _ui.InvokeAsync(() => { clip.IsWorking = true; clip.Failed = false; clip.Error = null; clip.Status = "Downloading"; clip.Progress = 0; });
        var progress = new Progress<double>(p => _ui.InvokeAsync(() => clip.Progress = p));
        try
        {
            var path = await _downloader.DownloadAsync(clip.Info, _settings, sourceName, limiter, progress, ct);
            Log.Success($"✓ {clip.Title} → {Path.GetFileName(path)}");
            await _ui.InvokeAsync(() => { clip.OnDisk = true; clip.Status = "Downloaded"; clip.Progress = 1; UpdateCounts(); });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _ui.InvokeAsync(() => clip.Status = "Stopped");
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Error($"Failed: {clip.Title} — {e.Message}");
            await _ui.InvokeAsync(() =>
            {
                clip.Failed = true;
                clip.Error = $"Download failed: {e.Message}";
                clip.Status = e is HttpRequestException { StatusCode: { } sc } ? $"Failed · {(int)sc}" : "Failed";
            });
            if (e is SunoAuthException) throw;
            return false;
        }
        finally
        {
            await _ui.InvokeAsync(() => clip.IsWorking = false);
        }
    }

    private async Task RunAsync(string busyText, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Status = busyText;
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Status = "Stopped."; }
        catch (SunoAuthException e)
        {
            Status = e.Message;
            SignInNeeded?.Invoke();
        }
        catch (HttpRequestException e)
        {
            Status = $"Network problem: {e.Message}";
            Log.Error(Status);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void UpdateCounts()
    {
        var onDisk = Clips.Count(c => c.OnDisk);
        var cached = Clips.Count(c => c.CachePath is not null);
        var locked = Clips.Count(c => !c.OnDisk && c.Info.AudioLocked) - cached; // cached ones are counted on their own
        Counts = Clips.Count == 0 ? Tagline
            : $"{Clips.Count} songs · {onDisk} on disk · {Clips.Count - onDisk - locked - cached} new" + (locked > 0 ? $" · {locked} stream only" : "")
              + (cached > 0 ? $" · {cached} in analytics cache" : "");
    }

    public ClipInfo? Find(string clipId) =>
        Clips.FirstOrDefault(c => c.Info.Id.Equals(clipId, StringComparison.OrdinalIgnoreCase))?.Info;

    /// <summary>The analyzer saved a full capture of a clip: its row now plays that copy.</summary>
    public void OnCached(string clipId, string path)
    {
        foreach (var c in Clips.Where(c => c.Info.Id.Equals(clipId, StringComparison.OrdinalIgnoreCase))) c.MarkCached(path);
        UpdateCounts();
    }
}
