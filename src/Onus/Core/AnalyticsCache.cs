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

    /// <summary>The library scan skips the cache: these are analysis copies, not downloads.</summary>
    public static bool IsCachePath(string downloadFolder, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder(downloadFolder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Whole song captured: within half a second of its duration.</summary>
    public static bool IsComplete(double recordedSeconds, double duration) => duration > 0 && recordedSeconds >= duration - 0.5;

    /// <summary>Playback position moved other than by playing on (a seek, or Previous restarting the song).</summary>
    public static bool IsJump(double lastPosition, double position, double elapsed) =>
        Math.Abs(position - (lastPosition + elapsed)) > 2;
}
