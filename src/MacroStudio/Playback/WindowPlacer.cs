using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>
/// Where this app's own window should sit while a macro plays with the window kept visible: on the monitor (or the part of one)
/// that the mouse paths and targets do not use. Pure geometry, no windows involved.
/// </summary>
public static class WindowPlacer
{
    const int Cell = 24, Step = 48;

    /// <summary>Points along the path (every ~12 px between consecutive positions), so a rectangle is "busy" if the line crosses it, not only its corners.</summary>
    public static List<(int X, int Y)> Along(IEnumerable<(int X, int Y)> positions)
    {
        var l = new List<(int, int)>();
        (int X, int Y)? prev = null;
        foreach (var p in positions)
        {
            if (prev is { } q)
            {
                double d = Math.Sqrt((double)(p.X - q.X) * (p.X - q.X) + (double)(p.Y - q.Y) * (p.Y - q.Y));
                int n = (int)(d / 12);
                for (int k = 1; k <= n; k++) l.Add((q.X + (int)((p.X - q.X) * (double)k / (n + 1)), q.Y + (int)((p.Y - q.Y) * (double)k / (n + 1))));
            }
            l.Add(p); prev = p;
        }
        return l;
    }

    /// <summary>A rectangle of busy points (a target), on a coarse lattice.</summary>
    public static IEnumerable<(int X, int Y)> Fill(ScreenRect r)
    {
        for (int y = r.Y; y <= r.Y + r.Height; y += 12) for (int x = r.X; x <= r.X + r.Width; x += 12) yield return (x, y);
    }

    /// <summary>How many busy points lie inside the rectangle (0 = nothing in the way).</summary>
    public static int Cost(ScreenRect r, IReadOnlyList<(int X, int Y)> busy)
    {
        int c = 0;
        foreach (var (x, y) in busy) if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) c++;
        return c;
    }

    /// <summary>
    /// The least obstructive rectangle: tries the wanted size first on every monitor, then smaller sizes (never below the minimum, never larger than the monitor),
    /// and returns the first completely free spot found at the largest size; when nothing is free, the one with the fewest busy points.
    /// </summary>
    public static (ScreenRect Rect, int Cost) Choose(IReadOnlyList<ScreenRect> monitors, IReadOnlyList<(int X, int Y)> busy, int width, int height, int minW = 480, int minH = 340)
    {
        (ScreenRect Rect, int Cost, double Score)? best = null;
        foreach (var mon in monitors)
        {
            // summed-area table of busy cells for this monitor
            int cw = (mon.Width + Cell - 1) / Cell + 1, ch = (mon.Height + Cell - 1) / Cell + 1;
            var cells = new int[cw + 1, ch + 1];
            foreach (var (x, y) in busy)
                if (x >= mon.X && x < mon.X + mon.Width && y >= mon.Y && y < mon.Y + mon.Height) cells[(x - mon.X) / Cell + 1, (y - mon.Y) / Cell + 1]++;
            for (int i = 1; i <= cw; i++) for (int j = 1; j <= ch; j++) cells[i, j] += cells[i - 1, j] + cells[i, j - 1] - cells[i - 1, j - 1];
            int Sum(int x0, int y0, int x1, int y1) // cells, inclusive-exclusive
            {
                x0 = Math.Clamp(x0, 0, cw); x1 = Math.Clamp(x1, 0, cw); y0 = Math.Clamp(y0, 0, ch); y1 = Math.Clamp(y1, 0, ch);
                return cells[x1, y1] - cells[x0, y1] - cells[x1, y0] + cells[x0, y0];
            }

            foreach (double scale in new[] { 1, 0.85, 0.7, 0.55, 0 })
            {
                int w = Math.Min(mon.Width, scale == 0 ? minW : Math.Max(minW, (int)(width * scale)));
                int h = Math.Min(mon.Height, scale == 0 ? minH : Math.Max(minH, (int)(height * scale)));
                for (int y = mon.Y; ; y += Step)
                {
                    int yy = Math.Min(y, mon.Y + mon.Height - h);
                    for (int x = mon.X; ; x += Step)
                    {
                        int xx = Math.Min(x, mon.X + mon.Width - w);
                        // cells touched by the rectangle (the busy points are binned by cell, so this errs on the safe side)
                        int cost = Sum((xx - mon.X) / Cell + 1 - 1, (yy - mon.Y) / Cell, (xx - mon.X + w - 1) / Cell + 1, (yy - mon.Y + h - 1) / Cell + 1);
                        double score = cost * 1000.0 + (1 - Math.Min(1.0, (double)w * h / ((double)width * height))) * 100;
                        if (best is null || score < best.Value.Score) best = (new ScreenRect(xx, yy, w, h), cost, score);
                        if (xx >= mon.X + mon.Width - w) break;
                    }
                    if (yy >= mon.Y + mon.Height - h) break;
                }
                if (best is { Cost: 0 } && best.Value.Rect.Width >= Math.Min(width, mon.Width) * 0.999) break;   // full size and free: done
            }
        }
        var b = best ?? (new ScreenRect(0, 0, width, height), 0, 0);
        return (b.Rect, Cost(b.Rect, busy));
    }
}
