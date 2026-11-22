using Onus.Core;

namespace Onus.Services;

/// <summary>Scans the download folder for audio files. Tags are only re-read for files whose
/// size or mtime changed, so a rescan of thousands of songs takes a moment, not minutes.</summary>
public sealed class LibraryIndex
{
    private Dictionary<string, Track> _cache;

    public LibraryIndex()
    {
        _cache = JsonStore.Load<List<Track>>(AppPaths.LibraryCache)
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<Track> Tracks => _cache.Values;

    /// <summary>Suno ids already on disk: what the Cloud page marks as downloaded and sync skips.</summary>
    public HashSet<string> SunoIds() =>
        _cache.Values.Select(t => t.SunoId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task<List<Track>> ScanAsync(string folder, CancellationToken ct = default) => Task.Run(() =>
    {
        var next = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!TagIO.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (AnalyticsCache.IsCachePath(folder, path)) continue;
                var fi = new FileInfo(path);
                next[path] = _cache.TryGetValue(path, out var hit) && hit.Size == fi.Length && hit.Modified == fi.LastWriteTimeUtc
                    ? hit
                    : TagIO.Read(path);
            }
        }
        _cache = next;
        try { JsonStore.Save(AppPaths.LibraryCache, next.Values.ToList()); }
        catch (IOException e) { Log.Warn($"Library cache not saved: {e.Message}"); }
        return next.Values.OrderByDescending(t => t.Modified).ToList();
    }, ct);

    /// <summary>Re-read one file after an edit; returns the fresh track.</summary>
    public Track Refresh(string path) => _cache[path] = TagIO.Read(path);

    public void Forget(string path) => _cache.Remove(path);
}
