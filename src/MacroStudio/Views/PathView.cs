using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MacroStudio.Editing;
using MacroStudio.Model;

namespace MacroStudio.Views;

/// <summary>
/// Interactive map of the recording in screen coordinates: cursor path colored by time, direction arrows,
/// click and scroll markers, start/end, selection and playhead. Drag selected points to edit coordinates.
/// </summary>
public sealed class PathView : FrameworkElement
{
    const int Buckets = 48;
    static readonly Pen[] BucketPens = Enumerable.Range(0, Buckets).Select(i => Theme.Pen(Theme.Brush(Theme.TimeColor((i + 0.5) / Buckets).ToString()), 1.6)).ToArray();
    static readonly Brush[] BucketBrushes = Enumerable.Range(0, Buckets).Select(i => (Brush)Theme.Brush(Theme.TimeColor((i + 0.5) / Buckets).ToString())).ToArray();

    readonly EditSession _s;
    double _scale = 1, _ox, _oy;
    bool _needFit = true;
    int _hover = -1;
    Point _mouse;
    Point? _panStart;
    (double ox, double oy) _panOrigin;
    bool _leftDown, _dragging;
    Point _downPt;

    // ---- transform box (move / scale / rotate the selected points together, like a layer in an image editor) ----
    enum Grab { None, Move, Scale, Rotate }
    sealed class Drag
    {
        public Grab Kind; public int Hx, Hy;                       // Scale: which handle (-1, 0, +1 on each axis)
        public Dictionary<int, MacroEvent> Origin = new();         // the events as they were when the drag began
        public (double X, double Y, double W, double H) Box;       // their bounding box then (desktop coordinates)
        public (double X, double Y) Down;                          // the mouse then (desktop coordinates)
        public Xf Current; public bool Moved; public string Readout = "";
    }
    Drag? _xf;
    int _boxVersion = -1, _boxSel = -1; (double X, double Y, double W, double H)? _boxCache;
    const double HandleR = 5, RotateGap = 28;

    /// <summary>The box around the selected positioned events; only for two or more of them (a single point has the plain point drag).</summary>
    bool Gizmo(out (double X, double Y, double W, double H) box)
    {
        if (_boxVersion != _s.Version || _boxSel != _s.Selected.Count)
        {
            _boxVersion = _s.Version; _boxSel = _s.Selected.Count;
            var picked = _s.Selected.Select(_s.IndexOfId).Where(i => i >= 0).Select(i => _s.Events[i]).Where(e => e.HasPos()).ToList();
            _boxCache = picked.Count >= 2 ? Xf.BoundsOf(picked) : null;
        }
        box = _boxCache ?? default;
        return _boxCache != null;
    }

    static (double x, double y)[] Corners((double X, double Y, double W, double H) b) => [(b.X, b.Y), (b.X + b.W, b.Y), (b.X + b.W, b.Y + b.H), (b.X, b.Y + b.H)];   // TL, TR, BR, BL

    /// <summary>The corners, edge midpoints and the rotate handle in screen pixels; follows the live transform while dragging.</summary>
    (Point[] corners, Point[] mids, Point rotate, Point center) GizmoPoints((double X, double Y, double W, double H) box)
    {
        var c = Corners(box).Select(q => { var m = _xf != null ? _xf.Current.Map(q.x, q.y) : (X: q.x, Y: q.y); return ToScreen(m.X, m.Y); }).ToArray();
        var mids = new[] { Mid(c[0], c[1]), Mid(c[1], c[2]), Mid(c[2], c[3]), Mid(c[3], c[0]) };      // top, right, bottom, left
        var up = new Vector(mids[0].X - mids[2].X, mids[0].Y - mids[2].Y); if (up.Length < 1e-6) up = new Vector(0, -1); up.Normalize();
        return (c, mids, new Point(mids[0].X + up.X * RotateGap, mids[0].Y + up.Y * RotateGap), Mid(c[0], c[2]));
    }

    static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>What is under the mouse: a handle, the inside of the box, or nothing. Handles are tested first.</summary>
    (Grab kind, int hx, int hy) GizmoAt(Point p)
    {
        if (!Gizmo(out var box)) return (Grab.None, 0, 0);
        var g = GizmoPoints(box);
        if (Dist(p, g.rotate) <= 9) return (Grab.Rotate, 0, 0);
        int[][] dirs = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
        for (int i = 0; i < 4; i++) if (Dist(p, g.corners[i]) <= 8) return (Grab.Scale, dirs[i][0], dirs[i][1]);
        int[][] edges = [[0, -1], [1, 0], [0, 1], [-1, 0]];
        for (int i = 0; i < 4; i++) if (Dist(p, g.mids[i]) <= 8) return (Grab.Scale, edges[i][0], edges[i][1]);
        var r = new Rect(g.corners[0], g.corners[2]); r.Inflate(4, 4);
        return r.Contains(p) ? (Grab.Move, 0, 0) : (Grab.None, 0, 0);
    }

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    void BeginDrag(Grab kind, int hx, int hy, Point p)
    {
        if (!Gizmo(out var box)) { box = default; }
        var origin = _s.Selected.Select(_s.IndexOfId).Where(i => i >= 0).Select(i => _s.Events[i]).ToDictionary(e => e.Id);
        var (dx, dy) = ToDesktop(p);
        _xf = new Drag { Kind = kind, Hx = hx, Hy = hy, Origin = origin, Box = box, Down = (dx, dy), Current = new Xf(0, 0) };
        _leftDown = true; _dragging = kind != Grab.Move; _downPt = p;
        CaptureMouse();
    }

    /// <summary>The transform that the mouse position implies for the drag in progress.</summary>
    Xf Solve(Drag d, Point p)
    {
        var (mx, my) = ToDesktop(p);
        var b = d.Box; double cx = b.X + b.W / 2, cy = b.Y + b.H / 2;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (d.Kind)
        {
            case Grab.Move:
                return new Xf(0, 0, Dx: Math.Round(mx - d.Down.X), Dy: Math.Round(my - d.Down.Y));
            case Grab.Rotate:
            {
                double a = (Math.Atan2(my - cy, mx - cx) - Math.Atan2(d.Down.Y - cy, d.Down.X - cx)) * 180 / Math.PI;
                a = ((a + 540) % 360) - 180;
                if (shift) a = Math.Round(a / 15) * 15;
                return new Xf(cx, cy, RotDeg: a);
            }
            default:
            {
                // the opposite edge or corner stays put (Alt: the centre does)
                double ax = alt || d.Hx == 0 ? cx : d.Hx > 0 ? b.X : b.X + b.W, ay = alt || d.Hy == 0 ? cy : d.Hy > 0 ? b.Y : b.Y + b.H;
                double dx0 = d.Down.X - ax, dy0 = d.Down.Y - ay, dx1 = mx - ax, dy1 = my - ay;
                double sx = d.Hx == 0 || Math.Abs(dx0) < 1e-6 ? 1 : dx1 / dx0, sy = d.Hy == 0 || Math.Abs(dy0) < 1e-6 ? 1 : dy1 / dy0;
                if (d.Hx != 0 && d.Hy != 0 && !shift)                       // corners keep the proportions unless Shift is held
                {
                    double den = dx0 * dx0 + dy0 * dy0, k = den < 1e-6 ? 1 : (dx1 * dx0 + dy1 * dy0) / den;
                    sx = sy = k;
                }
                return new Xf(ax, ay, sx, sy);
            }
        }
    }

    void DrawGizmo(DrawingContext dc)
    {
        if (!Gizmo(out var box)) return;
        var box0 = _xf != null ? _xf.Box : box;
        var g = GizmoPoints(box0);
        var pen = new Pen(Theme.Accent, 1.4) { DashStyle = DashStyles.Dash };
        var quad = new StreamGeometry();
        using (var ctx = quad.Open()) { ctx.BeginFigure(g.corners[0], false, true); for (int i = 1; i < 4; i++) ctx.LineTo(g.corners[i], true, false); }
        quad.Freeze();
        dc.DrawGeometry(null, pen, quad);
        dc.DrawLine(Theme.Pen(Theme.Accent, 1.2), g.mids[0], g.rotate);
        dc.DrawEllipse(Theme.White, Theme.Pen(Theme.Accent, 1.6), g.rotate, 6, 6);
        var hp = Theme.Pen(Theme.Accent, 1.6);
        foreach (var q in g.corners.Concat(g.mids)) dc.DrawRectangle(Theme.White, hp, new Rect(q.X - HandleR, q.Y - HandleR, HandleR * 2, HandleR * 2));
        dc.DrawLine(Theme.Pen(Theme.Accent, 1.2), new Point(g.center.X - 5, g.center.Y), new Point(g.center.X + 5, g.center.Y));
        dc.DrawLine(Theme.Pen(Theme.Accent, 1.2), new Point(g.center.X, g.center.Y - 5), new Point(g.center.X, g.center.Y + 5));
        if (_xf is { Moved: true } d && d.Readout.Length > 0)
        {
            var t = Theme.Label(this, d.Readout, 11, Theme.White, bold: true);
            var at = new Point(Math.Min(_mouse.X + 14, ActualWidth - t.Width - 14), Math.Max(4, _mouse.Y - t.Height - 14));
            dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(at.X - 6, at.Y - 3, t.Width + 12, t.Height + 6), 4, 4);
            dc.DrawText(t, at);
        }
    }

    Cursor CursorFor((Grab kind, int hx, int hy) h) => h.kind switch
    {
        Grab.Move => Cursors.SizeAll,
        Grab.Rotate => Cursors.Cross,
        Grab.Scale => (h.hx, h.hy) switch { (0, _) => Cursors.SizeNS, (_, 0) => Cursors.SizeWE, var (x, y) when x == y => Cursors.SizeNWSE, _ => Cursors.SizeNESW },
        _ => null!,
    };

    public PathView(EditSession s)
    {
        _s = s;
        ClipToBounds = true;
        Focusable = true;
        s.EventsChanged += InvalidateVisual;
        s.PlayheadChanged += InvalidateVisual;
        s.SelectionChanged += () => { Follow(); InvalidateVisual(); };
    }

    public void RequestFit() { _needFit = true; InvalidateVisual(); }

    Point ToScreen(double x, double y) => new(x * _scale + _ox, y * _scale + _oy);
    (double x, double y) ToDesktop(Point p) => ((p.X - _ox) / _scale, (p.Y - _oy) / _scale);

    void Fit()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        if (_s.Desktop != null) { var v = _s.Desktop.Virtual; Add(v.X, v.Y); Add(v.X + v.Width, v.Y + v.Height); }
        foreach (var i in _s.PosIndex) Add(_s.Events[i].X, _s.Events[i].Y);
        if (minX > maxX || ActualWidth < 10 || ActualHeight < 10) return;
        double bw = Math.Max(1, maxX - minX), bh = Math.Max(1, maxY - minY);
        _scale = Math.Min((ActualWidth - 40) / bw, (ActualHeight - 40) / bh);
        _ox = (ActualWidth - bw * _scale) / 2 - minX * _scale;
        _oy = (ActualHeight - bh * _scale) / 2 - minY * _scale;
        _needFit = false;
    }

    void Follow()
    {
        var e = _s.PrimaryEvent;
        if (e is not { } ev || !ev.HasPos()) return;
        var p = ToScreen(ev.X, ev.Y);
        if (p.X < 20 || p.Y < 20 || p.X > ActualWidth - 20 || p.Y > ActualHeight - 20)
        {
            _ox += ActualWidth / 2 - p.X;
            _oy += ActualHeight / 2 - p.Y;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, w, h));
        if (_needFit) Fit();
        var ev = _s.Events;
        if (ev.Length == 0)
        {
            Centered(dc, "No events. Record a macro or open one from the library.", w, h);
            return;
        }

        if (_s.Desktop != null)
        {
            int n = 1;
            foreach (var m in _s.Desktop.Monitors)
            {
                var a = ToScreen(m.X, m.Y);
                var r = new Rect(a, new Size(m.Width * _scale, m.Height * _scale));
                dc.DrawRectangle(Theme.Monitor, Theme.LinePen, r);
                var label = Theme.Label(this, $"Monitor {n++}  {m.Width}×{m.Height}", 11, Theme.Muted);
                dc.DrawText(label, new Point(r.X + 6, r.Bottom - label.Height - 4)); // bottom-left keeps the legend (top-left) readable
            }
        }

        EnsureCache(ev);   // the O(events) geometry rebuild happens only when events or the view transform change: playhead and hover ticks reuse it
        DrawPath(dc);
        DrawMarkers(dc, ev);
        DrawSelection(dc, ev);
        DrawGizmo(dc);
        DrawPlayhead(dc);
        DrawOverlay(dc, ev, w, h);
    }

    // ---- cached path & marker geometry: rebuilding it costs O(events), so it runs once per (version, transform) ----
    StreamGeometry[]? _lines, _arrows;
    List<(double X, double Y, int I)>? _markers;
    int _cacheVer = -1; double _cacheScale, _cacheOx, _cacheOy;
    internal int CacheBuilds;   // test hook: how often the expensive rebuild actually ran

    void EnsureCache(MacroEvent[] ev)
    {
        if (_cacheVer == _s.Version && _cacheScale == _scale && _cacheOx == _ox && _cacheOy == _oy) return;
        _cacheVer = _s.Version; _cacheScale = _scale; _cacheOx = _ox; _cacheOy = _oy;
        CacheBuilds++;
        BuildPath(ev);
        BuildMarkers(ev);
    }

    void Centered(DrawingContext dc, string text, double w, double h)
    {
        var t = Theme.Label(this, text, 13, Theme.Muted);
        dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
    }

    void BuildPath(MacroEvent[] ev)
    {
        _lines = null; _arrows = null;
        var pi = _s.PosIndex;
        if (pi.Length < 2) return;
        double dur = Math.Max(1, _s.Duration);
        var lines = new StreamGeometry[Buckets];
        var arrows = new StreamGeometry[Buckets];
        var lctx = new StreamGeometryContext[Buckets];
        var actx = new StreamGeometryContext[Buckets];
        int cur = -1;
        double acc = 0;
        var last = ToScreen(ev[pi[0]].X, ev[pi[0]].Y);
        for (int k = 1; k < pi.Length; k++)
        {
            var e = ev[pi[k]];
            var pt = ToScreen(e.X, e.Y);
            double dx = pt.X - last.X, dy = pt.Y - last.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0 && k != pi.Length - 1) continue; // level of detail: sub-pixel steps add nothing
            int b = Math.Min(Buckets - 1, (int)(e.T / dur * Buckets));
            if (lines[b] == null) { lines[b] = new StreamGeometry(); lctx[b] = lines[b].Open(); arrows[b] = new StreamGeometry(); actx[b] = arrows[b].Open(); }
            if (b != cur) { lctx[b].BeginFigure(last, false, false); cur = b; }
            lctx[b].LineTo(pt, true, false);
            acc += len;
            if (acc >= 90 && len > 0)
            {
                acc = 0;
                double ux = dx / len, uy = dy / len;
                actx[b].BeginFigure(pt, true, true);
                actx[b].LineTo(new Point(pt.X - ux * 9 - uy * 4.5, pt.Y - uy * 9 + ux * 4.5), true, false);
                actx[b].LineTo(new Point(pt.X - ux * 9 + uy * 4.5, pt.Y - uy * 9 - ux * 4.5), true, false);
            }
            last = pt;
        }
        for (int b = 0; b < Buckets; b++)
        {
            if (lines[b] == null) continue;
            lctx[b].Close(); actx[b].Close();
            lines[b].Freeze(); arrows[b].Freeze();
        }
        _lines = lines; _arrows = arrows;
    }

    void DrawPath(DrawingContext dc)
    {
        if (_lines == null) return;
        for (int b = 0; b < Buckets; b++)
        {
            if (_lines[b] == null) continue;
            dc.DrawGeometry(null, BucketPens[b], _lines[b]);
            dc.DrawGeometry(BucketBrushes[b], null, _arrows![b]);
        }
    }

    /// <summary>Screen position of every button/scroll marker, computed once per transform like the path.</summary>
    void BuildMarkers(MacroEvent[] ev)
    {
        var m = new List<(double X, double Y, int I)>();
        for (int i = 0; i < ev.Length; i++)
        {
            var e = ev[i];
            if (e.IsButton()) { var p = ToScreen(e.X, e.Y); m.Add((p.X, p.Y, i)); }
            else if (e.IsWheel()) { var p = WheelAnchor(e); m.Add((p.X, p.Y, i)); }
        }
        _markers = m;
    }

    void DrawMarkers(DrawingContext dc, MacroEvent[] ev)
    {
        double w = ActualWidth, h = ActualHeight;
        if (_markers != null)
            foreach (var (x, y, i) in _markers)
            {
                if (x < -20 || y < -20 || x > w + 20 || y > h + 20) continue;
                var e = ev[i];
                if (e.Kind == EventKind.ButtonDown) dc.DrawEllipse(Theme.ButtonBrush(e.Button), Theme.Pen(Theme.White, 1.5), new Point(x, y), 6, 6);
                else if (e.Kind == EventKind.ButtonUp) dc.DrawEllipse(Theme.White, Theme.Pen(Theme.ButtonBrush(e.Button), 2), new Point(x, y), 4, 4);
                else DrawScrollGlyph(dc, e, new Point(x, y));
            }
        var pi = _s.PosIndex;
        if (pi.Length > 0)
        {
            var a = ev[pi[0]]; var z = ev[pi[^1]];
            var pa = ToScreen(a.X, a.Y); var pz = ToScreen(z.X, z.Y);
            dc.DrawEllipse(Brushes.ForestGreen, Theme.Pen(Theme.White, 2), pa, 7, 7);
            dc.DrawText(Theme.Label(this, "START", 10, Brushes.ForestGreen, bold: true), new Point(pa.X + 10, pa.Y - 16));
            dc.DrawRectangle(Theme.Warn, Theme.Pen(Theme.White, 2), new Rect(pz.X - 6, pz.Y - 6, 12, 12));
            dc.DrawText(Theme.Label(this, "END", 10, Theme.Warn, bold: true), new Point(pz.X + 10, pz.Y + 4));
        }
    }

    Point WheelAnchor(MacroEvent e) { var p = ToScreen(e.X, e.Y); return new Point(p.X + 13, p.Y); }

    void DrawScrollGlyph(DrawingContext dc, MacroEvent e, Point c)
    {
        double s = 5 + Math.Min(8, Math.Abs(e.Delta) / 120.0 * 2.5);
        int dir = e.Delta >= 0 ? 1 : -1;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (e.Kind == EventKind.Wheel) // up = tip above, down = tip below
            {
                ctx.BeginFigure(new Point(c.X, c.Y - dir * s), true, true);
                ctx.LineTo(new Point(c.X - s * 0.8, c.Y + dir * s * 0.6), true, false);
                ctx.LineTo(new Point(c.X + s * 0.8, c.Y + dir * s * 0.6), true, false);
            }
            else // right / left
            {
                ctx.BeginFigure(new Point(c.X + dir * s, c.Y), true, true);
                ctx.LineTo(new Point(c.X - dir * s * 0.6, c.Y - s * 0.8), true, false);
                ctx.LineTo(new Point(c.X - dir * s * 0.6, c.Y + s * 0.8), true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(Theme.Scroll, Theme.Pen(Theme.White, 1), g);
    }

    void DrawSelection(DrawingContext dc, MacroEvent[] ev)
    {
        int shown = 0;
        foreach (var id in _s.Selected)
        {
            int i = _s.IndexOfId(id);
            if (i < 0 || !ev[i].HasPos() || shown++ > 3000) continue;
            var p = ev[i].IsWheel() ? WheelAnchor(ev[i]) : ToScreen(ev[i].X, ev[i].Y);
            dc.DrawEllipse(null, Theme.SelectPen, p, id == _s.Primary ? 11 : 9, id == _s.Primary ? 11 : 9);
        }
        int pi = _s.IndexOfId(_s.Primary);
        if (pi >= 0 && ev[pi].HasPos()) Callout(dc, pi, ToScreen(ev[pi].X, ev[pi].Y));
    }

    void Callout(DrawingContext dc, int i, Point at)
    {
        var e = _s.Events[i];
        double prevT = i > 0 ? _s.Events[i - 1].T : 0;
        var t = Theme.Label(this, $"#{i + 1}  {e.Describe()}\n{EventInfo.Seconds(e.T)}   Δ {(e.T - prevT):0.0} ms", 11, Theme.Text);
        double x = Math.Min(at.X + 14, ActualWidth - t.Width - 14), y = Math.Max(4, Math.Min(at.Y - t.Height - 14, ActualHeight - t.Height - 10));
        dc.DrawRoundedRectangle(Theme.White, Theme.LinePen, new Rect(x - 6, y - 3, t.Width + 12, t.Height + 6), 4, 4);
        dc.DrawText(t, new Point(x, y));
    }

    void DrawPlayhead(DrawingContext dc)
    {
        if (_s.PositionAt(_s.PlayheadMs) is not { } pos) return;
        var p = ToScreen(pos.X, pos.Y);
        dc.DrawEllipse(null, Theme.PlayheadPen, p, 8, 8);
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X - 13, p.Y), new Point(p.X - 4, p.Y));
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X + 4, p.Y), new Point(p.X + 13, p.Y));
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X, p.Y - 13), new Point(p.X, p.Y - 4));
        dc.DrawLine(Theme.PlayheadPen, new Point(p.X, p.Y + 4), new Point(p.X, p.Y + 13));
    }

    void DrawOverlay(DrawingContext dc, MacroEvent[] ev, double w, double h)
    {
        if (_hover >= 0 && _hover < ev.Length)
        {
            var e = ev[_hover];
            var p = e.IsWheel() ? WheelAnchor(e) : ToScreen(e.X, e.Y);
            dc.DrawEllipse(null, Theme.Pen(Theme.Text, 1.5), p, 9, 9);
            if (_hover != _s.IndexOfId(_s.Primary)) Callout(dc, _hover, ToScreen(e.X, e.Y));
        }
        // legend: path colour = time
        var grad = new LinearGradientBrush(Theme.TimeColor(0), Theme.TimeColor(1), 0);
        grad.GradientStops.Insert(1, new GradientStop(Theme.TimeColor(0.5), 0.5));
        dc.DrawRoundedRectangle(grad, null, new Rect(10, 10, 110, 7), 3, 3);
        dc.DrawText(Theme.Label(this, "start", 10, Theme.Muted), new Point(10, 19));
        var end = Theme.Label(this, EventInfo.Seconds(_s.Duration), 10, Theme.Muted);
        dc.DrawText(end, new Point(120 - end.Width, 19));
        dc.DrawText(Theme.Label(this, "● press   ○ release   ▲▼◀▶ scroll   ◯ playhead", 10, Theme.Muted), new Point(10, 34));
        var (dx, dy) = ToDesktop(_mouse);
        var info = Theme.Label(this, $"{dx:0}, {dy:0}   zoom {_scale * 100:0}%   wheel zoom · drag pan · double-click fit", 10, Theme.Muted);
        dc.DrawText(info, new Point(w - info.Width - 10, h - info.Height - 6));
    }

    // ---- interaction ----

    int HitTest(Point p)
    {
        var ev = _s.Events;
        int best = -1; double bestD = 10;
        foreach (var i in _s.PosIndex)
        {
            var e = ev[i];
            var q = e.IsWheel() ? WheelAnchor(e) : ToScreen(e.X, e.Y);
            if (Math.Abs(q.X - p.X) > 12 || Math.Abs(q.Y - p.Y) > 12) continue;
            double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
            if (e.Kind != EventKind.MouseMove) d -= 4; // markers win over path points underneath
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double f = Math.Pow(1.15, e.Delta / 120.0), ns = Math.Clamp(_scale * f, 0.02, 60);
        f = ns / _scale;
        _ox = p.X - (p.X - _ox) * f;
        _oy = p.Y - (p.Y - _oy) * f;
        _scale = ns;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { RequestFit(); return; }
        if (e.ChangedButton != MouseButton.Left) { StartPan(p); return; }
        // the transform box first: its handles scale / rotate the whole selection, and a drag inside it moves it
        var gz = GizmoAt(p);
        int hit = HitTest(p);
        if (gz.kind is Grab.Scale or Grab.Rotate || (gz.kind == Grab.Move && (hit < 0 || _s.Selected.Contains(_s.Events[hit].Id)) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            BeginDrag(gz.kind, gz.hx, gz.hy, p);
            return;
        }
        if (hit >= 0)
        {
            int id = _s.Events[hit].Id;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _s.Toggle(id);
            else if (!_s.Selected.Contains(id)) _s.SelectOne(id);
            else _s.Select(_s.Selected.ToList(), id);
            BeginDrag(Grab.Move, 0, 0, p);            // dragging any selected point moves the selection
        }
        else
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _s.ClearSelection();
            StartPan(p);
        }
    }

    void StartPan(Point p) { _panStart = p; _panOrigin = (_ox, _oy); CaptureMouse(); Cursor = Cursors.SizeAll; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _mouse = p;
        if (_panStart is { } ps)
        {
            _ox = _panOrigin.ox + p.X - ps.X;
            _oy = _panOrigin.oy + p.Y - ps.Y;
        }
        else if (_leftDown && _xf is { } d)
        {
            if (!d.Moved && (d.Kind != Grab.Move || Math.Abs(p.X - _downPt.X) > 3 || Math.Abs(p.Y - _downPt.Y) > 3)) { d.Moved = true; _dragging = true; }
            if (d.Moved)
            {
                var xf = Solve(d, p);
                d.Current = xf;
                d.Readout = d.Kind switch
                {
                    Grab.Move => $"Move {xf.Dx:+0;-0;0}, {xf.Dy:+0;-0;0}",
                    Grab.Rotate => $"Rotate {xf.RotDeg:0.#}°",
                    _ => Math.Abs(xf.Sx - xf.Sy) < 1e-9 ? $"Scale {xf.Sx * 100:0}%" : $"Scale {xf.Sx * 100:0}% × {xf.Sy * 100:0}%",
                };
                var org = d.Origin;
                _s.Modify(org.Keys.ToList(), ev => org.TryGetValue(ev.Id, out var o) ? xf.Apply(o) : ev, "xform");   // always from the original positions
            }
        }
        else { _hover = HitTest(p); Cursor = CursorFor(GizmoAt(p)); }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragging) _s.EndCoalesce();
        _leftDown = _dragging = false; _xf = null; _panStart = null; _boxVersion = -1;
        Cursor = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; if (_xf == null) Cursor = null; InvalidateVisual(); }
}
