using A = Onus.Core.AudioAnalysis;

namespace Onus.Core;

/// <summary>What the analyzer concludes about a whole song, kept with the library cache. <see cref="Mood"/> is null
/// when the song had under 15 s of recognisable chords (same rule as the live panel).</summary>
public sealed record SongProfile(string? Mood, string Key, double KeyScore, double MajorShare, double Energy,
    double Lufs, double ChangesPerMinute, string TopChords)
{
    /// <summary>Bump when the analysis changes, so cached profiles are redone.</summary>
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
}

/// <summary>
/// The live analyzer's song-level math (BS.1770 loudness, chroma → triads → major share, centroid → energy, mood),
/// run over a whole file instead of the speakers: feed 48 kHz interleaved stereo, then <see cref="Result"/>.
/// The key comes from the song's mean chroma rather than the panel's rolling 8 s, so it's the whole song's key.
/// </summary>
// ponytail: one 8192-point frame every 100 ms (the panel uses ~30/s); plenty for chords that must last 0.3 s.
public sealed class SongProfiler
{
    private const int N = 8192, Hop = LoudnessMeter.SampleRate / 10, LevelWindow = 2048;
    private const double Step = (double)Hop / LoudnessMeter.SampleRate;

    private readonly LoudnessMeter _loudness = new();
    private readonly Harmony _harmony = new();
    private readonly float[] _ring = new float[N], _mono = new float[N];
    private readonly double[] _chordChroma = new double[12], _chromaSum = new double[12];
    private int _filled, _pos, _sinceHop;
    private double _centroidSum, _loudTime;

    public void Process(ReadOnlySpan<float> interleaved)
    {
        _loudness.Process(interleaved);
        for (var i = 0; i + 1 < interleaved.Length; i += 2)
        {
            _ring[_pos] = (interleaved[i] + interleaved[i + 1]) / 2;
            _pos = (_pos + 1) % N;
            if (_filled < N) _filled++;
            if (++_sinceHop < Hop || _filled < N) continue;
            _sinceHop = 0;
            _ring.AsSpan(_pos).CopyTo(_mono); // oldest first
            _ring.AsSpan(0, _pos).CopyTo(_mono.AsSpan(N - _pos));
            Frame();
        }
    }

    private void Frame()
    {
        if (A.ToDb(A.Level(_mono.AsSpan(N - LevelWindow)).Rms) <= -55) return; // the panel's "loud" gate
        var mags = A.Magnitudes(_mono);
        var chroma = A.Chroma(mags, LoudnessMeter.SampleRate);
        var fast = 1 - Math.Exp(-Step / 0.5);
        for (var i = 0; i < 12; i++)
        {
            _chordChroma[i] += fast * (chroma[i] - _chordChroma[i]);
            _chromaSum[i] += chroma[i];
        }
        _harmony.Add(_chordChroma, Step);
        _centroidSum += A.Centroid(mags, LoudnessMeter.SampleRate) * Step;
        _loudTime += Step;
    }

    public SongProfile Result()
    {
        var lufs = _loudness.Integrated;
        var energy = Harmony.Energy(lufs, _loudTime > 0 ? _centroidSum / _loudTime : 0);
        var h = _harmony;
        var (tonic, minor, score) = _chromaSum.Sum() > 0 ? A.Key(_chromaSum) : (0, false, 0);
        return new SongProfile(
            h.ChordSeconds >= 15 ? Harmony.Mood(h.MajorShare, energy) : null,
            _chromaSum.Sum() > 0 ? $"{A.Notes[tonic]} {(minor ? "minor" : "major")}" : "",
            score, h.MajorShare, energy, double.IsFinite(lufs) ? lufs : -70, h.ChangesPerMinute, string.Join(" ", h.Top(4)));
    }
}
