using NAudio.Wave;
using Onus.Core;
using Onus.Services;

namespace Onus.Tests;

/// <summary>The Library editor: range edits on a clip, and saving over the song / as a copy.</summary>
public class EditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("onus-edit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Stereo clip whose frame i holds (i, -i) / 1000, so positions are readable after an edit.</summary>
    private static AudioClip Ramp(int frames) =>
        new(Enumerable.Range(0, frames).SelectMany(i => new[] { i / 1000f, -i / 1000f }).ToArray(), 2, 1000);

    private static float[] Left(AudioClip c) => c.Samples.Where((_, i) => i % 2 == 0).ToArray();

    [Fact]
    public void Crop_delete_and_insert_move_whole_frames()
    {
        var c = Ramp(10);
        Left(AudioEdit.Crop(c, 2, 5)).Should().Equal(0.002f, 0.003f, 0.004f);
        Left(AudioEdit.Delete(c, 2, 8)).Should().Equal(0f, 0.001f, 0.008f, 0.009f);
        var pasted = AudioEdit.Insert(c, 1, AudioEdit.Slice(c, 8, 10));
        Left(pasted).Take(4).Should().Equal(0f, 0.008f, 0.009f, 0.001f);
        pasted.Frames.Should().Be(12);
        AudioEdit.Delete(c, 5, 99).Frames.Should().Be(5); // clamped
        c.Frames.Should().Be(10); // edits never touch the original (it's the undo snapshot)
    }

    [Fact]
    public void Paste_refuses_a_different_format()
    {
        var act = () => AudioEdit.Insert(Ramp(4), 0, new AudioClip(new float[4], 1, 1000));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Gain_normalize_silence_and_fades_touch_only_the_range()
    {
        var c = new AudioClip(Enumerable.Repeat(0.5f, 20).ToArray(), 2, 1000); // 10 frames of 0.5
        AudioEdit.Gain(c, 0, 5, 6.0206).Samples.Take(10).Should().AllSatisfy(s => s.Should().BeApproximately(1f, 1e-4f));
        AudioEdit.Gain(c, 0, 5, 6.0206).Samples.Skip(10).Should().AllBeEquivalentTo(0.5f);
        AudioEdit.Peak(AudioEdit.Normalize(c, 0, 10), 0, 10).Should().BeApproximately((float)Math.Pow(10, -1 / 20.0), 1e-4f);
        AudioEdit.Silence(c, 3, 4).Samples.Should().Equal([.. Enumerable.Repeat(0.5f, 6), 0f, 0f, .. Enumerable.Repeat(0.5f, 12)]);

        var fadeIn = Left(AudioEdit.Fade(c, 0, 10, fadeIn: true));
        fadeIn[0].Should().Be(0);
        fadeIn[9].Should().BeApproximately(0.5f, 1e-5f);
        fadeIn.Should().BeInAscendingOrder();
        var fadeOut = Left(AudioEdit.Fade(c, 5, 10, fadeIn: false));
        fadeOut.Take(5).Should().AllBeEquivalentTo(0.5f);
        fadeOut[9].Should().BeApproximately(0, 1e-6f);
    }

    [Fact]
    public void Audible_range_skips_quiet_ends_and_normalize_leaves_silence_alone()
    {
        var samples = new float[40];
        samples[10] = 0.5f; samples[25] = -0.5f; // frames 5 and 12
        var c = new AudioClip(samples, 2, 1000);
        AudioEdit.Audible(c).Should().Be((5, 13));
        AudioEdit.Audible(new AudioClip(new float[8], 2, 1000)).Should().Be((0, 0));
        var silent = new AudioClip(new float[8], 2, 1000);
        AudioEdit.Normalize(silent, 0, 4).Should().BeSameAs(silent);
    }

    [Fact]
    public void Peaks_give_min_max_per_column_over_all_channels()
    {
        var p = AudioEdit.Peaks(Ramp(10), 0, 10, 2); // columns: frames 0-4, 5-9
        p.Should().Equal(-0.004f, 0.004f, -0.009f, 0.009f);
        AudioEdit.Peaks(Ramp(10), 0, 2, 4).Should().HaveCount(8); // zoomed past one frame per column
    }

    private string Tone(string name = "song.wav", double seconds = 2)
    {
        var path = Path.Combine(_dir, name);
        using var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
        for (var i = 0; i < 48000 * seconds; i++)
        {
            var s = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 48000));
            w.WriteSample(s);
            w.WriteSample(s);
        }
        return path;
    }

    private static readonly ClipInfo Clip = new() { Id = "0f8fad5b-d9cb-469f-a165-70867728950e", Title = "Neon Rain", Artist = "grim", Prompt = "lights" };

    [Theory]
    [InlineData("song.wav")]
    [InlineData("song.mp3")]
    [InlineData("song.m4a")]
    public void Saving_over_the_song_keeps_its_id_and_hands_the_original_over(string name)
    {
        var path = Path.Combine(_dir, name);
        Exporter.Export(TagIO.Read(Tone("src.wav")), path);
        TagIO.WriteSuno(path, Clip, art: null, full: true);
        var song = TagIO.Read(path);

        var clip = Exporter.Decode(path);
        clip.Seconds.Should().BeApproximately(2, 0.1);
        var discarded = new List<string>();
        Exporter.Write(song, AudioEdit.Crop(clip, 0, clip.SampleRate), path, keepId: true, p =>
        {
            discarded.Add(p);
            File.Move(p, Path.Combine(_dir, "backup" + Path.GetExtension(p)));
        });

        discarded.Should().Equal(path);
        var t = TagIO.Read(path);
        t.SunoId.Should().Be(Clip.Id);
        t.Title.Should().Be("Neon Rain");
        t.Prompt.Should().Be("lights");
        t.Duration.Should().BeApproximately(1, 0.1);
        Directory.GetFiles(_dir, ".*").Should().BeEmpty(); // temp file gone
    }

    [Fact]
    public void A_copy_drops_the_id_and_a_failed_discard_leaves_the_original()
    {
        var path = Tone();
        TagIO.WriteSuno(path, Clip, art: null, full: true);
        var song = TagIO.Read(path);
        var clip = AudioEdit.Crop(Exporter.Decode(path), 0, 24000);

        var copy = Path.Combine(_dir, "copy.mp3");
        Exporter.Write(song, clip, copy, keepId: false);
        TagIO.Read(copy).SunoId.Should().BeNull();
        TagIO.Read(copy).Title.Should().Be("Neon Rain");

        var before = File.ReadAllBytes(path);
        var act = () => Exporter.Write(song, clip, path, keepId: true, _ => throw new IOException("bin refused"));
        act.Should().Throw<IOException>();
        File.ReadAllBytes(path).Should().Equal(before);
        Directory.GetFiles(_dir, ".*").Should().BeEmpty();
    }
}
