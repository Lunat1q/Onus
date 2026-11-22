using Onus.Core;

namespace Onus.Tests;

public class AudioAnalysisTests
{
    private const int Rate = 48000;

    private static float[] Tone(int n, params (double Hz, double Amp)[] partials) =>
        Enumerable.Range(0, n).Select(i => (float)partials.Sum(p => p.Amp * Math.Sin(2 * Math.PI * p.Hz * i / Rate))).ToArray();

    [Fact]
    public void Full_scale_sine_peaks_at_0_dB_with_rms_minus_3()
    {
        var (rms, peak) = AudioAnalysis.Level(Tone(4800, (1000, 1)));
        AudioAnalysis.ToDb(peak).Should().BeApproximately(0, 0.01);
        AudioAnalysis.ToDb(rms).Should().BeApproximately(-3.01, 0.05);
    }

    [Fact]
    public void Spectrum_reads_a_sine_at_its_frequency_and_amplitude()
    {
        var mags = AudioAnalysis.Magnitudes(Tone(8192, (1500, 0.5)));
        var bin = Array.IndexOf(mags, mags.Max());
        (bin * AudioAnalysis.BinHz(Rate, mags.Length)).Should().BeApproximately(1500, 6);
        mags.Max().Should().BeApproximately(0.5, 0.1); // Hann scalloping keeps it within ~1.5 dB

        var bands = AudioAnalysis.Bands(mags, Rate, 48);
        var loudest = Array.IndexOf(bands, bands.Max());
        (30 * Math.Pow(16000 / 30.0, (loudest + 0.5) / 48)).Should().BeInRange(1300, 1750);
    }

    [Fact]
    public void Finds_the_fundamental_of_a_harmonic_tone_and_its_partials()
    {
        var mags = AudioAnalysis.Magnitudes(Tone(8192, (220, 0.4), (440, 0.2), (660, 0.13), (880, 0.1), (1100, 0.08)));
        var f0 = AudioAnalysis.Fundamental(mags, Rate);
        f0.Should().BeApproximately(220, 3);
        AudioAnalysis.NoteName(f0).Should().StartWith("A3");
        var h = AudioAnalysis.Harmonics(mags, Rate, f0);
        h[0].Should().BeApproximately(0, 0.01);
        h[1].Should().BeApproximately(-6, 1.5); // 0.2 vs 0.4
        h[7].Should().BeLessThan(-40);           // no 8th partial
    }

    [Fact]
    public void Silence_has_no_pitch()
    {
        AudioAnalysis.Fundamental(new double[4096], Rate).Should().Be(0);
        AudioAnalysis.NoteName(0).Should().Be("—");
        AudioAnalysis.Harmonics(new double[4096], Rate, 0).Should().AllSatisfy(h => h.Should().BeLessThan(-60)); // empty bars, not full
    }

    [Theory]
    [InlineData(440, "A4")]
    [InlineData(261.63, "C4")]
    [InlineData(452.89, "A4 +50¢")]
    public void Names_notes(double hz, string name) => AudioAnalysis.NoteName(hz).Should().Be(name);

    [Fact]
    public void C_major_triad_reads_as_C_major()
    {
        var mags = AudioAnalysis.Magnitudes(Tone(8192, (261.63, 0.3), (329.63, 0.3), (392.0, 0.3)));
        var chroma = AudioAnalysis.Chroma(mags, Rate);
        new[] { chroma[0], chroma[4], chroma[7] }.Min().Should().BeGreaterThan(0.5);
        chroma[1].Should().BeLessThan(0.1);
        var (tonic, minor, _) = AudioAnalysis.Key(chroma);
        (AudioAnalysis.Notes[tonic], minor).Should().Be(("C", false));
    }

    [Fact]
    public void A_minor_triad_reads_as_A_minor()
    {
        var mags = AudioAnalysis.Magnitudes(Tone(8192, (220, 0.3), (261.63, 0.3), (329.63, 0.3)));
        var (tonic, minor, _) = AudioAnalysis.Key(AudioAnalysis.Chroma(mags, Rate));
        (AudioAnalysis.Notes[tonic], minor).Should().Be(("A", true));
    }

    [Fact]
    public void Correlation_tells_mono_from_out_of_phase()
    {
        var x = Tone(2048, (300, 0.5));
        AudioAnalysis.Correlation(x, x).Should().BeApproximately(1, 1e-6);
        AudioAnalysis.Correlation(x, x.Select(s => -s).ToArray()).Should().BeApproximately(-1, 1e-6);
        AudioAnalysis.Correlation(new float[10], new float[10]).Should().Be(0);
    }

    [Fact]
    public void Centroid_rises_with_brightness()
    {
        var dark = AudioAnalysis.Centroid(AudioAnalysis.Magnitudes(Tone(8192, (200, 0.5))), Rate);
        var bright = AudioAnalysis.Centroid(AudioAnalysis.Magnitudes(Tone(8192, (200, 0.5), (6000, 0.5))), Rate);
        bright.Should().BeGreaterThan(dark * 5);
    }

    [Fact]
    public void Full_scale_997_Hz_stereo_sine_is_0_LUFS() // BS.1770 calibration: −3.01 LUFS per channel, both = 0
    {
        var m = new LoudnessMeter();
        var mono = Tone(Rate * 4, (997, 1));
        var stereo = mono.SelectMany(s => new[] { s, s }).ToArray();
        m.Process(stereo);
        m.Momentary.Should().BeApproximately(0, 0.1);
        m.ShortTerm.Should().BeApproximately(0, 0.1);
        m.Integrated.Should().BeApproximately(0, 0.1);
    }

    [Fact]
    public void Integrated_loudness_gates_out_silence()
    {
        var m = new LoudnessMeter();
        var tone = Tone(Rate * 2, (997, 0.1)).SelectMany(s => new[] { s, s }).ToArray(); // −20 dBFS
        m.Process(tone);
        var before = m.Integrated;
        m.Process(new float[Rate * 4]);
        m.Integrated.Should().BeApproximately(before, 0.5); // silence below −70 LUFS doesn't drag it down
        m.Momentary.Should().Be(double.NegativeInfinity);
        m.Reset();
        m.Integrated.Should().Be(double.NegativeInfinity);
    }
}
