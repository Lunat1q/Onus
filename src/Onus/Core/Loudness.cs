namespace Onus.Core;

/// <summary>
/// ITU-R BS.1770 loudness (stereo): K-weighting, momentary (400 ms), short-term (3 s) and gated integrated LUFS.
/// Fed continuously from the capture thread; read from the UI. Not thread-safe on its own (caller locks).
/// </summary>
// ponytail: K-weighting coefficients are the spec's 48 kHz set and capture is fixed at 48 kHz; derive per rate if that changes.
public sealed class LoudnessMeter
{
    public const int SampleRate = 48000;
    private const int Block = SampleRate / 10; // 100 ms sub-blocks

    private readonly Biquad[] _shelf = [Shelf(), Shelf()], _highPass = [HighPass(), HighPass()];
    private readonly List<double> _blocks = []; // mean-square sum of both channels per 100 ms
    private readonly List<double> _gating = []; // 400 ms block energies, every 100 ms (75 % overlap)
    private double _acc;
    private int _count;

    public double Momentary => Lufs(Mean(4));
    public double ShortTerm => Lufs(Mean(30));

    /// <summary>Gated integrated loudness: −70 LUFS absolute gate, then −10 LU relative gate.</summary>
    public double Integrated
    {
        get
        {
            var abs = _gating.Where(e => Lufs(e) > -70).ToList();
            if (abs.Count == 0) return double.NegativeInfinity;
            var relGate = Lufs(abs.Average()) - 10;
            var rel = abs.Where(e => Lufs(e) > relGate).ToList();
            return rel.Count == 0 ? double.NegativeInfinity : Lufs(rel.Average());
        }
    }

    /// <summary>Interleaved stereo float samples.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + 1 < interleaved.Length; i += 2)
        {
            var l = _highPass[0].Step(_shelf[0].Step(interleaved[i]));
            var r = _highPass[1].Step(_shelf[1].Step(interleaved[i + 1]));
            _acc += l * l + r * r;
            if (++_count < Block) continue;
            _blocks.Add(_acc / Block);
            if (_blocks.Count > 30) _blocks.RemoveAt(0);
            if (_blocks.Count >= 4) _gating.Add(Mean(4));
            _acc = 0;
            _count = 0;
        }
    }

    public void Reset()
    {
        _blocks.Clear();
        _gating.Clear();
        _acc = 0;
        _count = 0;
    }

    private double Mean(int n) => _blocks.Count < n ? (_blocks.Count == 0 ? 0 : _blocks.Average()) : _blocks.Skip(_blocks.Count - n).Average();

    private static double Lufs(double meanSquare) => meanSquare <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(meanSquare);

    private static Biquad Shelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
    private static Biquad HighPass() => new(1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621);

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1, _z2;

        public double Step(double x)
        {
            var y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
