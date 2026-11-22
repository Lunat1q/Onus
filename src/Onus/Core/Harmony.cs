namespace Onus.Core;

/// <summary>
/// Chord-by-chord harmony of one song: which major/minor triad sounds now, how long each chord sounded, the
/// major vs. minor share of that time, chord changes per minute, and a mood from the share plus energy.
/// Feed it a lightly smoothed chroma (~0.5 s) every frame; <see cref="Reset"/> on song change.
/// </summary>
public sealed class Harmony
{
    /// <summary>Chord changes shorter than this are flicker, not harmony.</summary>
    private const double Settle = 0.3;
    private const double MinScore = 0.6;

    private readonly Dictionary<(int Root, bool Minor), double> _seconds = [];
    private (int Root, bool Minor)? _candidate;
    private double _candidateAge;

    public (int Root, bool Minor)? Current { get; private set; }
    public double MajorSeconds { get; private set; }
    public double MinorSeconds { get; private set; }
    public int Changes { get; private set; }
    /// <summary>Everything fed so far, chord or not (the clock for changes per minute).</summary>
    public double Elapsed { get; private set; }

    public double ChordSeconds => MajorSeconds + MinorSeconds;
    /// <summary>0 = only minor chords, 1 = only major; 0.5 before any chord.</summary>
    public double MajorShare => ChordSeconds > 0 ? MajorSeconds / ChordSeconds : 0.5;
    public double ChangesPerMinute => Elapsed > 5 ? Changes / (Elapsed / 60) : 0;

    /// <summary>Best triad for a chroma vector by Pearson r against a root/third/fifth template; null when nothing fits.</summary>
    // ponytail: triads only, and the 5th partial of a minor chord's root lands on the major third, so a harmonically
    // rich minor chord can read major. Template sets with 7ths / sus and harmonic-aware chroma are the upgrade path.
    public static (int Root, bool Minor, double Score)? Chord(double[] chroma)
    {
        (int, bool, double) best = (0, false, double.NegativeInfinity);
        for (var root = 0; root < 12; root++)
            foreach (var minor in new[] { false, true })
            {
                int third = (root + (minor ? 3 : 4)) % 12, fifth = (root + 7) % 12;
                var r = AudioAnalysis.Correlate(chroma, i => i == root || i == third || i == fifth ? 1 : 0);
                if (r > best.Item3) best = (root, minor, r);
            }
        return best.Item3 >= MinScore ? best : null;
    }

    public static string Name(int root, bool minor) => AudioAnalysis.Notes[root] + (minor ? "m" : "");

    public void Add(double[] chroma, double seconds)
    {
        Elapsed += seconds;
        var c = Chord(chroma) is { } x ? (x.Root, x.Minor) : ((int, bool)?)null;
        if (c != _candidate) { _candidate = c; _candidateAge = 0; }
        _candidateAge += seconds;
        if (_candidate != Current && _candidateAge >= Settle)
        {
            if (Current is not null && _candidate is not null) Changes++;
            Current = _candidate;
        }
        if (Current is not { } now) return;
        _seconds[now] = _seconds.GetValueOrDefault(now) + seconds;
        if (now.Minor) MinorSeconds += seconds; else MajorSeconds += seconds;
    }

    /// <summary>Most-played chords first, e.g. ["Am", "F", "C", "G"].</summary>
    public IEnumerable<string> Top(int count) =>
        _seconds.OrderByDescending(kv => kv.Value).Take(count).Select(kv => Name(kv.Key.Root, kv.Key.Minor));

    public void Reset()
    {
        _seconds.Clear();
        (_candidate, _candidateAge, Current, MajorSeconds, MinorSeconds, Changes, Elapsed) = (null, 0, null, 0, 0, 0, 0);
    }

    /// <summary>0..1 arousal from integrated loudness (−20…−8 LUFS) and average brightness (1…4 kHz centroid).</summary>
    // ponytail: two-feature guess; tempo / onset density would be the honest energy measure.
    public static double Energy(double integratedLufs, double centroidHz) =>
        ((double.IsFinite(integratedLufs) ? Math.Clamp((integratedLufs + 20) / 12, 0, 1) : 0) +
         Math.Clamp((centroidHz - 1000) / 3000, 0, 1)) / 2;

    /// <summary>Valence (major share) × arousal (energy) quadrant, with a bittersweet middle.</summary>
    public static string Mood(double majorShare, double energy)
    {
        var high = energy >= 0.55;
        return majorShare switch
        {
            >= 0.6 => high ? "Happy · upbeat" : "Warm · peaceful",
            <= 0.4 => high ? "Dark · intense" : "Sad · melancholic",
            _ => high ? "Bittersweet · driving" : "Bittersweet · reflective",
        };
    }
}
