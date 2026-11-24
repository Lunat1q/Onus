using NAudio.MediaFoundation;
using NAudio.Wave;
using Onus.Core;

namespace Onus.Services;

/// <summary>Library "Save as": copy or convert a song to MP3 / WAV / M4A (AAC) through Windows' Media Foundation
/// codecs, tags carried over. Same format as the source = plain copy, no re-encode. Also the editor's decode / save.</summary>
public static class Exporter
{
    public static readonly string[] Formats = [".mp3", ".wav", ".m4a"];

    /// <summary>Writes to a temp name first (Media Foundation picks the container from the extension, so it keeps
    /// the real one), tags it, then moves it over <paramref name="target"/>.</summary>
    public static void Export(Track t, string target)
    {
        var ext = Format(target);
        var temp = TempFor(target);
        try
        {
            if (string.Equals(Path.GetExtension(t.Path), ext, StringComparison.OrdinalIgnoreCase)) File.Copy(t.Path, temp, overwrite: true);
            else
            {
                using var reader = new MediaFoundationReader(t.Path);
                Encode(reader, temp, ext);
                TagIO.CopyTags(t, temp);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally { Discard(temp); }
    }

    /// <summary>The whole song as float samples at its own rate and channel count (editor).</summary>
    public static AudioClip Decode(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var samples = reader.ToSampleProvider();
        var f = samples.WaveFormat;
        var all = new List<float>((int)Math.Min(int.MaxValue / 2, reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign) * f.Channels + 1));
        var buffer = new float[f.SampleRate * f.Channels];
        int read;
        while ((read = samples.Read(buffer)) > 0) all.AddRange(buffer.AsSpan(0, read));
        return new AudioClip(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all)[..(all.Count - all.Count % f.Channels)].ToArray(), f.Channels, f.SampleRate);
    }

    /// <summary>Editor save: <paramref name="clip"/> encoded by <paramref name="target"/>'s extension, tags copied from
    /// <paramref name="t"/>. Replacing the song itself (<paramref name="keepId"/>: it's still the download) first hands
    /// the existing file to <paramref name="discardExisting"/> (Recycle Bin) once the new one is fully written.</summary>
    public static void Write(Track t, AudioClip clip, string target, bool keepId, Action<string>? discardExisting = null)
    {
        var ext = Format(target);
        var temp = TempFor(target);
        try
        {
            Encode(new ClipReader(clip).ToWaveProvider16(), temp, ext);
            TagIO.CopyTags(t, temp, keepId);
            if (discardExisting is not null && File.Exists(target)) discardExisting(target);
            File.Move(temp, target, overwrite: true);
        }
        finally { Discard(temp); }
    }

    private static string Format(string target)
    {
        var ext = Path.GetExtension(target).ToLowerInvariant();
        return Formats.Contains(ext) ? ext : throw new ArgumentException($"Can't save as {ext}");
    }

    private static string TempFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? "", $".{Path.GetFileNameWithoutExtension(target)}.saving{Path.GetExtension(target)}");

    private static void Discard(string temp)
    {
        try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Encode(IWaveProvider source, string temp, string ext)
    {
        var pcm = Encodable(source);
        using var resampler = pcm == source ? null : pcm as IDisposable;
        switch (ext)
        {
            case ".wav": WaveFileWriter.CreateWaveFile(temp, pcm); break;
            case ".mp3": MediaFoundationEncoder.EncodeToMp3(pcm, temp, 320_000); break;
            default: MediaFoundationEncoder.EncodeToAac(pcm, temp, 256_000); break;
        }
    }

    /// <summary>The MP3/AAC encoders take 16-bit PCM at 44.1/48 kHz only; anything else is resampled to that.</summary>
    private static IWaveProvider Encodable(IWaveProvider source)
    {
        var f = source.WaveFormat;
        if (f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample == 16 && f.SampleRate is 44100 or 48000 && f.Channels <= 2)
            return source;
        return new MediaFoundationResampler(source, new WaveFormat(f.SampleRate == 44100 ? 44100 : 48000, 16, Math.Min(f.Channels, 2)));
    }
}

/// <summary>Plays or encodes a clip from <see cref="Position"/> up to <see cref="End"/> (frames). Position is read by
/// the UI while the audio thread advances it.</summary>
public sealed class ClipReader(AudioClip clip, int start = 0, int? end = null) : ISampleProvider
{
    private int _position = start;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, clip.Channels);
    public int Position => Volatile.Read(ref _position);
    public int End { get; } = Math.Min(end ?? clip.Frames, clip.Frames);
    public float Volume { get; set; } = 1;

    public int Read(Span<float> buffer)
    {
        var pos = Position;
        var frames = Math.Max(0, Math.Min(buffer.Length / clip.Channels, End - pos));
        var n = frames * clip.Channels;
        clip.Samples.AsSpan(pos * clip.Channels, n).CopyTo(buffer);
        if (Volume != 1) for (var i = 0; i < n; i++) buffer[i] *= Volume;
        Volatile.Write(ref _position, pos + frames);
        return n;
    }
}
