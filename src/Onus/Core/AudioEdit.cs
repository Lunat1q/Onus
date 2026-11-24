namespace Onus.Core;

/// <summary>A decoded song in memory: interleaved float samples. Edits never change it in place, they return a new
/// clip, so the editor's undo stack is just the previous clips.</summary>
public sealed record AudioClip(float[] Samples, int Channels, int SampleRate)
{
    public int Frames => Samples.Length / Channels;
    public double Seconds => (double)Frames / SampleRate;
}

/// <summary>The Library editor's operations on frame ranges [start, end). Ranges are clamped to the clip.</summary>
public static class AudioEdit
{
    /// <summary>Keep only [start, end).</summary>
    public static AudioClip Crop(AudioClip c, int start, int end) => Slice(c, start, end);

    /// <summary>Copy of [start, end) (the editor's clipboard).</summary>
    public static AudioClip Slice(AudioClip c, int start, int end)
    {
        (start, end) = Clamp(c, start, end);
        return c with { Samples = c.Samples[(start * c.Channels)..(end * c.Channels)] };
    }

    public static AudioClip Delete(AudioClip c, int start, int end)
    {
        (start, end) = Clamp(c, start, end);
        return c with { Samples = [.. c.Samples.AsSpan(0, start * c.Channels), .. c.Samples.AsSpan(end * c.Channels)] };
    }

    /// <summary>Put <paramref name="piece"/> in at frame <paramref name="at"/> (same channel count and rate only).</summary>
    public static AudioClip Insert(AudioClip c, int at, AudioClip piece)
    {
        if (piece.Channels != c.Channels || piece.SampleRate != c.SampleRate)
            throw new ArgumentException($"Can't paste {piece.SampleRate / 1000.0:0.#} kHz × {piece.Channels} into {c.SampleRate / 1000.0:0.#} kHz × {c.Channels}");
        at = Math.Clamp(at, 0, c.Frames) * c.Channels;
        return c with { Samples = [.. c.Samples.AsSpan(0, at), .. piece.Samples, .. c.Samples.AsSpan(at)] };
    }

    public static AudioClip Silence(AudioClip c, int start, int end) => Map(c, start, end, (_, _) => 0);

    public static AudioClip Gain(AudioClip c, int start, int end, double db)
    {
        var g = (float)Math.Pow(10, db / 20);
        return Map(c, start, end, (s, _) => s * g);
    }

    /// <summary>Scale [start, end) so its loudest sample sits at <paramref name="targetDb"/> dBFS. Silence stays silent.</summary>
    public static AudioClip Normalize(AudioClip c, int start, int end, double targetDb = -1)
    {
        var peak = Peak(c, start, end);
        return peak <= 1e-6 ? c : Gain(c, start, end, targetDb - 20 * Math.Log10(peak));
    }

    /// <summary>Fade in (silence → full) or out across [start, end), on an S-curve (no click, no sudden drop).</summary>
    public static AudioClip Fade(AudioClip c, int start, int end, bool fadeIn)
    {
        (start, end) = Clamp(c, start, end);
        var len = Math.Max(1, end - start - 1);
        return Map(c, start, end, (s, frame) =>
        {
            var t = (double)(frame - start) / len;
            if (!fadeIn) t = 1 - t;
            return s * (float)(0.5 - 0.5 * Math.Cos(Math.PI * t));
        });
    }

    /// <summary>First and last audible frame (any channel above <paramref name="thresholdDb"/> dBFS), as a range to crop to.
    /// All silent → (0, 0).</summary>
    public static (int Start, int End) Audible(AudioClip c, double thresholdDb = -50)
    {
        var t = (float)Math.Pow(10, thresholdDb / 20);
        int first = Array.FindIndex(c.Samples, s => Math.Abs(s) > t), last = Array.FindLastIndex(c.Samples, s => Math.Abs(s) > t);
        return first < 0 ? (0, 0) : (first / c.Channels, last / c.Channels + 1);
    }

    public static float Peak(AudioClip c, int start, int end)
    {
        (start, end) = Clamp(c, start, end);
        var peak = 0f;
        foreach (var s in c.Samples.AsSpan(start * c.Channels, (end - start) * c.Channels)) peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    /// <summary>Waveform: min and max (over all channels) for each of <paramref name="columns"/> equal slices of
    /// [start, end), as [min0, max0, min1, max1, …]. Zoomed past one frame per column, a column shows its nearest frame.</summary>
    public static float[] Peaks(AudioClip c, double start, double end, int columns)
    {
        var result = new float[columns * 2];
        var per = (end - start) / columns;
        for (var col = 0; col < columns; col++)
        {
            var a = (int)(start + col * per);
            var b = Math.Max(a + 1, (int)(start + (col + 1) * per));
            if (a < 0 || a >= c.Frames) continue;
            b = Math.Min(b, c.Frames);
            float lo = 0, hi = 0;
            foreach (var s in c.Samples.AsSpan(a * c.Channels, (b - a) * c.Channels)) { lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
            result[col * 2] = lo;
            result[col * 2 + 1] = hi;
        }
        return result;
    }

    private static AudioClip Map(AudioClip c, int start, int end, Func<float, int, float> f)
    {
        (start, end) = Clamp(c, start, end);
        var samples = (float[])c.Samples.Clone();
        for (var frame = start; frame < end; frame++)
            for (int ch = 0, i = frame * c.Channels; ch < c.Channels; ch++, i++) samples[i] = f(samples[i], frame);
        return c with { Samples = samples };
    }

    private static (int, int) Clamp(AudioClip c, int start, int end)
    {
        start = Math.Clamp(start, 0, c.Frames);
        return (start, Math.Clamp(end, start, c.Frames));
    }
}
