using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;

namespace MacroStudio.Views;

/// <summary>
/// While a macro plays: the recorded mouse path (blue, dashed) and the path the pointer actually takes (orange, semi-transparent),
/// drawn over the screen, one click-through, capture-excluded window per monitor. Both are shown so they can be compared.
/// </summary>
public sealed class PathOverlay : IDisposable
{
    static readonly Brush RecordedBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 59, 130, 246)));
    static readonly Brush OptimizedBrush = Frozen(new SolidColorBrush(Color.FromArgb(165, 249, 115, 22)));

    readonly List<(Window Win, Layer Layer)> _wins = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly Func<IReadOnlyList<(ScreenRect Rect, string Label)>>? _located;

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <param name="capturable">Visible to screen recorders, screenshots and screen sharing. Off: hidden from every capture (and so from the detector).</param>
    /// <param name="located">The targets the macro has found right now, drawn as boxes just outside their pixels.</param>
    public PathOverlay(IReadOnlyList<(int X, int Y)> recorded, PathTrace trace, bool capturable = false, Func<IReadOnlyList<(ScreenRect Rect, string Label)>>? located = null, bool visible = true)
    {
        _located = located;
        var monitors = Screens.Current().Monitors;
        int minX = monitors.Min(m => m.X);
        foreach (var mon in monitors)
        {
            var layer = new Layer(mon, recorded, trace, mon.X == minX);
            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                Topmost = true, ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, Width = 100, Height = 100, Content = layer, Focusable = false,
            };
            WindowPlacement.Pin(win, mon);
            win.SourceInitialized += (_, _) => { WindowPlacement.ClickThrough(win); if (!capturable) WindowPlacement.ExcludeFromCapture(win); };
            win.Show();
            _wins.Add((win, layer));
        }
        Visible = visible;
        _timer.Tick += (_, _) =>
        {
            var now = _located?.Invoke() ?? [];   // ask once per tick, not once per monitor
            foreach (var (_, l) in _wins) l.Refresh(now);
        };
        _timer.Start();
    }

    /// <summary>Show or hide the whole overlay at any time (while playing, while recording the screen).</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            foreach (var (w, _) in _wins) { if (value) w.Show(); else w.Hide(); }
            if (value) _timer.Start(); else _timer.Stop();
        }
    }
    bool _visible = true;

    /// <summary>Whether screen recorders, screenshots and screen sharing see the overlay (off = hidden from every capture).</summary>
    public bool Capturable { set { foreach (var (w, _) in _wins) WindowPlacement.ExcludeFromCapture(w, !value); } }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var (w, _) in _wins) w.Close();
        _wins.Clear();
    }

    sealed class Layer : FrameworkElement
    {
        readonly ScreenRect _mon;
        readonly IReadOnlyList<(int X, int Y)> _recorded;
        readonly PathTrace _trace;
        readonly bool _legend;   // only the leftmost monitor draws the key, once at construction instead of re-enumerating monitors every frame
        IReadOnlyList<(ScreenRect Rect, string Label)> _items = [];
        int _drawn = -1;

        public Layer(ScreenRect mon, IReadOnlyList<(int X, int Y)> recorded, PathTrace trace, bool legend)
        { _mon = mon; _recorded = recorded; _trace = trace; _legend = legend; IsHitTestVisible = false; }

        public void Refresh(IReadOnlyList<(ScreenRect Rect, string Label)> now)
        {
            int n = _trace.Count + _trace.Clicks().Length * 100000;
            bool same = now.Count == _items.Count && now.Zip(_items).All(z => z.First.Rect == z.Second.Rect && z.First.Label == z.Second.Label);
            if (n != _drawn || !same) { _drawn = n; _items = now; InvalidateVisual(); }
        }

        Geometry? Line(IReadOnlyList<(int X, int Y)> pts, double k)
        {
            if (pts.Count < 2) return null;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool open = false;
                foreach (var p in pts)
                {
                    var pt = new Point((p.X - _mon.X) * k, (p.Y - _mon.Y) * k);
                    if (!open) { c.BeginFigure(pt, false, false); open = true; } else c.LineTo(pt, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double k = ActualWidth / _mon.Width; // DIPs per physical pixel
            if (Line(_recorded, k) is { } rg) dc.DrawGeometry(null, new Pen(RecordedBrush, 2) { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round }, rg);
            var actual = _trace.Snapshot();
            if (Line(actual, k) is { } og) dc.DrawGeometry(null, new Pen(OptimizedBrush, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, og);
            foreach (var (r, label) in _items)
            {
                if (!_mon.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)) continue;
                var box = new Rect((r.X - _mon.X) * k - 4, (r.Y - _mon.Y) * k - 4, r.Width * k + 8, r.Height * k + 8);   // outside the pixels being matched
                dc.DrawRectangle(null, Theme.Pen(Theme.White, 5), box);
                dc.DrawRectangle(null, Theme.Pen(Theme.Playhead, 2.5), box);
                var t = Theme.Label(this, label, 12, Theme.White, bold: true);
                dc.DrawRectangle(Theme.Playhead, null, new Rect(box.X, box.Y - t.Height - 4, t.Width + 10, t.Height + 4));
                dc.DrawText(t, new Point(box.X + 5, box.Y - t.Height - 2));
            }
            int num = 0;
            foreach (var c in _trace.Clicks())
            {
                num++;
                if (!_mon.Contains(c.X, c.Y)) continue;
                var cp = new Point((c.X - _mon.X) * k, (c.Y - _mon.Y) * k);
                dc.DrawEllipse(null, new Pen(Brushes.White, 5), cp, 13, 13);
                dc.DrawEllipse(null, new Pen(Brushes.Red, 3), cp, 13, 13);
                dc.DrawEllipse(Brushes.Red, null, cp, 3, 3);
                var nt = Theme.Label(this, num.ToString(), 12, Theme.White, bold: true);
                dc.DrawRectangle(Brushes.Red, null, new Rect(cp.X + 14, cp.Y - 22, nt.Width + 8, nt.Height + 2));
                dc.DrawText(nt, new Point(cp.X + 18, cp.Y - 21));
            }
            if (actual.Length > 0 && _mon.Contains(actual[^1].X, actual[^1].Y))
                dc.DrawEllipse(OptimizedBrush, null, new Point((actual[^1].X - _mon.X) * k, (actual[^1].Y - _mon.Y) * k), 6, 6);
            if (_legend)
            {
                var t1 = Theme.Label(this, "┄ recorded path", 12, Theme.White, bold: true);
                var t2 = Theme.Label(this, "━ optimized path (what the mouse does)   ▭ located target   ◎ click", 12, Theme.White, bold: true);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(8, 8, Math.Max(t1.Width, t2.Width) + 16, t1.Height + t2.Height + 12));
                dc.DrawText(t1, new Point(16, 12)); dc.DrawText(t2, new Point(16, 14 + t1.Height));
            }
        }
    }
}
