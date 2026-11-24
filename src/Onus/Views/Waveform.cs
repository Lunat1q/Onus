using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Onus.Core;

namespace Onus.Views;

/// <summary>The editor's waveform: time ruler, min/max peaks of the visible range, selection, cursor and playhead.
/// Click = cursor, drag (or Shift+click) = selection, wheel = zoom at the mouse, Shift+wheel = scroll.
/// Peaks are recomputed only when the clip, view or width changes, so the playhead can redraw at 30 fps.</summary>
public sealed class Waveform : FrameworkElement
{
    private const double Ruler = 18;
    private static readonly Brush Wave = Frozen(Color.FromRgb(0xA8, 0x55, 0xF7)), SelWave = Frozen(Color.FromRgb(0xF0, 0xC8, 0xFF)),
        SelFill = Frozen(Color.FromArgb(0x40, 0xA8, 0x55, 0xF7)), Faint = Frozen(Color.FromRgb(0x6C, 0x6C, 0x82)),
        Back = Frozen(Color.FromRgb(0x12, 0x12, 0x19));
    private static readonly Pen CursorPen = FrozenPen(Colors.White, 1), PlayPen = FrozenPen(Color.FromRgb(0xEC, 0x48, 0x99), 1.5),
        TickPen = FrozenPen(Color.FromRgb(0x2A, 0x2A, 0x37), 1);

    private AudioClip? _clip;
    private float[]? _peaks;
    private (AudioClip?, double, double, int) _peaksFor;
    private int _anchor;
    private bool _dragging;

    /// <summary>Setting it keeps the zoom (clamped to the new length); call <see cref="Fit"/> for the whole song.</summary>
    public AudioClip? Audio { get => _clip; set { _clip = value; SetView(ViewStart, ViewEnd); } }
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; } = 1;
    public int SelStart { get; private set; }
    public int SelEnd { get; private set; }
    public int Caret { get; private set; }
    /// <summary>Playhead frame, or -1 when not playing.</summary>
    public int Playhead { get; set { field = value; InvalidateVisual(); } } = -1;
    public bool HasSelection => SelEnd > SelStart;

    /// <summary>Caret, selection or view moved.</summary>
    public event Action? Changed;

    public Waveform()
    {
        ClipToBounds = true;
        Focusable = false;
        base.Cursor = Cursors.IBeam;
    }

    public void Fit() => SetView(0, _clip?.Frames ?? 1);

    public void ZoomToSelection() { if (HasSelection) SetView(SelStart, SelEnd); }

    public void Zoom(double factor, double? aroundFrame = null)
    {
        var at = aroundFrame ?? (Playhead >= 0 ? Playhead : Caret);
        var width = Math.Max(64, (ViewEnd - ViewStart) * factor);
        var share = ViewEnd > ViewStart ? Math.Clamp((at - ViewStart) / (ViewEnd - ViewStart), 0, 1) : 0.5;
        SetView(at - width * share, at - width * share + width);
    }

    public void SetView(double start, double end)
    {
        var frames = Math.Max(1, _clip?.Frames ?? 1);
        var width = Math.Clamp(end - start, Math.Min(64, frames), frames);
        ViewStart = Math.Clamp(start, 0, frames - width);
        ViewEnd = ViewStart + width;
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>Set the cursor and selection (frames, clamped), e.g. after an edit.</summary>
    public void Select(int cursor, int selStart = 0, int selEnd = 0)
    {
        var frames = _clip?.Frames ?? 0;
        Caret = Math.Clamp(cursor, 0, frames);
        SelStart = Math.Clamp(Math.Min(selStart, selEnd), 0, frames);
        SelEnd = Math.Clamp(Math.Max(selStart, selEnd), 0, frames);
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>Keep the playhead on screen while playing (page along).</summary>
    public void Follow()
    {
        if (Playhead >= 0 && (Playhead < ViewStart || Playhead > ViewEnd)) SetView(Playhead, Playhead + ViewEnd - ViewStart);
    }

    private double X(double frame) => (frame - ViewStart) / (ViewEnd - ViewStart) * ActualWidth;
    private int FrameAt(double x) => (int)Math.Clamp(ViewStart + x / Math.Max(1, ActualWidth) * (ViewEnd - ViewStart), 0, _clip?.Frames ?? 0);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_clip is null) return;
        var frame = FrameAt(e.GetPosition(this).X);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _anchor = HasSelection ? (Math.Abs(frame - SelStart) < Math.Abs(frame - SelEnd) ? SelEnd : SelStart) : Caret;
            Select(_anchor, _anchor, frame);
        }
        else
        {
            _anchor = frame;
            Select(frame);
        }
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var frame = FrameAt(e.GetPosition(this).X);
        if (Math.Abs(X(frame) - X(_anchor)) >= 3) Select(_anchor, _anchor, frame);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var width = ViewEnd - ViewStart;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SetView(ViewStart - Math.Sign(e.Delta) * width * 0.15, ViewEnd - Math.Sign(e.Delta) * width * 0.15);
        else Zoom(e.Delta > 0 ? 0.8 : 1.25, FrameAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Back, null, new Rect(0, 0, w, h));
        if (_clip is null || w < 2 || h < Ruler + 4) return;
        var columns = (int)w;
        if (_peaks is null || _peaksFor != (_clip, ViewStart, ViewEnd, columns))
        {
            _peaks = AudioEdit.Peaks(_clip, ViewStart, ViewEnd, columns);
            _peaksFor = (_clip, ViewStart, ViewEnd, columns);
        }
        DrawRuler(dc, w);

        double mid = Ruler + (h - Ruler) / 2, amp = (h - Ruler) / 2 - 2;
        dc.DrawLine(TickPen, new Point(0, mid), new Point(w, mid));
        double s0 = X(SelStart), s1 = X(SelEnd);
        if (HasSelection) dc.DrawRectangle(SelFill, null, new Rect(new Point(Math.Max(0, s0), Ruler), new Point(Math.Min(w, s1), h)));
        for (var col = 0; col < columns; col++)
        {
            double top = mid - _peaks[col * 2 + 1] * amp, bottom = mid - _peaks[col * 2] * amp;
            if (bottom - top < 1) { top = mid - 0.5; bottom = mid + 0.5; }
            dc.DrawRectangle(HasSelection && col >= s0 && col < s1 ? SelWave : Wave, null, new Rect(col, top, 1, bottom - top));
        }
        var cx = X(Caret);
        if (cx >= 0 && cx <= w) dc.DrawLine(CursorPen, new Point(cx, Ruler), new Point(cx, h));
        var px = X(Playhead);
        if (Playhead >= 0 && px >= 0 && px <= w) dc.DrawLine(PlayPen, new Point(px, 0), new Point(px, h));
    }

    /// <summary>Labels every 1/2/5 × 10ⁿ seconds, at least ~90 px apart.</summary>
    private void DrawRuler(DrawingContext dc, double w)
    {
        var rate = _clip!.SampleRate;
        var secondsPerPx = (ViewEnd - ViewStart) / rate / w;
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        var step = steps.FirstOrDefault(s => s / secondsPerPx >= 90, 600);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var t = Math.Ceiling(ViewStart / rate / step) * step; t * rate <= ViewEnd; t += step)
        {
            var x = X(t * rate);
            dc.DrawLine(TickPen, new Point(x, Ruler - 5), new Point(x, ActualHeight));
            var label = new FormattedText(EditorWindow.Time(t, step < 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10.5, Faint, dpi);
            dc.DrawText(label, new Point(x + 3, 2));
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(Frozen(c), width);
        p.Freeze();
        return p;
    }
}
