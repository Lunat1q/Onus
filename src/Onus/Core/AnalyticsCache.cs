namespace Onus.Core;

/// <summary>
/// Where captured stream-only songs live (<c>{download folder}/analytics/{clip id}.m4a</c>) and when a capture
/// counts as complete. A complete capture plays locally instead of through suno.com's player.
/// </summary>
public static class AnalyticsCache
{
    public const string FolderName = "analytics";

    public static string Folder(string downloadFolder) => Path.Combine(downloadFolder, FolderName);

    /// <summary>Null for anything that isn't a Suno clip id (a GUID), so API data can't steer the file path.</summary>
    public static string? PathFor(string downloadFolder, string clipId) =>
        Guid.TryParse(clipId, out var g) ? Path.Combine(Folder(downloadFolder), g.ToString("D") + ".m4a") : null;

    public static string? Find(string downloadFolder, string clipId) =>
        PathFor(downloadFolder, clipId) is { } p && File.Exists(p) ? p : null;

    /// <summary>Inside the cache folder: listed in the library, but not a download (Sync and "On disk" ignore it).</summary>
    public static bool IsCachePath(string downloadFolder, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder(downloadFolder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A capture still being encoded (renamed to {id}.m4a once tagged).</summary>
    public const string EncodingSuffix = ".encoding.m4a";

    /// <summary>The library lists captures too, except where a real download of the same clip exists.</summary>
    public static List<Track> ForLibrary(string downloadFolder, IReadOnlyCollection<Track> tracks)
    {
        var downloaded = tracks.Where(t => !IsCachePath(downloadFolder, t.Path)).Select(t => t.SunoId).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tracks.Where(t => t.SunoId is null || !downloaded.Contains(t.SunoId) || !IsCachePath(downloadFolder, t.Path)).ToList();
    }

    /// <summary>Present once the folder only holds captures made at the right level.</summary>
    public const string LevelMarker = ".level-v3";

    /// <summary>Drops every capture the folder held before the last level fix; the songs record again on the next
    /// listen. v2: Onus 1.0 captures ~13.5 dB low on surround devices (Windows' 8→2 downmix). v3: a clipped burst at
    /// the start (the page played its first ms at full volume while the gain was set for the slider). Runs once per
    /// folder (marker file). Call when nothing is encoding into it.</summary>
    // ponytail: bump the marker name if a future capture fix needs another purge.
    public static void PurgeStale(string downloadFolder)
    {
        var dir = Folder(downloadFolder);
        if (!Directory.Exists(dir) || File.Exists(Path.Combine(dir, LevelMarker))) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.m4a").Concat(Directory.EnumerateFiles(dir, ".level-*"))) File.Delete(f);
            File.WriteAllText(Path.Combine(dir, LevelMarker), "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Analytics cache: purge failed: {e.Message}"); }
    }

    /// <summary>Whole song captured: within half a second of its duration.</summary>
    public static bool IsComplete(double recordedSeconds, double duration) => duration > 0 && recordedSeconds >= duration - 0.5;

    /// <summary>Playback position moved other than by playing on (a seek, or Previous restarting the song).</summary>
    public static bool IsJump(double lastPosition, double position, double elapsed) =>
        Math.Abs(position - (lastPosition + elapsed)) > 2;
}
