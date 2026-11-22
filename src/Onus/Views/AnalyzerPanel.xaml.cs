using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Onus.Core;
using Onus.Services;
using Onus.ViewModels;
using A = Onus.Core.AudioAnalysis;

namespace Onus.Views;

/// <summary>
/// Live analysis of whatever Onus is playing (local file, Suno stream or suno.com's own player): levels, loudness,
/// spectrum + spectrogram, pitch/harmonics/chroma/key and stereo image. Capture runs only while the panel is open.
/// </summary>
public partial class AnalyzerPanel : UserControl
{
    private const int N = 8192, LevelWindow = 2048, Bands = 72, SpecW = 320;

    private readonly float[] _l = new float[N], _r = new float[N], _mono = new float[N];
    private readonly double[] _vu = [-90, -90], _peak = [-90, -90], _peakAge = [0, 0];
    private readonly double[] _bars = Filled(Bands, -90), _caps = Filled(Bands, -90), _capAge = new double[Bands];
    private readonly double[] _chromaAvg = new double[12], _chordChroma = new double[12];
    private readonly Harmony _harmony = new();
    private readonly WriteableBitmap _specBmp = new(SpecW, Bands, 96, 96, PixelFormats.Bgra32, null);
    private readonly int[] _specPx = new int[SpecW * Bands];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TranslateTransform _slide = new();
    private MainViewModel? _vm;
    private bool _hooked;
    private double _last, _f0, _centroid, _centroidSum, _loudTime;
    private double[] _harmonics = new double[8], _chroma = new double[12];
    private (int Tonic, bool Minor, double Score)? _key;
    private int _frame;

    public AnalyzerPanel()
    {
        InitializeComponent();
        ((UIElement)Content).RenderTransform = _slide;
        Spectrogram.Source = _specBmp;
        Array.Fill(_specPx, AnalyzerDraw.Heat(0));
        Meter.Draw = (dc, s, dip) => AnalyzerDraw.Meter(dc, s, dip, _vu, _peak);
        Spectrum.Draw = (dc, s, dip) => AnalyzerDraw.Spectrum(dc, s, dip, _bars, _caps, _f0);
        Partials.Draw = (dc, s, dip) => AnalyzerDraw.Partials(dc, s, dip, _harmonics);
        Chroma.Draw = (dc, s, dip) => AnalyzerDraw.Chroma(dc, s, dip, _chroma, _key?.Tonic);
        MajorMinor.Draw = (dc, s, _) => AnalyzerDraw.MajorMinor(dc, s, _harmony.MajorShare, _harmony.ChordSeconds > 0);
        Vector.Draw = (dc, s, _) => AnalyzerDraw.Goniometer(dc, s, _l, _r, LevelWindow);
        Correlation.Draw = (dc, s, _) => AnalyzerDraw.CorrelationBar(dc, s, A.Correlation(_l.AsSpan(N - LevelWindow), _r.AsSpan(N - LevelWindow)));
        IsVisibleChanged += (_, _) => { if (IsVisible) Open(); else Close(); };
        Unloaded += (_, _) => Close();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not MainViewModel vm) return;
            _vm = vm;
            vm.Player.TrackChanged += _ => NewTrack();
        };
    }

    private static double[] Filled(int n, double v) { var a = new double[n]; Array.Fill(a, v); return a; }

    private AnalyticsHub? Hub => _vm?.Analytics;

    private void Open()
    {
        // Slide up rather than grow, so the scopes never lay out at a sliver of their size.
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Height, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; _last = 0; }
        Hub?.PanelOpened();
    }

    /// <summary>Panel hidden: stop drawing. Capture is the hub's business (it may keep recording).</summary>
    private void Close()
    {
        if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
        Hub?.PanelClosed();
    }

    private void NewTrack()
    {
        Array.Clear(_chromaAvg);
        Array.Clear(_chordChroma);
        _key = null;
        _harmony.Reset();
        _centroidSum = _loudTime = 0;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _last;
        if (dt < 1 / 30.0) return;
        Status.Text = Hub?.Error ?? "";
        Status.Visibility = Hub?.Error is null ? Visibility.Collapsed : Visibility.Visible;
        if (Hub?.Tap is not { } tap) return;
        _last = now;
        _frame++;

        tap.Snapshot(_l, _r);
        for (var i = 0; i < N; i++) _mono[i] = (_l[i] + _r[i]) / 2;

        // Levels: RMS with VU-like release, peak hold 1.5 s then fall.
        for (var ch = 0; ch < 2; ch++)
        {
            var (rms, peak) = A.Level((ch == 0 ? _l : _r).AsSpan(N - LevelWindow));
            _vu[ch] = Math.Max(A.ToDb(rms), _vu[ch] - 30 * dt);
            Hold(ref _peak[ch], ref _peakAge[ch], A.ToDb(peak), dt, 1.5, 20);
        }

        var mags = A.Magnitudes(_mono);
        var bands = A.Bands(mags, AudioTap.Rate, Bands);
        for (var b = 0; b < Bands; b++)
        {
            _bars[b] = Math.Max(bands[b], _bars[b] - 60 * dt);
            Hold(ref _caps[b], ref _capAge[b], bands[b], dt, 0.8, 25);
        }
        PushSpectrogram(bands);

        var loud = _vu.Max() > -55;
        _f0 = loud ? A.Fundamental(mags, AudioTap.Rate) : 0;
        _harmonics = A.Harmonics(mags, AudioTap.Rate, _f0);
        _chroma = loud ? A.Chroma(mags, AudioTap.Rate) : new double[12];
        if (loud)
        {
            var alpha = 1 - Math.Exp(-dt / 8.0);
            for (var i = 0; i < 12; i++) _chromaAvg[i] += alpha * (_chroma[i] - _chromaAvg[i]);
            if (_chromaAvg.Sum() > 0.5) _key = A.Key(_chromaAvg);

            // Chords: ~0.5 s of chroma, step capped so a stalled frame (pause, panel reopened) isn't counted as music.
            var step = Math.Min(dt, 0.1);
            var fast = 1 - Math.Exp(-step / 0.5);
            for (var i = 0; i < 12; i++) _chordChroma[i] += fast * (_chroma[i] - _chordChroma[i]);
            _harmony.Add(_chordChroma, step);
            _centroid = A.Centroid(mags, AudioTap.Rate);
            _centroidSum += _centroid * step;
            _loudTime += step;
        }
        else _centroid = 0;

        if (_frame % 4 == 0) UpdateText(mags, loud);
        foreach (var s in new[] { Meter, Spectrum, Partials, Chroma, MajorMinor, Vector, Correlation }) s.InvalidateVisual();
    }

    private static void Hold(ref double value, ref double age, double input, double dt, double hold, double fallDbPerSec)
    {
        if (input >= value) { value = input; age = 0; return; }
        age += dt;
        if (age > hold) value = Math.Max(input, value - fallDbPerSec * dt);
    }

    private void PushSpectrogram(double[] bands)
    {
        for (var y = 0; y < Bands; y++)
        {
            var row = y * SpecW;
            Array.Copy(_specPx, row + 1, _specPx, row, SpecW - 1);
            _specPx[row + SpecW - 1] = AnalyzerDraw.Heat((bands[Bands - 1 - y] + 90) / 90); // low notes at the bottom
        }
        _specBmp.WritePixels(new Int32Rect(0, 0, SpecW, Bands), _specPx, SpecW * 4, 0);
    }

    private void UpdateText(double[] mags, bool loud)
    {
        var st = Hub!.Tap!.Stats();
        static string Lu(double v) => double.IsNegativeInfinity(v) || v < -70 ? "  —  " : $"{v,5:0.0}";
        ShortTerm.Text = Lu(st.ShortTerm).Trim();
        var peakDb = st.MaxPeak > 0 ? A.ToDb(st.MaxPeak) : double.NegativeInfinity;
        var plr = double.IsFinite(peakDb) && double.IsFinite(st.Integrated) ? $"{peakDb - st.Integrated,5:0.0} LU" : "  —";
        var target = double.IsFinite(st.Integrated) && st.Integrated > -70 ? $"{st.Integrated + 14,5:+0.0;-0.0} LU" : "  —";
        LoudStats.Text = $"Moment.  {Lu(st.Momentary)}\nIntegr.  {Lu(st.Integrated)}\nPeak     {Lu(peakDb)} dB\n" +
                         $"PLR      {plr}\nTarget   {target}\nClips    {st.Clips,5}";

        var centroid = _centroid;
        Capture.Text = Hub?.Recorder?.State ?? "";
        Brightness.Text = loud ? $"brightness {(centroid >= 1000 ? $"{centroid / 1000:0.0} kHz" : $"{centroid:0} Hz")}" : "";

        Pitch.Text = A.NoteName(_f0);
        PitchHz.Text = _f0 > 0 ? $"{_f0:0.0} Hz" : "";
        Key.Text = _key is { } k ? $"Key  {A.Notes[k.Tonic]} {(k.Minor ? "minor" : "major")} · {Math.Max(0, k.Score):P0}" : "Key  listening…";

        var h = _harmony;
        Chord.Text = loud && h.Current is { } c ? Harmony.Name(c.Root, c.Minor) : "—";
        var energy = Harmony.Energy(st.Integrated, _loudTime > 0 ? _centroidSum / _loudTime : 0);
        Mood.Text = h.ChordSeconds >= 15 ? Harmony.Mood(h.MajorShare, energy) : "listening…";
        var top = string.Join(" ", h.Top(4));
        MoodStats.Text = $"Major {h.MajorShare,4:P0}  minor {1 - h.MajorShare,4:P0}\n" +
                         $"Chords  {(top.Length > 0 ? top : "—")}\nChanges {h.ChangesPerMinute,3:0} / min\nEnergy  {energy,4:P0}";

        var w = _l.AsSpan(N - LevelWindow);
        var r = _r.AsSpan(N - LevelWindow);
        double mid = 0, side = 0;
        for (var i = 0; i < w.Length; i++) { mid += (w[i] + r[i]) * (w[i] + r[i]); side += (w[i] - r[i]) * (w[i] - r[i]); }
        var width = mid + side > 1e-9 ? Math.Sqrt(side) / (Math.Sqrt(mid) + Math.Sqrt(side)) : 0;
        StereoStats.Text = $"Corr {A.Correlation(w, r),5:+0.00;-0.00}  Width {width,4:P0}";
    }
}
