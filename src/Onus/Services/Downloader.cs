using System.Net;
using Onus.Core;

namespace Onus.Services;

/// <summary>Downloads one clip: resolve the stream (MP3, or WAV via Suno's converter), save it via a
/// .part file, then write tags, lyrics .txt and cover art sidecars.</summary>
public sealed class Downloader(SunoClient suno, HttpClient http)
{
    public const int MinBytes = 1024;
    public const string LockedMessage = "Stream only: Suno doesn't share the audio file for this song (downloads aren't unlocked on this account).";

    public async Task<string> DownloadAsync(ClipInfo clip, Settings s, string? sourceName, RateLimiter limiter,
        IProgress<double>? progress, CancellationToken ct)
    {
        clip = await WithDetailsAsync(clip, ct);
        var (url, ext) = await ResolveAudioAsync(clip, s, ct);
        var dir = FileNames.TargetFolder(s.DownloadFolder, clip, s, sourceName);
        Directory.CreateDirectory(dir);
        var wanted = Path.Combine(dir, FileNames.Sanitize(clip.Title.Length > 0 ? clip.Title : clip.Id) + ext);

        await limiter.WaitAsync(ct);
        var path = await SaveStreamAsync(url, wanted, progress, ct);

        var art = await TryGetBytesAsync(clip.ImageLargeUrl ?? clip.ImageUrl, ct);
        try
        {
            TagIO.WriteSuno(path, clip, art, s.EmbedMetadata);
        }
        catch (Exception e) when (e is IOException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            Log.Warn($"Saved {Path.GetFileName(path)} but could not tag it: {e.Message}");
        }
        if (s.SaveLyricsFile && clip.Lyrics.Length > 0)
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".txt"), clip.Lyrics, ct);
        if (s.SaveArtFiles && art is { Length: > 0 })
        {
            WriteIfMissing(Path.ChangeExtension(path, ".jpg"), art);
            WriteIfMissing(Path.Combine(dir, "cover.jpg"), art); // folder art for Explorer
        }
        return path;
    }

    /// <summary>Tag an analytics-cache capture like a download (title, style, prompt, lyrics, SUNO_UUID, cover).</summary>
    public async Task TagCaptureAsync(string path, ClipInfo clip, CancellationToken ct = default)
    {
        clip = await WithDetailsAsync(clip, ct);
        var art = await TryGetBytesAsync(clip.ImageLargeUrl ?? clip.ImageUrl, ct);
        TagIO.WriteSuno(path, clip, art, full: true);
    }

    /// <summary>List views often omit prompt/lyrics: refetch the clip then, keeping what we have on failure.</summary>
    private async Task<ClipInfo> WithDetailsAsync(ClipInfo clip, CancellationToken ct)
    {
        if (clip.Prompt.Length > 0) return clip;
        try { return await suno.GetClipAsync(clip.Id, ct) ?? clip; }
        catch (Exception e) when (e is HttpRequestException or SunoAuthException) { Log.Warn($"No details for {clip.Title}: {e.Message}"); return clip; }
    }

    private async Task<(string Url, string Ext)> ResolveAudioAsync(ClipInfo clip, Settings s, CancellationToken ct)
    {
        if (s.Format == AudioFormat.Wav)
        {
            var wav = clip.WavUrl ?? await suno.ConvertWavAsync(clip.Id, ct);
            if (wav is not null) return (wav, FileNames.ExtensionFromUrl(wav, ".wav"));
            Log.Warn($"No WAV for {clip.Title}; falling back to MP3.");
        }
        var mp3 = clip.AudioUrl ?? throw new InvalidOperationException(clip.AudioLocked ? LockedMessage : "Suno has no audio for this clip yet.");
        return (mp3, FileNames.ExtensionFromUrl(mp3, ".mp3"));
    }

    private static readonly Lock MoveGate = new();

    /// <summary>Stream to a .part file, verify size, then move into place under a free name ("Song v2.mp3")
    /// — the library never sees half a file, and parallel clips with the same title can't collide.</summary>
    internal async Task<string> SaveStreamAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        var part = $"{path}.{Guid.NewGuid():N}.part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // CDN links are public; the Suno token is deliberately not sent to them.
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var u = new Uri(url); // path only: signed query strings stay out of the log
                    throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {u.Host}{u.AbsolutePath}",
                        null, resp.StatusCode);
                }
                var total = resp.Content.Headers.ContentLength ?? 0;
                long done = 0;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(part))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }
                if (done < MinBytes) throw new IOException($"Downloaded file too small ({done} bytes).");
                if (total > 0 && done < total) throw new IOException($"Incomplete download ({done}/{total} bytes).");
                lock (MoveGate)
                {
                    var final = FileNames.Unique(path);
                    File.Move(part, final, overwrite: false);
                    return final;
                }
            }
            catch (Exception e) when (attempt < 3 && !ct.IsCancellationRequested && IsTransient(e))
            {
                Log.Warn($"Retry {attempt}/3 for {Path.GetFileName(path)}: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
        }
    }

    /// <summary>Network blips, 5xx, 408 and 429 are worth a retry; a 403/404 won't change in 2 s.</summary>
    internal static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } sc } => (int)sc >= 500 || sc is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException or IOException => true,
        _ => false,
    };

    private async Task<byte[]?> TryGetBytesAsync(string? url, CancellationToken ct)
    {
        if (url is null) return null;
        try { return await http.GetByteArrayAsync(url, ct); }
        catch (HttpRequestException e) { Log.Warn($"Cover art unavailable: {e.Message}"); return null; }
    }

    private static void WriteIfMissing(string path, byte[] data)
    {
        try { if (!File.Exists(path)) File.WriteAllBytes(path, data); }
        catch (IOException e) { Log.Warn($"Could not save {Path.GetFileName(path)}: {e.Message}"); }
    }
}
