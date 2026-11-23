using Onus.Core;
using Onus.ViewModels;

namespace Onus.Tests;

public class AnalyticsCacheTests
{
    private const string Id = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "onus-music");

    [Fact]
    public void Caches_under_analytics_by_clip_id()
    {
        AnalyticsCache.PathFor(Root, Id).Should().Be(Path.Combine(Root, "analytics", Id + ".m4a"));
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\x")]
    [InlineData("")]
    public void Ignores_ids_that_are_not_suno_clip_ids(string id) => AnalyticsCache.PathFor(Root, id).Should().BeNull();

    [Fact]
    public void Library_scan_skips_only_the_cache_folder()
    {
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "analytics", Id + ".m4a")).Should().BeTrue();
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "analytics-songs", "a.mp3")).Should().BeFalse();
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "Song.mp3")).Should().BeFalse();
    }

    [Theory]
    [InlineData(152.6, 153.0, true)]
    [InlineData(150.0, 153.0, false)]
    [InlineData(10.0, 0.0, false)] // unknown duration never counts
    public void Complete_means_the_whole_song(double recorded, double duration, bool complete) =>
        AnalyticsCache.IsComplete(recorded, duration).Should().Be(complete);

    [Theory]
    [InlineData(10.0, 10.25, 0.25, false)] // playing on
    [InlineData(10.0, 10.0, 1.0, false)]   // position report lagging a little
    [InlineData(10.0, 60.0, 0.03, true)]   // seek forward
    [InlineData(80.0, 0.0, 0.03, true)]    // Previous: restart
    public void Detects_seeks(double last, double pos, double elapsed, bool jump) =>
        AnalyticsCache.IsJump(last, pos, elapsed).Should().Be(jump);

    [Fact]
    public void Cached_stream_only_row_plays_its_capture()
    {
        var info = new ClipInfo { Id = Id, AudioLocked = true };
        new CloudClip(info, false).Status.Should().Be("Stream only");

        var path = Path.Combine(Root, "analytics", Id + ".m4a");
        var row = new CloudClip(info, false, path);
        row.Status.Should().Be("Analytics cache");
        var item = row.ToPlayItem()!;
        item.Source.Should().Be(new Uri(path));
        item.FilePath.Should().Be(path);

        var later = new CloudClip(info, false);
        later.MarkCached(path);
        later.Status.Should().Be("Analytics cache");
        later.ToPlayItem()!.Source.IsFile.Should().BeTrue();
        new CloudClip(info, true, path).Status.Should().Be("On disk"); // a real download wins
    }

    [Fact]
    public void Row_is_the_current_song_whatever_copy_plays()
    {
        var row = new CloudClip(new ClipInfo { Id = Id, AudioLocked = true }, false);
        row.Is(row.ToPlayItem()).Should().BeTrue();                                  // stream via Suno's player
        row.Is(new PlayItem(Id.ToUpperInvariant(), "", "", new Uri("file:///c:/a.mp3"), null, "", 0, "c:/a.mp3")).Should().BeTrue(); // the download
        row.Is(new PlayItem("7c9e6679-7425-40de-944b-e07fc1f90ae7", "", "", new Uri("file:///c:/b.mp3"), null, "", 0, null)).Should().BeFalse();
        row.Is(null).Should().BeFalse();
    }
}

public class AnalyticsRecorderTests
{
    private const string A = "0f8fad5b-d9cb-469f-a165-70867728950e", B = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    [Fact]
    public void Starts_on_a_clicked_song_even_while_the_old_page_still_reports_its_position()
    {
        var root = Path.Combine(Path.GetTempPath(), "onus-rec-" + Guid.NewGuid().ToString("N"));
        using var rec = new Onus.Services.AnalyticsRecorder(new Onus.Services.AudioTap(), () => root);
        try
        {
            rec.Tick(A, true, true, 0, 180, 0);
            rec.IsRecording.Should().BeTrue();

            rec.Tick(B, true, true, 45, 180, 0.1); // mouse switch mid-song: song A's page reports 0:45 once more
            rec.IsRecording.Should().BeFalse();
            rec.Tick(B, true, true, 0, 200, 0.2);  // song B's page loaded
            rec.IsRecording.Should().BeTrue();

            rec.Tick(B, true, true, 90, 200, 0.3); // seek drops the capture …
            rec.IsRecording.Should().BeFalse();
            rec.Tick(B, true, true, 0, 200, 0.4);  // … back at 0:00 it starts over
            rec.IsRecording.Should().BeTrue();
        }
        finally
        {
            rec.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}

public class WebPlayerMessageTests
{
    [Fact]
    public void Time_message_carries_the_pages_real_volume()
    {
        var m = Onus.Services.SunoWebPlayer.Parse("""{"t":"time","pos":3.5,"dur":120,"playing":true,"vol":0.46}""")!;
        (m.Position, m.Duration, m.Playing, m.Volume).Should().Be((3.5, 120.0, true, 0.46));
        Onus.Services.SunoWebPlayer.Parse("""{"t":"time","vol":7}""")!.Volume.Should().Be(1);   // clamped
        Onus.Services.SunoWebPlayer.Parse("""{"t":"time","vol":"x"}""")!.Volume.Should().BeNull();
    }
}

public class AudioTapTests
{
    [Fact]
    public void Surround_capture_keeps_front_pair_at_full_level()
    {
        // Two 7.1 frames: FL, FR, C, LFE, BL, BR, SL, SR. Windows' own 8→2 downmix would scale FL by ~0.21.
        float[] raw = [0.5f, -0.25f, 9, 9, 9, 9, 9, 9, 0.1f, 0.2f, 9, 9, 9, 9, 9, 9];
        var stereo = new float[4];
        Onus.Services.AudioTap.FrontPair(raw, 8, 2f, stereo);
        stereo.Should().Equal(1f, -0.5f, 0.2f, 0.4f);
    }
}
