using Onus.Core;

namespace Onus.Tests;

public class HarmonyTests
{
    private const int Rate = 48000;

    private static double[] ChromaOf(params double[] hz)
    {
        var x = Enumerable.Range(0, 8192).Select(i => (float)hz.Sum(f => 0.25 * Math.Sin(2 * Math.PI * f * i / Rate))).ToArray();
        return AudioAnalysis.Chroma(AudioAnalysis.Magnitudes(x), Rate);
    }

    private static readonly double[] CMajor = ChromaOf(261.63, 329.63, 392.0), AMinor = ChromaOf(220, 261.63, 329.63),
        FMajor = ChromaOf(174.61, 220, 261.63), DMinor = ChromaOf(146.83, 174.61, 220);

    [Fact]
    public void Names_triads_from_audio()
    {
        Harmony.Chord(CMajor).Should().Match<(int Root, bool Minor, double)?>(c => c!.Value.Root == 0 && !c.Value.Minor);
        Harmony.Chord(AMinor).Should().Match<(int Root, bool Minor, double)?>(c => c!.Value.Root == 9 && c.Value.Minor);
        Harmony.Name(9, true).Should().Be("Am");
    }

    [Fact]
    public void Flat_or_single_note_chroma_is_no_chord()
    {
        Harmony.Chord(new double[12]).Should().BeNull();
        Harmony.Chord(Enumerable.Repeat(1.0, 12).ToArray()).Should().BeNull();
    }

    [Fact]
    public void Tallies_minor_share_changes_and_top_chords()
    {
        var h = new Harmony();
        void Play(double[] chroma, int seconds) { for (var i = 0; i < seconds * 20; i++) h.Add(chroma, 0.05); }
        for (var bar = 0; bar < 4; bar++) { Play(AMinor, 3); Play(FMajor, 1); Play(DMinor, 2); }

        h.MajorShare.Should().BeApproximately(4 / 24.0, 0.03); // F: 4 s of 24
        h.Changes.Should().Be(11);
        h.Top(2).Should().Equal("Am", "Dm");
        h.ChangesPerMinute.Should().BeApproximately(11 / 0.4, 1);
        Harmony.Mood(h.MajorShare, 0.3).Should().Be("Sad · melancholic");

        h.Reset();
        h.ChordSeconds.Should().Be(0);
        h.MajorShare.Should().Be(0.5);
    }

    [Fact]
    public void Flicker_shorter_than_settle_time_is_not_a_change()
    {
        var h = new Harmony();
        for (var i = 0; i < 20; i++) h.Add(CMajor, 0.05);
        h.Add(AMinor, 0.05); // one stray frame
        for (var i = 0; i < 20; i++) h.Add(CMajor, 0.05);
        h.Changes.Should().Be(0);
        h.MinorSeconds.Should().Be(0);
    }

    [Theory]
    [InlineData(0.8, 0.8, "Happy · upbeat")]
    [InlineData(0.8, 0.2, "Warm · peaceful")]
    [InlineData(0.2, 0.8, "Dark · intense")]
    [InlineData(0.5, 0.2, "Bittersweet · reflective")]
    public void Mood_quadrants(double major, double energy, string mood) => Harmony.Mood(major, energy).Should().Be(mood);

    [Fact]
    public void Energy_rises_with_loudness_and_brightness()
    {
        Harmony.Energy(double.NegativeInfinity, 0).Should().Be(0);
        Harmony.Energy(-8, 4000).Should().Be(1);
        Harmony.Energy(-14, 2500).Should().BeApproximately(0.5, 0.01);
    }
}
