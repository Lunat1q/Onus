using System.Text.RegularExpressions;

namespace Onus.Core;

public static partial class FileNames
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex BadChars();

    public static string Sanitize(string name, int maxLength = 200)
    {
        var safe = BadChars().Replace(name, "_").Trim(' ', '.');
        if (safe.Length > maxLength) safe = safe[..maxLength].TrimEnd(' ', '.');
        return safe.Length == 0 ? "Untitled" : safe;
    }

    /// <summary>"Song.mp3" → "Song v2.mp3" → "Song v3.mp3"… (same scheme as SunoSync, so libraries mix cleanly).</summary>
    public static string Unique(string path, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} v{n}{ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>Folder a clip lands in: playlist subfolder beats month folder; stems nest under their song.</summary>
    public static string TargetFolder(string root, ClipInfo clip, Settings s, string? sourceName)
    {
        var dir = root;
        if (s.PlaylistSubfolder && !string.IsNullOrWhiteSpace(sourceName)) dir = Path.Combine(dir, Sanitize(sourceName));
        else if (s.OrganizeByMonth && clip.Month is { } month) dir = Path.Combine(dir, month);
        if (s.StemsInTrackFolder && clip.IsStem) dir = Path.Combine(dir, Sanitize(clip.BaseTitle));
        return dir;
    }

    public static string ExtensionFromUrl(string url, string fallback)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            return string.IsNullOrEmpty(ext) ? fallback : ext.ToLowerInvariant();
        }
        catch (UriFormatException) { return fallback; }
    }
}
