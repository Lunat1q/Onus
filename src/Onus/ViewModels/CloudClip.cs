using CommunityToolkit.Mvvm.ComponentModel;
using Onus.Core;

namespace Onus.ViewModels;

/// <summary>A row on the Suno page: one remote clip plus its download state. <paramref name="cachePath"/> = a complete
/// analytics capture of a stream-only clip (<see cref="AnalyticsCache"/>), played instead of suno.com's player.</summary>
public sealed partial class CloudClip(ClipInfo info, bool onDisk, string? cachePath = null) : ObservableObject
{
    public const string CachedMessage = "Analytics cache: captured in full while the analyzer listened. Plays from this copy, without Suno's player.";

    public ClipInfo Info { get; } = info;
    public string? CachePath { get; private set; } = onDisk ? null : cachePath;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _onDisk = onDisk;
    [ObservableProperty] private string _status = onDisk ? "On disk" : cachePath is not null ? "Analytics cache" : info.AudioLocked ? "Stream only" : "New";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _error =onDisk ? null : cachePath is not null ? CachedMessage : info.AudioLocked ? Services.Downloader.LockedMessage : null;

    public string Title => TitleText.Display(Info.Title, Info.Prompt);
    public string Subtitle => Info.Tags.Length > 0 ? Info.Tags : Info.Description;
    public string DurationText => TitleText.Duration(Info.Duration);
    public string Created => DateTime.TryParse(Info.CreatedAt, out var d) ? d.ToLocalTime().ToString("MMM d, yyyy") : "";
    public string? Art => Info.ImageUrl;
    public bool CanStream => Info.AudioUrl is not null || Info.AudioLocked;
    public bool IsLiked => Info.IsLiked;

    /// <summary>The player's item is this clip, whether it plays the stream, the capture or the downloaded file.</summary>
    public bool Is(PlayItem? item) => item is not null && Info.Id.Equals(item.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The analyzer just finished capturing this clip.</summary>
    public void MarkCached(string path)
    {
        if (OnDisk) return;
        CachePath = path;
        Status = "Analytics cache";
        Error = CachedMessage;
    }

    /// <summary>Cached clips play their capture; other locked clips via their suno.com page (<see cref="Services.SunoWebPlayer"/>).</summary>
    public PlayItem? ToPlayItem() => !CanStream ? null : new PlayItem(
        Info.Id, Title, Info.Artist.Length > 0 ? Info.Artist : "Suno", new Uri(CachePath ?? Info.AudioUrl ?? Info.SongPage),
        Info.ImageLargeUrl ?? Info.ImageUrl, Info.Lyrics, Info.Duration, CachePath);
}

public sealed record LogLine(string Time, string Level, string Message);
