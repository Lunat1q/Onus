using System.Diagnostics;
using System.Text;
using Onus.Core;

namespace Onus.Tests;

public class FeedScanTests
{
    private static readonly ClipFilter All = new() { HideDisliked = false, HideStems = false, HideStudioClips = false };
    private static readonly ScanOptions Plain = new(1, 0, false, 2, TimeSpan.Zero);

    private static IReadOnlyList<ClipInfo> Page(params string[] ids) => ids.Select(i => new ClipInfo { Id = i }).ToList();

    private static PageFetcher Pages(params IReadOnlyList<ClipInfo>[] pages) => (src, page, _, _) =>
        Task.FromResult((page - 1 < pages.Length ? pages[page - 1] : (IReadOnlyList<ClipInfo>)[], src));

    private static async Task<List<int>> Run(PageFetcher fetch, ScanOptions opt, FeedSource? source = null, params string[] onDisk)
    {
        var seen = new List<int>();
        await FeedScan.RunAsync(fetch, source ?? FeedSource.Library, All, opt, onDisk.ToHashSet(), (p, _) => seen.Add(p), CancellationToken.None);
        return seen;
    }

    [Fact]
    public async Task Stops_at_the_first_empty_page() // SunoSync kept requesting pages forever here
    {
        (await Run(Pages(Page("a"), Page("b")), Plain)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Max_pages_counts_from_the_start_page()
    {
        var fetch = Pages(Page("a"), Page("b"), Page("c"), Page("d"));
        (await Run(fetch, Plain with { StartPage = 2, MaxPages = 2 })).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Smart_resume_stops_after_dry_pages_once_new_songs_were_seen()
    {
        var fetch = Pages(Page("old1"), Page("new"), Page("old2"), Page("old3"), Page("old4"), Page("x"));
        (await Run(fetch, Plain with { SmartResume = true }, null, "old1", "old2", "old3", "old4"))
            .Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Smart_resume_keeps_scanning_when_nothing_new_was_seen_yet()
    {
        var fetch = Pages(Page("o1"), Page("o2"), Page("o3"), Page("n"));
        (await Run(fetch, Plain with { SmartResume = true }, null, "o1", "o2", "o3")).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Playlists_are_fetched_once()
    {
        (await Run(Pages(Page("a"), Page("b")), Plain, new FeedSource(SourceKind.Playlist, "p"))).Should().Equal(1);
    }

    [Fact]
    public async Task Fetcher_may_switch_the_source_and_the_scan_follows_it()
    {
        PageFetcher fetch = (src, page, _, _) =>
            Task.FromResult((Page($"p{page}"), src with { Kind = SourceKind.Playlist }));
        (await Run(fetch, Plain, new FeedSource(SourceKind.Workspace, "w"))).Should().Equal(1);
    }
}

public class FileNamesTests
{
    [Theory]
    [InlineData("a/b:c?", "a_b_c_")]
    [InlineData("  .dots. ", "dots")]
    [InlineData("...", "Untitled")]
    public void Sanitizes(string raw, string safe) => FileNames.Sanitize(raw).Should().Be(safe);

    [Fact]
    public void Unique_appends_v2_then_v3()
    {
        var taken = new HashSet<string> { Path.Combine("d", "Song.mp3"), Path.Combine("d", "Song v2.mp3") };
        FileNames.Unique(Path.Combine("d", "Song.mp3"), taken.Contains).Should().Be(Path.Combine("d", "Song v3.mp3"));
    }

    [Fact]
    public void Playlist_folder_beats_month_and_stems_nest_under_song()
    {
        var s = new Settings { DownloadFolder = "root", OrganizeByMonth = true, PlaylistSubfolder = true, StemsInTrackFolder = true };
        var stem = new ClipInfo { Id = "x", Title = "Song (Drums)", CreatedAt = "2026-05-01" };
        FileNames.TargetFolder("root", stem, s, "Mix: 1").Should().Be(Path.Combine("root", "Mix_ 1", "Song"));
        FileNames.TargetFolder("root", stem, s, null).Should().Be(Path.Combine("root", "2026-05", "Song"));
    }

    [Fact]
    public void Extension_comes_from_url_path() =>
        FileNames.ExtensionFromUrl("https://cdn1.suno.ai/abc.WAV?x=1", ".mp3").Should().Be(".wav");
}

public class TitleTextTests
{
    [Theory]
    [InlineData("Dark Techno, fast tempo", "Dark Techno")]
    [InlineData("indie rock with emotional vocals", "indie rock")]
    [InlineData("one two three four five", "one two three four")]
    public void Genre_takes_the_first_words(string prompt, string genre) => TitleText.Genre(prompt).Should().Be(genre);

    [Theory]
    [InlineData("house, 124 BPM", 124)]
    [InlineData("10 bpm drone", null)]
    [InlineData("no tempo", null)]
    public void Bpm_is_extracted_when_plausible(string text, int? bpm) => TitleText.Bpm(text).Should().Be(bpm);

    [Fact]
    public void Uuid_titles_fall_back_to_the_prompt() =>
        TitleText.Display("0f8fad5b-d9cb-469f-a165-70867728950e", "glowing city lights at night again")
            .Should().Be("Glowing city lights at night");

    [Fact]
    public void List_artifacts_become_untitled_with_genre() =>
        TitleText.Display("['control: bouncy, crisp']").Should().Be("Untitled (control: bouncy)");

    [Fact]
    public void Bracketed_titles_are_unwrapped() => TitleText.Display("['My Song']").Should().Be("My Song");
}

public class JwtTests
{
    private static string Token(string payload) =>
        "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void Reads_exp() => Jwt.Expiry(Token("""{"exp":1700000000}""")).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    [InlineData(null)]
    public void Garbage_has_no_expiry(string? token) => Jwt.Expiry(token).Should().BeNull();

    [Fact]
    public void Clean_strips_pasted_junk() => Jwt.Clean(" \"eyJ.abc…\"\n").Should().Be("eyJ.abc");
}

public class QueueNavTests
{
    private static readonly Random Rng = new(1);

    [Fact]
    public void Next_stops_at_end_unless_repeat_all()
    {
        QueueNav.Next(2, 3, false, RepeatMode.Off, true, Rng).Should().Be(-1);
        QueueNav.Next(2, 3, false, RepeatMode.All, true, Rng).Should().Be(0);
    }

    [Fact]
    public void Repeat_one_loops_only_when_the_track_ends_by_itself()
    {
        QueueNav.Next(1, 3, false, RepeatMode.One, auto: true, Rng).Should().Be(1);
        QueueNav.Next(1, 3, false, RepeatMode.One, auto: false, Rng).Should().Be(2);
    }

    [Fact]
    public void Shuffle_never_repeats_the_current_track()
    {
        for (var i = 0; i < 50; i++) QueueNav.Next(0, 2, true, RepeatMode.Off, true, Rng).Should().Be(1);
    }

    [Fact]
    public void Previous_restarts_after_three_seconds()
    {
        QueueNav.Previous(2, 3, 10, RepeatMode.Off).Should().Be(2);
        QueueNav.Previous(2, 3, 1, RepeatMode.Off).Should().Be(1);
        QueueNav.Previous(0, 3, 1, RepeatMode.All).Should().Be(2);
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_with_enums_as_names()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Settings { Format = AudioFormat.Wav }, JsonStore.Options);
        json.Should().Contain("\"Wav\"");
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(json, JsonStore.Options)!;
        back.Format.Should().Be(AudioFormat.Wav);
        back.Filter.HideStems.Should().BeTrue();
    }

    [Theory]
    [InlineData(50, 2)]
    [InlineData(500, 5)]
    [InlineData(4999, 10)]
    [InlineData(9000, 20)]
    public void Smart_resume_threshold_scales_with_library(int size, int pages) => Settings.SmartResumePages(size).Should().Be(pages);
}

public class RateLimiterTests
{
    [Fact]
    public async Task Spaces_out_calls()
    {
        var limiter = new RateLimiter(TimeSpan.FromMilliseconds(120));
        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(100);
    }
}
