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

    /// <summary>A one-second 8 kHz mono PCM WAV.</summary>
    private string Wav()
    {
        var path = Path.Combine(_dir, "a.wav");
        using var w = new BinaryWriter(File.Create(path));
        const int rate = 8000, samples = 8000;
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2); w.Write(new byte[samples * 2]);
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
}
