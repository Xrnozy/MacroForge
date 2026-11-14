using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MacroStudio.Editing;
using MacroStudio.Model;

namespace MacroStudio.Views;

/// <summary>
/// Chronological lanes (mouse speed, buttons, keys, scroll) sharing one time axis with the playhead.
/// Only the visible time range is drawn, so long recordings stay responsive.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    enum LaneKind { Move, Buttons, Discord, Keys, Scroll }
    readonly record struct Lane(LaneKind Kind, string Name, double Y, double H);
    enum Mode { None, Scrub, MaybeDrag, Drag, Rubber, Pan }

    const double Gutter = 74, RulerH = 22, RowH = 18, Pad = 4;
    static readonly double[] Steps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000, 120000, 300000, 600000, 1800000, 3600000];

    readonly EditSession _s;
    double _start, _ppm = 0.1;   // visible start (ms) and pixels per ms
    bool _needFit = true;
    Mode _mode;
    Point _down;
    double _panStart;
    Dictionary<int, double>? _origT;
    int _hover = -1;

    public TimelineView(EditSession s)
    {
        _s = s;
        ClipToBounds = true;
        Focusable = true;
        s.EventsChanged += InvalidateVisual;
        s.SelectionChanged += () => { Reveal(_s.PrimaryEvent?.T); InvalidateVisual(); };
        s.PlayheadChanged += () => { Reveal(_s.PlayheadMs); InvalidateVisual(); };
    }

    public void RequestFit() { _needFit = true; InvalidateVisual(); }

    double X(double t) => Gutter + (t - _start) * _ppm;
    double T(double x) => _start + (x - Gutter) / _ppm;

    void Reveal(double? t)
    {
        if (t is not { } tt || ActualWidth < Gutter + 50) return;
        double range = (ActualWidth - Gutter) / _ppm;
        if (tt < _start || tt > _start + range) _start = tt - range * 0.15;
    }

    List<Lane> Lanes()
    {
        var a = _s.Analysis;
        var l = new List<Lane>();
        double y = RulerH;
        void Add(LaneKind k, string n, double h) { l.Add(new Lane(k, n, y, h)); y += h; }
        Add(LaneKind.Move, "Mouse", 50);
        Add(LaneKind.Buttons, "Buttons", a.ButtonRows * RowH + 2 * Pad);
        Add(LaneKind.Discord, "Steps", RowH + 2 * Pad);   // Discord messages and When blocks
        Add(LaneKind.Keys, "Keys", a.KeyRows * RowH + 2 * Pad);
        Add(LaneKind.Scroll, "Scroll", 50);
        return l;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, w, h));
        if (_needFit && w > Gutter + 50)
        {
            _ppm = Math.Clamp((w - Gutter - 20) / Math.Max(1, _s.Duration), 0.0005, 300);
            _start = -10 / _ppm;
            _needFit = false;
        }
        var ev = _s.Events;
        var lanes = Lanes();
        var a = _s.Analysis;

        dc.PushClip(new RectangleGeometry(new Rect(Gutter, 0, Math.Max(0, w - Gutter), h)));
        for (int k = 0; k < lanes.Count; k++)
            dc.DrawRectangle(k % 2 == 0 ? Theme.PanelBg : Theme.Bg, null, new Rect(Gutter, lanes[k].Y, w, lanes[k].H));
        DrawRuler(dc, w, lanes[^1].Y + lanes[^1].H);

        double t0 = T(Gutter), t1 = T(w);
        int i0 = _s.LowerBound(t0 - 2 / _ppm), i1 = Math.Min(ev.Length, _s.LowerBound(t1 + 2 / _ppm));

        Lane Of(LaneKind k) => lanes.First(l => l.Kind == k);
        DrawMoves(dc, ev, Of(LaneKind.Move), i0, i1, w);
        DrawBars(dc, ev, a, Of(LaneKind.Buttons), Of(LaneKind.Keys), i1, w);
        DrawTargets(dc, ev, Of(LaneKind.Buttons), i0, i1, discord: false);
        DrawTargets(dc, ev, Of(LaneKind.Discord), i0, i1, discord: true);
        DrawScroll(dc, ev, Of(LaneKind.Scroll), i0, i1);

        double px = X(_s.PlayheadMs);
        if (px >= Gutter - 1 && px <= w)
        {
            dc.DrawLine(Theme.PlayheadPen, new Point(px, 0), new Point(px, h));
            dc.DrawRectangle(Theme.Playhead, null, new Rect(px - 5, 0, 10, 8));
        }
        if (_mode == Mode.Rubber)
        {
            var r = new Rect(_down, _cursor);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 37, 99, 235)), Theme.Pen(Theme.Accent, 1), r);
        }
        dc.Pop();

        // gutter with lane labels over the clipped content
        dc.DrawRectangle(Theme.White, null, new Rect(0, 0, Gutter, h));
        dc.DrawLine(Theme.LinePen, new Point(Gutter, 0), new Point(Gutter, h));
        foreach (var l in lanes) dc.DrawText(Theme.Label(this, l.Name, 11, Theme.Muted), new Point(8, l.Y + 6));

        if (_hover >= 0 && _hover < ev.Length)
        {
            var t = Theme.Label(this, $"#{_hover + 1}  {ev[_hover].Describe()}   {EventInfo.Seconds(ev[_hover].T)}", 11, Theme.Text);
            dc.DrawText(t, new Point(w - t.Width - 8, 4));
        }
        else
        {
            var t = Theme.Label(this, "wheel zoom · shift+wheel / right-drag pan · drag to retime · double-click fit", 10, Theme.Muted);
            dc.DrawText(t, new Point(w - t.Width - 8, h - t.Height - 4));
        }
    }

    void DrawRuler(DrawingContext dc, double w, double bottom)
    {
        dc.DrawLine(Theme.LinePen, new Point(Gutter, RulerH), new Point(w, RulerH));
        double need = 90 / _ppm;
        double step = Steps.FirstOrDefault(s => s >= need, Steps[^1]);
        double first = Math.Max(0, Math.Ceiling(T(Gutter) / step) * step);
        for (double t = first; X(t) < w + 50; t += step)
        {
            double x = X(t);
            dc.DrawLine(Theme.LinePen, new Point(x, 0), new Point(x, bottom));
            dc.DrawText(Theme.Label(this, Theme.Time(t), 10, Theme.Muted), new Point(x + 3, 6));
        }
    }

    void DrawMoves(DrawingContext dc, MacroEvent[] ev, Lane lane, int i0, int i1, double w)
    {
        int cols = (int)Math.Max(1, w - Gutter) + 2;
        var col = new float[cols];
        int pi = i0 - 1;
        while (pi >= 0 && !ev[pi].HasPos()) pi--;
        MacroEvent? prev = pi >= 0 ? ev[pi] : null;
        for (int i = i0; i < i1; i++)
        {
            var e = ev[i];
            if (e.Kind == EventKind.RelMove)   // mouse look: height from how fast it turns
            {
                double speed = Math.Sqrt((double)e.X * e.X + (double)e.Y * e.Y) / 8;
                int c = (int)(X(e.T) - Gutter);
                if (c >= 0 && c < cols) col[c] = Math.Max(col[c], (float)Math.Min(1, Math.Log10(1 + speed)));
                continue;
            }
            if (!e.HasPos()) continue;
            if (e.Kind == EventKind.MouseMove && prev is { } p)
            {
                double dist = Math.Sqrt((double)(e.X - p.X) * (e.X - p.X) + (double)(e.Y - p.Y) * (e.Y - p.Y));
                float height = (float)Math.Min(1, Math.Log10(1 + dist / Math.Max(0.5, e.T - p.T))); // px/ms on a log scale
                int c = (int)(X(e.T) - Gutter);
                if (c >= 0 && c < cols && height > col[c]) col[c] = height;
            }
            prev = e;
        }
        var brush = Theme.Accent;
        double bottom = lane.Y + lane.H - 4, full = lane.H - 10;
        for (int c = 0; c < cols; c++)
            if (col[c] > 0) dc.DrawRectangle(brush, null, new Rect(Gutter + c, bottom - Math.Max(2, col[c] * full), 1.5, Math.Max(2, col[c] * full)));

        int shown = 0;
        foreach (var id in _s.Selected)
        {
            int ix = _s.IndexOfId(id);
            if (ix < 0 || ev[ix].Kind is not (EventKind.MouseMove or EventKind.RelMove) || shown++ > 5000) continue;
            double x = X(ev[ix].T);
            if (x >= Gutter && x <= w) dc.DrawLine(Theme.SelectPen, new Point(x, lane.Y + 3), new Point(x, lane.Y + lane.H - 3));
        }
    }

    void DrawBars(DrawingContext dc, MacroEvent[] ev, Analysis a, Lane btnLane, Lane keyLane, int i1, double w)
    {
        double endX = X(_s.Duration);
        for (int i = 0; i < i1; i++)   // bars starting after the view's right edge can never be visible
        {
            var e = ev[i];
            if (!e.IsDown()) continue;
            int p = a.Partner[i];
            bool issue = a.Issues.ContainsKey(i);
            if (p < 0 && !issue) continue; // auto-repeat key-down, not a new press
            double x0 = X(e.T), x1 = p >= 0 ? X(ev[p].T) : Math.Max(endX, x0 + 8);
            if (x1 < Gutter || x0 > w) continue;
            bool key = e.IsKey();
            var lane = key ? keyLane : btnLane;
            var r = new Rect(x0, lane.Y + Pad + a.Row[i] * RowH, Math.Max(3, x1 - x0), RowH - 4);
            bool sel = _s.Selected.Contains(e.Id) || (p >= 0 && _s.Selected.Contains(ev[p].Id));
            dc.DrawRoundedRectangle(key ? Theme.Key : Theme.ButtonBrush(e.Button), sel ? Theme.SelectPen : issue ? Theme.WarnPen : null, r, 3, 3);
            if (r.Width > 18)
            {
                var t = Theme.Label(this, e.Name(), 10, Theme.White);
                t.MaxTextWidth = r.Width - 6; t.MaxTextHeight = r.Height; t.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(t, new Point(Math.Max(r.X, Gutter) + 3, r.Y + 1));
            }
        }
    }

    static readonly Brush DiscordBrush = Theme.Brush("#5865F2"), WhenBrush = Theme.Brush("#F59E0B");

    void DrawTargets(DrawingContext dc, MacroEvent[] ev, Lane lane, int i0, int i1, bool discord)
    {
        for (int i = i0; i < i1; i++)
        {
            var e = ev[i];
            if (discord ? e.Kind is not (EventKind.Discord or EventKind.When) : !e.IsTarget()) continue;
            double x = X(e.T);
            var r = new Rect(x - 6, lane.Y + Pad, 12, RowH - 4);
            bool sel = _s.Selected.Contains(e.Id);
            dc.DrawRoundedRectangle(e.Kind == EventKind.When ? WhenBrush : discord ? DiscordBrush : Theme.Scroll, sel ? Theme.SelectPen : _s.Analysis.Issues.ContainsKey(i) ? Theme.WarnPen : null, r, 6, 6);
            dc.DrawEllipse(Theme.White, null, new Point(x, r.Y + r.Height / 2), 2.5, 2.5);
            dc.DrawText(Theme.Label(this, e.Name(), 10, Theme.Text), new Point(x + 9, r.Y + 1));
        }
    }

    void DrawScroll(DrawingContext dc, MacroEvent[] ev, Lane lane, int i0, int i1)
    {
        double mid = lane.Y + lane.H / 2;
        dc.DrawLine(Theme.LinePen, new Point(Gutter, mid), new Point(ActualWidth, mid));
        for (int i = i0; i < i1; i++)
        {
            var e = ev[i];
            if (!e.IsWheel()) continue;
            double x = X(e.T);
            bool sel = _s.Selected.Contains(e.Id);
            var pen = Theme.Pen(sel ? Theme.Select : Theme.Scroll, sel ? 3 : 2);
            int dir = e.Delta >= 0 ? -1 : 1; // screen y grows downward; up-scroll draws above the line
            if (e.Kind == EventKind.Wheel)
            {
                double len = 5 + Math.Min(15, Math.Abs(e.Delta) / 120.0 * 6);
                dc.DrawLine(pen, new Point(x, mid), new Point(x, mid + dir * len));
                dc.DrawEllipse(sel ? Theme.Select : Theme.Scroll, null, new Point(x, mid + dir * len), 2.5, 2.5);
            }
            else
            {
                int right = e.Delta >= 0 ? 1 : -1;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x + right * 6, mid), true, true);
                    c.LineTo(new Point(x - right * 3, mid - 5), true, false);
                    c.LineTo(new Point(x - right * 3, mid + 5), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(sel ? Theme.Select : Theme.Scroll, null, g);
            }
        }
    }

    // ---- interaction ----

    Point _cursor;

    int HitTest(Point p)
    {
        var ev = _s.Events;
        var a = _s.Analysis;
        var lane = Lanes().Cast<Lane?>().FirstOrDefault(l => p.Y >= l!.Value.Y && p.Y < l.Value.Y + l.Value.H);
        if (lane is not { } ln || p.X < Gutter) return -1;
        double t = T(p.X), tol = 5 / _ppm;
        if (ln.Kind is LaneKind.Move or LaneKind.Scroll)
        {
            int best = -1; double bd = tol;
            for (int i = _s.LowerBound(t - tol); i < ev.Length && ev[i].T <= t + tol; i++)
            {
                bool match = ln.Kind == LaneKind.Move ? ev[i].Kind is EventKind.MouseMove or EventKind.RelMove : ev[i].IsWheel();
                if (match && Math.Abs(ev[i].T - t) <= bd) { bd = Math.Abs(ev[i].T - t); best = i; }
            }
            return best;
        }
        int row = (int)((p.Y - ln.Y - Pad) / RowH);
        bool key = ln.Kind == LaneKind.Keys;
        if (ln.Kind is LaneKind.Buttons or LaneKind.Discord && (row == 0 || ln.Kind == LaneKind.Discord)) // target-click markers sit on the first button row, Discord messages in their own lane
            for (int i = _s.LowerBound(t - 8 / _ppm); i < ev.Length && ev[i].T <= t + 8 / _ppm; i++)
                if (ln.Kind == LaneKind.Discord ? ev[i].Kind is EventKind.Discord or EventKind.When : ev[i].IsTarget()) return i;
        if (ln.Kind == LaneKind.Discord) return -1;
        int hi = Math.Min(ev.Length, _s.LowerBound(t + tol + 0.001));   // a bar under the cursor starts before t + tol: later events cannot match
        for (int i = 0; i < hi; i++)
        {
            var e = ev[i];
            if (!e.IsDown() || e.IsKey() != key || a.Row[i] != row) continue;
            int pt = a.Partner[i];
            double end = pt >= 0 ? ev[pt].T : _s.Duration + 8 / _ppm;
            if (t >= e.T - tol && t <= Math.Max(end, e.T + 3 / _ppm) + tol && (pt >= 0 || a.Issues.ContainsKey(i))) return i;
        }
        return -1;
    }

    void SelectHit(int hit, bool toggle)
    {
        var e = _s.Events[hit];
        var set = _s.WithPartners([e.Id]);
        if (toggle) { if (_s.Selected.Contains(e.Id)) { _s.Select(_s.Selected.Except(set).ToList()); } else _s.Select(_s.Selected.Union(set).ToList(), e.Id); }
        else if (!_s.Selected.Contains(e.Id)) _s.Select(set, e.Id);
        else _s.Select(_s.Selected.ToList(), e.Id);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { _start -= e.Delta / 120.0 * 80 / _ppm; InvalidateVisual(); return; }
        double t = T(Math.Max(p.X, Gutter));
        _ppm = Math.Clamp(_ppm * Math.Pow(1.2, e.Delta / 120.0), 0.0005, 300);
        _start = t - (Math.Max(p.X, Gutter) - Gutter) / _ppm;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _down = _cursor = p;
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { RequestFit(); return; }
        if (e.ChangedButton != MouseButton.Left) { _mode = Mode.Pan; _panStart = _start; CaptureMouse(); return; }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (p.Y < RulerH && p.X >= Gutter) { _mode = Mode.Scrub; _s.SetPlayhead(T(p.X)); CaptureMouse(); return; }
        int hit = HitTest(p);
        if (hit >= 0)
        {
            SelectHit(hit, ctrl);
            _origT = _s.Selected.Select(_s.IndexOfId).Where(i => i >= 0).ToDictionary(i => _s.Events[i].Id, i => _s.Events[i].T);
            _mode = Mode.MaybeDrag;
        }
        else
        {
            if (!ctrl) _s.ClearSelection();
            if (p.X >= Gutter) _s.SetPlayhead(T(p.X));
            _mode = Mode.Rubber;
        }
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _cursor = p;
        switch (_mode)
        {
            case Mode.Scrub: _s.SetPlayhead(T(Math.Max(p.X, Gutter))); break;
            case Mode.Pan: _start = _panStart - (p.X - _down.X) / _ppm; break;
            case Mode.MaybeDrag or Mode.Drag when _origT != null:
                if (_mode == Mode.MaybeDrag && Math.Abs(p.X - _down.X) > 3) _mode = Mode.Drag;
                if (_mode == Mode.Drag)
                {
                    var o = _origT;
                    double dt = Math.Max((p.X - _down.X) / _ppm, -o.Values.Min()); // keep every moved event at t >= 0
                    _s.Modify(o.Keys.ToList(), ev => o.TryGetValue(ev.Id, out var t0) ? ev with { T = t0 + dt } : ev, "timedrag");
                }
                break;
            case Mode.None: _hover = HitTest(p); break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_mode == Mode.Drag) _s.EndCoalesce();
        if (_mode == Mode.Rubber && (Math.Abs(_cursor.X - _down.X) > 3 || Math.Abs(_cursor.Y - _down.Y) > 3)) RubberSelect();
        _mode = Mode.None; _origT = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    void RubberSelect()
    {
        var r = new Rect(_down, _cursor);
        double t0 = T(Math.Max(r.Left, Gutter)), t1 = T(r.Right);
        var kinds = Lanes().Where(l => l.Y < r.Bottom && l.Y + l.H > r.Top).Select(l => l.Kind).ToHashSet();
        var ev = _s.Events;
        var ids = new List<int>();
        for (int i = _s.LowerBound(t0); i < ev.Length && ev[i].T <= t1; i++)
        {
            var e = ev[i];
            bool inLane = e.Kind is EventKind.MouseMove or EventKind.RelMove ? kinds.Contains(LaneKind.Move)
                : e.Kind is EventKind.Discord or EventKind.When ? kinds.Contains(LaneKind.Discord)
                : e.IsButton() || e.IsTarget() ? kinds.Contains(LaneKind.Buttons)
                : e.IsKey() ? kinds.Contains(LaneKind.Keys)
                : kinds.Contains(LaneKind.Scroll);
            if (inLane) ids.Add(e.Id);
        }
        var set = _s.WithPartners(ids);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) set.UnionWith(_s.Selected);
        _s.Select(set);
    }

    protected override void OnMouseLeave(MouseEventArgs e) { if (_mode == Mode.None) { _hover = -1; InvalidateVisual(); } }
}
