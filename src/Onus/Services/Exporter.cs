using NAudio.MediaFoundation;
using NAudio.Wave;
using Onus.Core;

namespace Onus.Services;

/// <summary>Library "Save as": copy or convert a song to MP3 / WAV / M4A (AAC) through Windows' Media Foundation
/// codecs, tags carried over. Same format as the source = plain copy, no re-encode.</summary>
public static class Exporter
{
    public static readonly string[] Formats = [".mp3", ".wav", ".m4a"];

    /// <summary>Writes to a temp name first (Media Foundation picks the container from the extension, so it keeps
    /// the real one), tags it, then moves it over <paramref name="target"/>.</summary>
    public static void Export(Track t, string target)
    {
        var ext = Path.GetExtension(target).ToLowerInvariant();
        if (!Formats.Contains(ext)) throw new ArgumentException($"Can't save as {ext}");
        var temp = Path.Combine(Path.GetDirectoryName(target) ?? "", $".{Path.GetFileNameWithoutExtension(target)}.saving{ext}");
        try
        {
            if (string.Equals(Path.GetExtension(t.Path), ext, StringComparison.OrdinalIgnoreCase)) File.Copy(t.Path, temp, overwrite: true);
            else
            {
                using var reader = new MediaFoundationReader(t.Path);
                var pcm = Encodable(reader);
                using var resampler = pcm as IDisposable; // the reader itself when no resampling was needed: disposing twice is harmless
                switch (ext)
                {
                    case ".wav": WaveFileWriter.CreateWaveFile(temp, pcm); break;
                    case ".mp3": MediaFoundationEncoder.EncodeToMp3(pcm, temp, 320_000); break;
                    default: MediaFoundationEncoder.EncodeToAac(pcm, temp, 256_000); break;
                }
                TagIO.CopyTags(t, temp);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The MP3/AAC encoders take 16-bit PCM at 44.1/48 kHz only; anything else is resampled to that.</summary>
    private static IWaveProvider Encodable(WaveStream reader)
    {
        var f = reader.WaveFormat;
        if (f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample == 16 && f.SampleRate is 44100 or 48000 && f.Channels <= 2)
            return reader;
        return new MediaFoundationResampler(reader, new WaveFormat(f.SampleRate == 44100 ? 44100 : 48000, 16, Math.Min(f.Channels, 2)));
    }
}
