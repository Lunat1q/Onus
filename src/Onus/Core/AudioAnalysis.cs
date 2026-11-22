namespace Onus.Core;

/// <summary>
/// Live-analysis math for the analyzer panel: levels, FFT spectrum, log bands, pitch (harmonic product spectrum),
/// harmonic strengths, chroma and a key estimate (Krumhansl–Schmuckler). Pure functions over sample arrays.
/// </summary>
public static class AudioAnalysis
{
    public static readonly string[] Notes = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    // Krumhansl–Kessler key profiles, index 0 = tonic.
    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    public static double ToDb(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-9));

    /// <summary>RMS and peak (linear, 1 = full scale) of one channel.</summary>
    public static (double Rms, double Peak) Level(ReadOnlySpan<float> x)
    {
        if (x.IsEmpty) return (0, 0);
        double sum = 0, peak = 0;
        foreach (var s in x)
        {
            sum += s * s;
            peak = Math.Max(peak, Math.Abs(s));
        }
        return (Math.Sqrt(sum / x.Length), peak);
    }

    /// <summary>In-place iterative radix-2 FFT. Length must be a power of two.</summary>
    public static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            for (var k = 0; k < len / 2; k++) // twiddle outside the block loop: n-1 trig calls in total
            {
                double wr = Math.Cos(ang * k), wi = Math.Sin(ang * k);
                for (var i = 0; i < n; i += len)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }

    /// <summary>Hann-windowed amplitude spectrum, bins 0..N/2-1; a full-scale sine reads ≈ 1 at its bin.</summary>
    public static double[] Magnitudes(ReadOnlySpan<float> mono)
    {
        var n = mono.Length;
        double[] re = new double[n], im = new double[n];
        for (var i = 0; i < n; i++) re[i] = mono[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        Fft(re, im);
        var mags = new double[n / 2];
        for (var i = 0; i < mags.Length; i++) mags[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * 4 / n; // 2/N × Hann gain 2
        return mags;
    }

    public static double BinHz(int sampleRate, int bins) => sampleRate / 2.0 / bins;

    /// <summary>Spectral centroid ("brightness") in Hz.</summary>
    public static double Centroid(double[] mags, int sampleRate)
    {
        var hz = BinHz(sampleRate, mags.Length);
        double num = 0, den = 0;
        for (var k = 1; k < mags.Length; k++) { num += k * hz * mags[k]; den += mags[k]; }
        return den == 0 ? 0 : num / den;
    }

    /// <summary>L/R correlation: +1 mono, 0 wide/unrelated, −1 out of phase. 0 for silence.</summary>
    public static double Correlation(ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        double lr = 0, ll = 0, rr = 0;
        for (var i = 0; i < l.Length; i++) { lr += l[i] * r[i]; ll += l[i] * l[i]; rr += r[i] * r[i]; }
        return ll * rr < 1e-12 ? 0 : lr / Math.Sqrt(ll * rr);
    }

    /// <summary>Log-spaced bands from <paramref name="lo"/> to <paramref name="hi"/> Hz, each the loudest bin in dBFS.</summary>
    public static double[] Bands(double[] mags, int sampleRate, int count, double lo = 30, double hi = 16000)
    {
        var bandsDb = new double[count];
        var hz = BinHz(sampleRate, mags.Length);
        for (var b = 0; b < count; b++)
        {
            var f0 = lo * Math.Pow(hi / lo, (double)b / count);
            var f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / count);
            int k0 = (int)(f0 / hz), k1 = Math.Min(mags.Length - 1, Math.Max(k0, (int)(f1 / hz)));
            double max = 0;
            for (var k = k0; k <= k1; k++) max = Math.Max(max, mags[k]);
            bandsDb[b] = ToDb(max);
        }
        return bandsDb;
    }

    /// <summary>Fundamental in Hz by harmonic product spectrum over <paramref name="harmonics"/> partials; 0 when too quiet.</summary>
    public static double Fundamental(double[] mags, int sampleRate, double lo = 55, double hi = 1000, int harmonics = 4)
    {
        var hz = BinHz(sampleRate, mags.Length);
        var floor = 1e-4; // ≈ -80 dBFS
        int k0 = Math.Max(1, (int)(lo / hz)), k1 = Math.Min((mags.Length - 1) / harmonics, (int)(hi / hz));
        var best = -1; var bestScore = double.NegativeInfinity;
        var score = new double[k1 + 2];
        for (var k = k0; k <= k1 + 1 && k * harmonics < mags.Length; k++)
        {
            double s = 0;
            for (var h = 1; h <= harmonics; h++) s += Math.Log(Math.Max(mags[k * h], floor));
            score[k] = s;
            if (k <= k1 && s > bestScore) { bestScore = s; best = k; }
        }
        if (best < 0 || mags[best] < 1e-3) return 0;
        // Parabolic interpolation on the fundamental's own bin for sub-bin accuracy.
        double a = mags[best - 1], b = mags[best], c = mags[best + 1];
        var den = a - 2 * b + c;
        var shift = den == 0 ? 0 : Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5);
        return (best + shift) * hz;
    }

    /// <summary>Level of partials 1..count of <paramref name="f0"/>, in dB relative to the fundamental.</summary>
    public static double[] Harmonics(double[] mags, int sampleRate, double f0, int count = 8)
    {
        var rel = new double[count];
        Array.Fill(rel, -120.0);
        if (f0 <= 0) return rel;
        var hz = BinHz(sampleRate, mags.Length);
        double Peak(double f)
        {
            var k = (int)Math.Round(f / hz);
            double m = 0;
            for (var i = Math.Max(0, k - 2); i <= Math.Min(mags.Length - 1, k + 2); i++) m = Math.Max(m, mags[i]);
            return m;
        }
        var fund = ToDb(Peak(f0));
        for (var h = 1; h <= count; h++) rel[h - 1] = f0 * h < sampleRate / 2.0 ? ToDb(Peak(f0 * h)) - fund : -120;
        return rel;
    }

    /// <summary>Energy per pitch class (C = 0), 80 Hz–5 kHz, scaled so the strongest is 1.</summary>
    public static double[] Chroma(double[] mags, int sampleRate)
    {
        var chroma = new double[12];
        var hz = BinHz(sampleRate, mags.Length);
        for (var k = (int)(80 / hz); k < mags.Length && k * hz <= 5000; k++)
        {
            var midi = 69 + 12 * Math.Log2(k * hz / 440);
            chroma[((int)Math.Round(midi) % 12 + 12) % 12] += mags[k] * mags[k];
        }
        var max = chroma.Max();
        if (max > 0) for (var i = 0; i < 12; i++) chroma[i] /= max;
        return chroma;
    }

    /// <summary>Best-correlating key for a chroma vector; Score is Pearson r (≈ confidence).</summary>
    public static (int Tonic, bool Minor, double Score) Key(double[] chroma)
    {
        (int, bool, double) best = (0, false, double.NegativeInfinity);
        for (var tonic = 0; tonic < 12; tonic++)
            foreach (var minor in new[] { false, true })
            {
                var profile = minor ? MinorProfile : MajorProfile;
                var r = Correlate(chroma, i => profile[(i - tonic + 12) % 12]);
                if (r > best.Item3) best = (tonic, minor, r);
            }
        return best;
    }

    /// <summary>Pearson r of a 12-element vector against a pitch-class function; 0 when either is flat.</summary>
    internal static double Correlate(double[] x, Func<int, double> y)
    {
        double mx = x.Average(), my = Enumerable.Range(0, 12).Average(y), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < 12; i++)
        {
            double dx = x[i] - mx, dy = y(i) - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxx == 0 || syy == 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>"A4 +3¢" style name for a frequency.</summary>
    public static string NoteName(double hz)
    {
        if (hz <= 0) return "—";
        var midi = 69 + 12 * Math.Log2(hz / 440);
        var n = (int)Math.Round(midi);
        var cents = (int)Math.Round((midi - n) * 100);
        var name = $"{Notes[(n % 12 + 12) % 12]}{n / 12 - 1}";
        return cents == 0 ? name : $"{name} {cents:+0;-0}¢";
    }
}
