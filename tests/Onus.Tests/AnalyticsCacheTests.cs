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
    public void Cache_folder_is_told_apart_from_downloads()
    {
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "analytics", Id + ".m4a")).Should().BeTrue();
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "analytics-songs", "a.mp3")).Should().BeFalse();
        AnalyticsCache.IsCachePath(Root, Path.Combine(Root, "Song.mp3")).Should().BeFalse();
    }

    [Fact]
    public void Library_lists_captures_unless_the_song_was_downloaded()
    {
        const string other = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
        Track T(string path, string? id) => new() { Path = Path.Combine(Root, path), SunoId = id };
        var captured = T(Path.Combine("analytics", Id + ".m4a"), Id);
        var shadowed = T(Path.Combine("analytics", other + ".m4a"), other);
        var download = T("Song.mp3", other.ToUpperInvariant());
        var local = T("Mine.mp3", null);

        AnalyticsCache.ForLibrary(Root, [captured, shadowed, download, local]).Should().Equal(captured, download, local);
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
    public void Captures_from_before_the_level_fix_are_purged_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "onus-purge-" + Guid.NewGuid().ToString("N"));
        var dir = Directory.CreateDirectory(AnalyticsCache.Folder(root)).FullName;
        try
        {
            var old = Path.Combine(dir, Id + ".m4a");
            File.WriteAllText(old, "capture with a burst at the start");
            File.WriteAllText(Path.Combine(dir, ".level-v2"), "");
            AnalyticsCache.PurgeStale(root);
            File.Exists(old).Should().BeFalse();
            Directory.GetFiles(dir).Select(Path.GetFileName).Should().Equal(AnalyticsCache.LevelMarker);

            File.WriteAllText(old, "good capture");
            AnalyticsCache.PurgeStale(root); // marker present: new captures stay
            File.Exists(old).Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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

public class CaptureGuardTests
{
    [Fact]
    public void Burst_from_a_wrong_gain_is_never_cached()
    {
        Onus.Services.AnalyticsRecorder.OutOfStep([0.5f, -0.99f, 1.1f]).Should().BeFalse(); // loud master, overshoot
        Onus.Services.AnalyticsRecorder.OutOfStep([0.5f, -9f, 0.1f]).Should().BeTrue();     // full-volume ms × gain 9
    }
}

public class WebPlayerMessageTests
{
    [Fact]
    public void Time_message_carries_the_pages_real_volume()
    {
        var m = Onus.Services.SunoWebPlayer.Parse("""{"t":"time","pos":3.5,"dur":120,"playing":true}""")!;
        (m.Position, m.Duration, m.Playing).Should().Be((3.5, 120.0, true));
        Onus.Services.SunoWebPlayer.Parse("""{"t":"time","pos":"x"}""")!.Position.Should().Be(0);
        Onus.Services.SunoWebPlayer.Parse("""[1]""").Should().BeNull();
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

    [Theory]
    [InlineData(1.0, 1f)]
    [InlineData(0.01, 100f)]    // 1 % slider: was capped at ×20 (analysis 14 dB low)
    [InlineData(0.001, 1000f)]
    [InlineData(0.0, 100000f)]  // slider 0: the players run at −100 dB, the tap undoes exactly that
    public void Volume_is_divided_out_down_to_a_whisper(double volume, float gain) =>
        Onus.Services.AudioTap.GainFor(volume).Should().BeApproximately(gain, gain * 1e-5f);

    [Fact]
    public void Gain_rises_only_after_the_louder_audio_has_passed()
    {
        var delay = Onus.Services.AudioTap.GainFollower.Delay;
        var g = new Onus.Services.AudioTap.GainFollower();
        g.Set(1, 0);
        g.At(0).Should().Be(1f);                   // first volume applies at once
        g.Set(0, 10);                              // slider to 0: audio still in flight is loud …
        g.At(10 + delay - 1).Should().Be(1f);      // … so no ×100 000 on it (would clip the capture)
        g.Set(0, 10 + delay / 2);                  // re-sent each tick: doesn't restart the wait
        g.At(10 + delay).Should().Be(100000f);
        g.Set(1, 20 + delay);                      // turn-up: the gain must drop before louder audio arrives
        g.At(20 + delay).Should().Be(1f);
    }
}
