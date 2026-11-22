namespace Onus.Core;

public delegate Task<(IReadOnlyList<ClipInfo> Clips, FeedSource Source)> PageFetcher(
    FeedSource source, int page, ClipFilter filter, CancellationToken ct);

public sealed record ScanOptions(int StartPage, int MaxPages, bool SmartResume, int SmartResumePages, TimeSpan PageDelay);

/// <summary>The page loop behind Fetch and Sync, kept free of HTTP so its stop rules are testable.</summary>
public static class FeedScan
{
    /// <summary>Walk pages from StartPage, hand each page's filtered clips to <paramref name="onPage"/>.
    /// Stops at: an empty page (end of feed — SunoSync looped forever here), MaxPages scanned (0 = no limit),
    /// a non-paging source (playlists), or Smart Resume: after new songs were seen, N pages in a row with none.</summary>
    public static async Task<int> RunAsync(PageFetcher fetch, FeedSource source, ClipFilter filter, ScanOptions opt,
        IReadOnlySet<string> onDisk, Action<int, IReadOnlyList<ClipInfo>> onPage, CancellationToken ct)
    {
        var page = Math.Max(1, opt.StartPage);
        var scanned = 0;
        var foundNew = false;
        var dryPages = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (opt.MaxPages > 0 && scanned >= opt.MaxPages) break;

            var (clips, effective) = await fetch(source, page, filter, ct);
            source = effective;
            scanned++;
            if (clips.Count == 0) break;

            var kept = clips.Where(filter.Matches).ToList();
            onPage(page, kept);

            if (kept.Any(c => !onDisk.Contains(c.Id))) { foundNew = true; dryPages = 0; }
            else if (foundNew) dryPages++;

            if (opt.SmartResume && foundNew && dryPages >= opt.SmartResumePages)
            {
                Log.Info($"Smart Resume: nothing new in the last {dryPages} pages, stopping.");
                break;
            }
            if (!source.Pages) break;
            page++;
            if (opt.PageDelay > TimeSpan.Zero) await Task.Delay(opt.PageDelay, ct);
        }
        return scanned;
    }
}
