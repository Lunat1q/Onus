using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Onus.Core;
using Onus.Services;

namespace Onus.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    public static readonly string[] SortOptions = ["Newest", "Title", "Artist", "Duration", "BPM"];

    private readonly LibraryIndex _index;
    private readonly TagStore _tags;
    private readonly PromptStore _prompts;
    private readonly Settings _settings;
    private readonly PlayerViewModel _player;
    private CancellationTokenSource? _scan;

    public ObservableCollection<Track> Tracks { get; private set; } = [];

    /// <summary>Rebuilt on every load (see <see cref="Load"/>), so the list never sees thousands of single Adds.</summary>
    [ObservableProperty] private ListCollectionView _view = new(new List<Track>());

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sortBy = "Newest";
    [ObservableProperty] private string? _tagFilter; // null = all, else keep/star/trash
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _summary = "";

    public event Action<string>? Toast;
    public event Action? Changed;

    public LibraryViewModel(LibraryIndex index, TagStore tags, PromptStore prompts, Settings settings, PlayerViewModel player)
    {
        _index = index;
        _tags = tags;
        _prompts = prompts;
        _settings = settings;
        _player = player;
        player.TrackChanged += item =>
        {
            foreach (var t in Tracks) t.IsPlaying = item?.FilePath is { } p && string.Equals(p, t.Path, StringComparison.OrdinalIgnoreCase);
        };
        player.TagChanged += (key, tag) => { foreach (var t in Tracks.Where(t => t.TagKey == key)) t.Tag = tag; View.Refresh(); };

        Load(index.Tracks.OrderByDescending(t => t.Modified)); // instant start from cache, then rescan
    }

    public string Folder => _settings.DownloadFolder;

    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnTagFilterChanged(string? value) => RefreshView();
    partial void OnSortByChanged(string value) => ApplySort();

    [RelayCommand]
    public async Task RescanAsync()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        IsScanning = true;
        try
        {
            Load(await _index.ScanAsync(_settings.DownloadFolder, _scan.Token));
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void Play(Track? track)
    {
        var visible = View.Cast<Track>().ToList();
        if (visible.Count == 0) return;
        _player.PlayQueue(visible.Select(t => t.ToPlayItem()).ToList(), track is null ? 0 : Math.Max(0, visible.IndexOf(track)));
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        _player.Shuffle = true;
        var visible = View.Cast<Track>().ToList();
        if (visible.Count > 0) _player.PlayQueue(visible.Select(t => t.ToPlayItem()).ToList(), Random.Shared.Next(visible.Count));
    }

    [RelayCommand] private void Keep(object? selection) => SetTag("keep", selection);
    [RelayCommand] private void Star(object? selection) => SetTag("star", selection);
    [RelayCommand] private void Trash(object? selection) => SetTag("trash", selection);

    private void SetTag(string tag, object? selection)
    {
        foreach (var t in Selection(selection))
        {
            t.Tag = _tags.Toggle(t.TagKey, tag);
            _player.OnExternalTag(t.TagKey, t.Tag);
        }
        View.Refresh();
    }

    [RelayCommand]
    private void ShowInExplorer(Track? t)
    {
        if (t is not null) Process.Start("explorer.exe", $"/select,\"{t.Path}\"");
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(_settings.DownloadFolder);
        Process.Start("explorer.exe", $"\"{_settings.DownloadFolder}\"");
    }

    /// <summary>Copies the files themselves (CF_HDROP), so they paste into Explorer or a DAW.</summary>
    [RelayCommand]
    private void CopyFiles(object? selection)
    {
        var files = new StringCollection();
        files.AddRange(Selection(selection).Select(t => t.Path).ToArray());
        if (files.Count == 0) return;
        Clipboard.SetFileDropList(files);
        Toast?.Invoke(files.Count == 1 ? "File copied — paste it anywhere" : $"{files.Count} files copied");
    }

    /// <summary>Save as {ext}: one song → a file dialog named after the title; several → a folder, names from titles.
    /// The folder is remembered.</summary>
    public async Task SaveAsAsync(IReadOnlyList<Track> tracks, string ext)
    {
        if (tracks.Count == 0) return;
        var folder = Directory.Exists(_settings.ExportFolder) ? _settings.ExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        List<(Track Track, string Target)> jobs;
        if (tracks.Count == 1)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = folder,
                FileName = FileNames.Sanitize(tracks[0].Title),
                Filter = "MP3|*.mp3|WAV|*.wav|M4A (AAC)|*.m4a",
                FilterIndex = Array.IndexOf(Exporter.Formats, ext) + 1,
                AddExtension = true,
            };
            if (dialog.ShowDialog() != true) return;
            var target = Exporter.Formats.Contains(Path.GetExtension(dialog.FileName).ToLowerInvariant())
                ? dialog.FileName : dialog.FileName + Exporter.Formats[dialog.FilterIndex - 1];
            jobs = [(tracks[0], target)];
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = folder, Title = $"Save {tracks.Count} songs as {ext[1..].ToUpperInvariant()}" };
            if (dialog.ShowDialog() != true) return;
            jobs = [];
            foreach (var t in tracks) // one at a time, so two songs with the same title get "v2" instead of colliding
            {
                var target = FileNames.Unique(Path.Combine(dialog.FolderName, FileNames.Sanitize(t.Title) + ext),
                    p => File.Exists(p) || jobs.Exists(j => string.Equals(j.Target, p, StringComparison.OrdinalIgnoreCase)));
                jobs.Add((t, target));
            }
        }
        _settings.ExportFolder = Path.GetDirectoryName(jobs[0].Target) ?? folder;

        Toast?.Invoke(jobs.Count == 1 ? $"Saving \"{jobs[0].Track.Title}\"…" : $"Saving {jobs.Count} songs…");
        var failed = 0;
        foreach (var (t, target) in jobs)
        {
            try { await Task.Run(() => Exporter.Export(t, target)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                failed++;
                Log.Warn($"Save as: {Path.GetFileName(t.Path)} → {Path.GetFileName(target)} failed: {e.Message}");
            }
        }
        Toast?.Invoke(failed == 0 ? (jobs.Count == 1 ? $"Saved {Path.GetFileName(jobs[0].Target)}" : $"Saved {jobs.Count} songs")
                                  : $"{failed} of {jobs.Count} couldn't be saved (see log)");
    }

    [RelayCommand]
    private void SaveToVault(Track? t)
    {
        if (t is null) return;
        var text = t.StyleTags.Length > 0 ? t.StyleTags : t.Prompt;
        if (text.Length == 0) { Toast?.Invoke("This song has no prompt to save"); return; }
        _prompts.All.Add(new Prompt { Title = t.Title, Text = text, Tags = t.Genre });
        _prompts.Save();
        Toast?.Invoke($"Saved \"{t.Title}\" to the Vault");
    }

    /// <summary>Recycle Bin, not delete: a mis-click is recoverable. Sidecars (.txt/.jpg) go too.</summary>
    [RelayCommand]
    private void Recycle(object? selection)
    {
        var doomed = Selection(selection).ToList();
        if (doomed.Count == 0) return;
        var what = doomed.Count == 1 ? $"\"{doomed[0].Title}\"" : $"{doomed.Count} songs";
        if (MessageBox.Show($"Move {what} to the Recycle Bin?", "Onus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var t in doomed)
        {
            try
            {
                foreach (var f in new[] { t.Path, Path.ChangeExtension(t.Path, ".txt"), Path.ChangeExtension(t.Path, ".jpg") }.Where(File.Exists))
                    FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _index.Forget(t.Path);
                Tracks.Remove(t);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                Toast?.Invoke($"Couldn't remove {t.Title}: {e.Message}");
            }
        }
        UpdateSummary();
        Changed?.Invoke();
    }

    /// <summary>After the metadata editor saved: re-read the file and swap the row.</summary>
    public void Reload(Track old)
    {
        var fresh = _index.Refresh(old.Path);
        fresh.Tag = _tags.Get(fresh.TagKey);
        var i = Tracks.IndexOf(old);
        if (i >= 0) Tracks[i] = fresh;
    }

    private void Load(IEnumerable<Track> tracks)
    {
        // Fresh collection + view instead of Clear/Add under DeferRefresh: adding while a refresh is deferred
        // left the ListView showing 1 row of 4 (see domain.md, operational lessons).
        var playing = _player.Current?.FilePath;
        var list = new ObservableCollection<Track>(tracks);
        foreach (var t in list)
        {
            t.Tag = _tags.Get(t.TagKey);
            t.IsPlaying = string.Equals(t.Path, playing, StringComparison.OrdinalIgnoreCase);
        }
        Tracks = list;
        OnPropertyChanged(nameof(Tracks));
        View = new ListCollectionView(list) { Filter = o => o is Track t && Matches(t) };
        ApplySort();
        UpdateSummary();
    }

    private bool Matches(Track t)
    {
        if (TagFilter is not null && t.Tag != TagFilter) return false;
        if (TagFilter is null && t.Tag == "trash") return false; // trash only shows under its own chip
        var q = Search.Trim();
        return q.Length == 0
               || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)
               || t.StyleTags.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(SortBy switch
            {
                "Title" => new SortDescription(nameof(Track.Title), ListSortDirection.Ascending),
                "Artist" => new SortDescription(nameof(Track.Artist), ListSortDirection.Ascending),
                "Duration" => new SortDescription(nameof(Track.Duration), ListSortDirection.Descending),
                "BPM" => new SortDescription(nameof(Track.Bpm), ListSortDirection.Descending),
                _ => new SortDescription(nameof(Track.Modified), ListSortDirection.Descending),
            });
        }
    }

    private void RefreshView()
    {
        View.Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var shown = View.Cast<Track>().Count();
        Summary = shown == Tracks.Count ? $"{Tracks.Count:N0} songs" : $"{shown:N0} of {Tracks.Count:N0} songs";
    }

    private static IEnumerable<Track> Selection(object? selection) => selection switch
    {
        Track t => [t],
        System.Collections.IList list => list.OfType<Track>().ToList(),
        _ => [],
    };
}
