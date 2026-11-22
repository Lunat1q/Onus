using System.Globalization;
using System.Windows;
using System.Windows.Media;
using A = Onus.Core.AudioAnalysis;

namespace Onus.Views;

/// <summary>A blank element the analyzer paints into each frame.</summary>
public sealed class Scope : FrameworkElement
{
    public Action<DrawingContext, Size, double>? Draw { get; set; }

    public Scope() => ClipToBounds = true;

    protected override void OnRender(DrawingContext dc)
    {
        if (RenderSize.Width >= 16 && RenderSize.Height >= 14) Draw?.Invoke(dc, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}

/// <summary>Immediate-mode drawing for the analyzer panel (palette from Themes/Styles.xaml).</summary>
internal static class AnalyzerDraw
{
    private const double Floor = -90;
    private static readonly Brush Track = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Grid = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Label = Frozen(Color.FromRgb(0x6C, 0x6C, 0x82));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly Brush Accent2 = Frozen(Color.FromRgb(0xEC, 0x48, 0x99));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xEA, 0xB3, 0x08));
    private static readonly Brush Danger = Frozen(Color.FromRgb(0xF4, 0x3F, 0x5E));
    private static readonly Brush Good = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Dot = Frozen(Color.FromArgb(0x70, 0xC0, 0x84, 0xFC));
    private static readonly Brush Bars = Gradient(Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly Brush MeterFill = Gradient(Color.FromRgb(0xF4, 0x3F, 0x5E), Color.FromRgb(0xEA, 0xB3, 0x08),
        Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Pen GridPen = FrozenPen(Grid, 1);
    private static readonly Pen HarmonicPen = FrozenPen(Frozen(Color.FromArgb(0xB0, 0xFD, 0xE6, 0x8A)), 1, dashed: true);
    private static readonly Pen CapPen = FrozenPen(Frozen(Color.FromArgb(0xD0, 0xF4, 0xF4, 0xF8)), 1.5);

    // Spectrum heat map for the spectrogram: background → purple → pink → warm white.
    private static readonly Color[] HeatStops =
        [Color.FromRgb(0x0E, 0x0E, 0x13), Color.FromRgb(0x3B, 0x1F, 0x6B), Color.FromRgb(0xA8, 0x55, 0xF7),
         Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0xFD, 0xE6, 0x8A)];

    /// <summary>0..1 → Bgra32 pixel.</summary>
    public static int Heat(double t)
    {
        t = Math.Clamp(t, 0, 1) * (HeatStops.Length - 1);
        var i = Math.Min((int)t, HeatStops.Length - 2);
        var f = t - i;
        Color a = HeatStops[i], b = HeatStops[i + 1];
        int Mix(byte x, byte y) => (int)(x + (y - x) * f);
        return unchecked((int)0xFF000000) | Mix(a.R, b.R) << 16 | Mix(a.G, b.G) << 8 | Mix(a.B, b.B);
    }

    /// <summary>Two vertical bars (L, R): RMS fill, peak-hold line, dB scale between them.</summary>
    public static void Meter(DrawingContext dc, Size s, double dip, double[] rmsDb, double[] peakDb)
    {
        const double lo = -60, barW = 16, top = 6;
        double Y(double db) => s.Height - 14 - (Math.Clamp(db, lo, 0) - lo) / -lo * (s.Height - 14 - top);
        double[] xs = [0, s.Width - barW];
        for (var ch = 0; ch < 2; ch++)
        {
            var x = xs[ch];
            dc.DrawRoundedRectangle(Track, null, new Rect(x, 0, barW, s.Height - 14), 3, 3);
            dc.PushClip(new RectangleGeometry(new Rect(x, Y(rmsDb[ch]), barW, Math.Max(0, s.Height - 14 - Y(rmsDb[ch])))));
            dc.DrawRoundedRectangle(MeterFill, null, new Rect(x, 0, barW, s.Height - 14), 3, 3);
            dc.Pop();
            if (peakDb[ch] > lo) dc.DrawRectangle(peakDb[ch] >= -0.1 ? Danger : Brushes.White, null, new Rect(x, Y(peakDb[ch]) - 1, barW, 2));
            Draw(dc, ch == 0 ? "L" : "R", x + barW / 2, s.Height - 12, dip, Label, center: true);
        }
        foreach (var db in new[] { 0, -6, -12, -24, -36, -48 })
            Draw(dc, db.ToString(CultureInfo.InvariantCulture), s.Width / 2, Y(db) - 6, dip, Label, center: true);
    }

    /// <summary>Log-frequency bars, falling peak caps, harmonic markers of <paramref name="f0"/>, frequency grid.</summary>
    public static void Spectrum(DrawingContext dc, Size s, double dip, double[] bars, double[] caps, double f0)
    {
        const double lo = 30, hi = 16000;
        var h = s.Height - 14;
        double X(double hz) => Math.Log(hz / lo) / Math.Log(hi / lo) * s.Width;
        double Y(double db) => h - (Math.Clamp(db, Floor, 0) - Floor) / -Floor * h;

        foreach (var db in new[] { -20, -40, -60 }) dc.DrawLine(GridPen, new Point(0, Y(db)), new Point(s.Width, Y(db)));
        foreach (var (hz, text) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
        {
            dc.DrawLine(GridPen, new Point(X(hz), 0), new Point(X(hz), h));
            Draw(dc, text, X(hz), h + 1, dip, Label, center: true);
        }
        Draw(dc, "dB", 2, 0, dip, Label);

        var w = s.Width / bars.Length;
        for (var b = 0; b < bars.Length; b++)
        {
            var top = Y(bars[b]);
            if (h - top > 0.5) dc.DrawRectangle(Bars, null, new Rect(b * w + 0.5, top, Math.Max(1, w - 1.5), h - top));
            if (caps[b] > Floor + 1) dc.DrawLine(CapPen, new Point(b * w + 0.5, Y(caps[b])), new Point(b * w + w - 1, Y(caps[b])));
        }
        if (f0 <= 0) return;
        for (var n = 1; n <= 8 && f0 * n < hi; n++)
        {
            var x = X(f0 * n);
            dc.DrawLine(HarmonicPen, new Point(x, 0), new Point(x, h));
            Draw(dc, n == 1 ? "f₀" : n.ToString(CultureInfo.InvariantCulture), x + 2, 0, dip, Warn);
        }
    }

    /// <summary>H1..H8 level relative to the fundamental, 0 to −60 dB.</summary>
    public static void Partials(DrawingContext dc, Size s, double dip, double[] rel)
    {
        var w = s.Width / rel.Length;
        for (var i = 0; i < rel.Length; i++)
        {
            var t = Math.Clamp((rel[i] + 60) / 60, 0, 1);
            dc.DrawRoundedRectangle(Track, null, new Rect(i * w + 2, 0, w - 4, s.Height - 12), 2, 2);
            var bh = t * (s.Height - 12);
            if (bh > 0.5) dc.DrawRoundedRectangle(i == 0 ? Warn : Accent, null, new Rect(i * w + 2, s.Height - 12 - bh, w - 4, bh), 2, 2);
            Draw(dc, $"H{i + 1}", i * w + w / 2, s.Height - 11, dip, Label, center: true, size: 9);
        }
    }

    /// <summary>12 pitch-class bars, tonic of the estimated key in pink.</summary>
    public static void Chroma(DrawingContext dc, Size s, double dip, double[] chroma, int? tonic)
    {
        var w = s.Width / 12;
        for (var i = 0; i < 12; i++)
        {
            dc.DrawRoundedRectangle(Track, null, new Rect(i * w + 1.5, 0, w - 3, s.Height - 12), 2, 2);
            var bh = Math.Clamp(chroma[i], 0, 1) * (s.Height - 12);
            if (bh > 0.5) dc.DrawRoundedRectangle(i == tonic ? Accent2 : Accent, null, new Rect(i * w + 1.5, s.Height - 12 - bh, w - 3, bh), 2, 2);
            Draw(dc, A.Notes[i], i * w + w / 2, s.Height - 11, dip, i == tonic ? Accent2 : Label, center: true, size: 9);
        }
    }

    /// <summary>Time in major chords (gold, left) vs. minor (purple, right); an empty track before any chord.</summary>
    public static void MajorMinor(DrawingContext dc, Size s, double majorShare, bool any)
    {
        var r = new Rect(0, s.Height / 2 - 4, s.Width, 8);
        dc.DrawRoundedRectangle(Track, null, r, 4, 4);
        if (!any) return;
        var split = majorShare * s.Width;
        if (split > 0.5) dc.DrawRoundedRectangle(Warn, null, new Rect(0, r.Y, split, r.Height), 4, 4);
        if (s.Width - split > 0.5) dc.DrawRoundedRectangle(Accent, null, new Rect(split, r.Y, s.Width - split, r.Height), 4, 4);
        dc.DrawLine(GridPen, new Point(s.Width / 2, 0), new Point(s.Width / 2, s.Height));
    }

    /// <summary>Goniometer: mid on the vertical axis, side on the horizontal; auto-scaled to the loudest sample.</summary>
    public static void Goniometer(DrawingContext dc, Size s, float[] l, float[] r, int count)
    {
        var size = Math.Min(s.Width, s.Height);
        var c = new Point(s.Width / 2, s.Height / 2);
        dc.DrawEllipse(null, GridPen, c, size / 2, size / 2);
        dc.DrawLine(GridPen, new Point(c.X, c.Y - size / 2), new Point(c.X, c.Y + size / 2));
        dc.DrawLine(GridPen, new Point(c.X - size / 2, c.Y), new Point(c.X + size / 2, c.Y));
        var start = l.Length - count;
        double max = 1e-4;
        for (var i = start; i < l.Length; i++) max = Math.Max(max, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        var k = size / 2 / (max * Math.Sqrt(2));
        for (var i = start; i < l.Length; i += 2)
        {
            double mid = (l[i] + r[i]) / Math.Sqrt(2), side = (l[i] - r[i]) / Math.Sqrt(2);
            dc.DrawRectangle(Dot, null, new Rect(c.X + side * k - 0.75, c.Y - mid * k - 0.75, 1.5, 1.5));
        }
    }

    /// <summary>−1..+1 bar with a marker; red when out of phase.</summary>
    public static void CorrelationBar(DrawingContext dc, Size s, double corr)
    {
        dc.DrawRoundedRectangle(Track, null, new Rect(0, s.Height / 2 - 3, s.Width, 6), 3, 3);
        dc.DrawLine(GridPen, new Point(s.Width / 2, 0), new Point(s.Width / 2, s.Height));
        var x = (corr + 1) / 2 * s.Width;
        dc.DrawRoundedRectangle(corr < 0 ? Danger : corr < 0.3 ? Warn : Good, null, new Rect(x - 3, 0, 6, s.Height), 3, 3);
    }

    private static void Draw(DrawingContext dc, string text, double x, double y, double dip, Brush brush, bool center = false, double size = 10)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, dip);
        dc.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y));
    }

    private static readonly Typeface Face = new("Segoe UI");

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush Gradient(params Color[] top2bottom)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (var i = 0; i < top2bottom.Length; i++) g.GradientStops.Add(new GradientStop(top2bottom[i], (double)i / (top2bottom.Length - 1)));
        g.Freeze();
        return g;
    }

    private static Pen FrozenPen(Brush b, double thickness, bool dashed = false)
    {
        var p = new Pen(b, thickness);
        if (dashed) p.DashStyle = DashStyles.Dash;
        p.Freeze();
        return p;
    }
}
