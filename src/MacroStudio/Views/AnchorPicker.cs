using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MacroStudio.Views;

/// <summary>
/// Shows a target and a crosshair: click (or drag) to choose where on the target the click lands. The anchor is stored as a
/// fraction of the target size from its centre, so "bottom-right corner" stays the bottom-right corner wherever the target moves.
/// </summary>
public sealed class AnchorPicker : FrameworkElement
{
    const double Pad = 16, Snap = 7;   // Pad, not Margin: must not hide FrameworkElement.Margin
    ImageSource? _img;
    double _rx, _ry;
    bool _drag;

    public event Action<double, double>? Changed;
    public event Action? Completed;

    public AnchorPicker()
    {
        Height = 140; MinWidth = 220; Cursor = Cursors.Cross;
        ToolTip = "Click where the click should land on the target. Near a corner, edge or the centre it snaps there.";
    }

    public void Set(ImageSource? image, double rx, double ry)
    {
        if (_drag) return; // do not fight the user while dragging
        _img = image; _rx = rx; _ry = ry;
        InvalidateVisual();
    }

    Rect ImageRect()
    {
        if (_img == null) return Rect.Empty;
        double iw = Math.Max(1, _img.Width), ih = Math.Max(1, _img.Height);
        double k = Math.Min((ActualWidth - 2 * Pad) / iw, (ActualHeight - 2 * Pad) / ih);
        k = Math.Min(k, 4);                       // do not blow a tiny target up absurdly
        double w = iw * k, h = ih * k;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Theme.PanelBg, Theme.LinePen, new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);
        var r = ImageRect();
        if (r.IsEmpty)
        {
            var t = Theme.Label(this, "choose a target first", 11, Theme.Muted);
            dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
            return;
        }
        dc.DrawImage(_img!, r);
        dc.DrawRectangle(null, Theme.Pen(Theme.Muted, 1), r);
        var p = new Point(r.X + r.Width / 2 + _rx * r.Width, r.Y + r.Height / 2 + _ry * r.Height);
        dc.DrawEllipse(null, Theme.Pen(Theme.White, 4), p, 7, 7);
        dc.DrawEllipse(null, Theme.Pen(Theme.Playhead, 2), p, 7, 7);
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X - 12, p.Y), new Point(p.X + 12, p.Y));
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X, p.Y - 12), new Point(p.X, p.Y + 12));
        var label = Theme.Label(this, $"{(0.5 + _rx) * 100:0}% / {(0.5 + _ry) * 100:0}%", 10, Theme.Muted);
        dc.DrawText(label, new Point(6, ActualHeight - label.Height - 3));
    }

    void Pick(Point p)
    {
        var r = ImageRect();
        if (r.IsEmpty) return;
        double rx = (p.X - r.X) / r.Width - 0.5, ry = (p.Y - r.Y) / r.Height - 0.5;
        rx = SnapTo(rx, r.Width); ry = SnapTo(ry, r.Height);
        _rx = Math.Clamp(rx, -0.75, 0.75); _ry = Math.Clamp(ry, -0.75, 0.75);
        InvalidateVisual();
        Changed?.Invoke(_rx, _ry);
    }

    /// <summary>Snap to the edges (-0.5, 0.5) and centre (0) when within a few pixels.</summary>
    static double SnapTo(double v, double size)
    {
        foreach (var s in new[] { -0.5, 0, 0.5 })
            if (Math.Abs(v - s) * size <= Snap) return s;
        return v;
    }

    /// <summary>Same effect as the user picking this anchor (used by tests).</summary>
    internal void RaiseSetForTest(double rx, double ry) { _rx = rx; _ry = ry; Changed?.Invoke(rx, ry); Completed?.Invoke(); }

    protected override void OnMouseDown(MouseButtonEventArgs e) { _drag = true; CaptureMouse(); Pick(e.GetPosition(this)); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) Pick(e.GetPosition(this)); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { _drag = false; ReleaseMouseCapture(); Completed?.Invoke(); }
}
