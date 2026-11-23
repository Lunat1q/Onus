using Onus.Core;
using Onus.Services;

namespace Onus.Tests;

/// <summary>The SUNO_UUID frame is what Sync uses to skip songs already on disk, so it must survive a
/// write → read round trip in both formats Onus downloads.</summary>
public class TagIOTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("onus-tags-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly ClipInfo Clip = new()
    {
        Id = "0f8fad5b-d9cb-469f-a165-70867728950e",
        Title = "Neon Rain",
        Artist = "grim",
        Tags = "dark synthwave, 118 bpm",
        Prompt = "[Verse]\nlights",
        Lyrics = "[Verse]\nlights",
        CreatedAt = "2026-02-01T00:00:00Z",
    };

    /// <summary>Ten silent MPEG-1 Layer III frames (128 kbps, 44.1 kHz): enough for TagLib to accept the file.</summary>
    private string Mp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x64;
        var path = Path.Combine(_dir, "a.mp3");
        File.WriteAllBytes(path, Enumerable.Repeat(frame, 10).SelectMany(f => f).ToArray());
        return path;
    }

    /// <summary>A one-second silent 16-bit PCM WAV (8 kHz mono by default).</summary>
    private string Wav(int rate = 8000, short channels = 1, string name = "a.wav")
    {
        var path = Path.Combine(_dir, name);
        using var w = new BinaryWriter(File.Create(path));
        int block = channels * 2, bytes = rate * block;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(rate); w.Write(rate * block); w.Write((short)block); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]);
        return path;
    }

    [Fact]
    public void Mp3_round_trip_keeps_uuid_lyrics_and_derives_genre_bpm() => AssertRoundTrip(Mp3());

    [Fact]
    public void Wav_round_trip_keeps_uuid_lyrics_and_derives_genre_bpm() => AssertRoundTrip(Wav());

    /// <summary>M4A is what Suno serves when downloads aren't unlocked (Opus in MP4); a 0.3 s silent sample.</summary>
    [Fact]
    public void M4a_round_trip_keeps_uuid_lyrics_and_derives_genre_bpm()
    {
        var path = Path.Combine(_dir, "a.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);
        AssertRoundTrip(path);
        TagIO.Read(path).Prompt.Should().Be(Clip.Prompt);
    }

    private static void AssertRoundTrip(string path)
    {
        TagIO.WriteSuno(path, Clip, art: null, full: true);
        var t = TagIO.Read(path);

        t.SunoId.Should().Be(Clip.Id);
        t.Title.Should().Be("Neon Rain");
        t.Artist.Should().Be("grim");
        t.Lyrics.Should().Be("[Verse]\nlights");
        t.Genre.Should().Be("dark synthwave");
        t.Bpm.Should().Be(118);
    }

    [Fact]
    public void Lyrics_fall_back_to_the_txt_sidecar()
    {
        var path = Mp3();
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), "from sidecar");
        TagIO.Read(path).Lyrics.Should().Be("from sidecar");
    }

    [Fact]
    public void Untagged_file_uses_its_name()
    {
        var path = Mp3();
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "my_cool_song.mp3");
        File.Move(path, renamed);
        TagIO.Read(renamed).Title.Should().Be("my cool song");
    }

    /// <summary>Save as: converted copies keep title, artist, lyrics and prompt, but not the SUNO_UUID (a copy in the
    /// library folder must not count as the download). 8 kHz mono goes through the resampler for MP3/AAC.</summary>
    [Theory]
    [InlineData(48000, 2, ".mp3")]
    [InlineData(48000, 2, ".m4a")]
    [InlineData(8000, 1, ".mp3")]
    [InlineData(8000, 1, ".m4a")]
    public void Save_as_converts_and_carries_tags(int rate, short channels, string ext)
    {
        var src = Wav(rate, channels);
        TagIO.WriteSuno(src, Clip, art: null, full: true);
        var target = Path.Combine(_dir, "out", "Neon Rain" + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Exporter.Export(TagIO.Read(src), target);

        var t = TagIO.Read(target);
        t.Title.Should().Be("Neon Rain");
        t.Artist.Should().Be("grim");
        t.Lyrics.Should().Be(Clip.Lyrics);
        t.Prompt.Should().Be(Clip.Prompt);
        t.SunoId.Should().BeNull();
        t.Duration.Should().BeApproximately(1, 0.2);
        Directory.GetFiles(Path.GetDirectoryName(target)!).Should().Equal(target); // temp file gone
    }

    [Fact]
    public void Save_as_to_wav_decodes_and_same_format_copies()
    {
        var src = Wav(48000, 2);
        TagIO.WriteSuno(src, Clip, art: null, full: true);
        var mp3 = Path.Combine(_dir, "b.mp3");
        Exporter.Export(TagIO.Read(src), mp3);

        var wav = Path.Combine(_dir, "c.wav");
        Exporter.Export(TagIO.Read(mp3), wav);
        TagIO.Read(wav).Title.Should().Be("Neon Rain");

        var copy = Path.Combine(_dir, "d.wav");
        Exporter.Export(TagIO.Read(src), copy);
        File.ReadAllBytes(copy).Should().Equal(File.ReadAllBytes(src));
    }
}
