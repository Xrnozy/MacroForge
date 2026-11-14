namespace MacroStudio.Model;

/// <summary>Moving a whole selection of positions: scaling it and fitting it inside a screen. Pure maths, so the editor and the tests share it.</summary>
public static class SelectionTransform
{
    // The selection's outline (min/max of both axes) in one pass instead of four Min/Max delegate scans.
    static (int X0, int X1, int Y0, int Y1) Bounds(IEnumerable<(int X, int Y)> pts)
    {
        int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
        foreach (var (x, y) in pts)
        {
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        return (x0, x1, y0, y1);
    }

    /// <summary>Scales positions about the middle of their outline (1 = no change).</summary>
    public static Func<int, int, (int X, int Y)> Scale(IReadOnlyCollection<(int X, int Y)> pts, double f)
    {
        if (pts.Count == 0 || f <= 0) return (x, y) => (x, y);
        var (x0, x1, y0, y1) = Bounds(pts);
        double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
        return (x, y) => ((int)Math.Round(cx + (x - cx) * f), (int)Math.Round(cy + (y - cy) * f));
    }

    /// <summary>
    /// Shrinks positions (never enlarges) until their outline fits inside the screen with a margin, then slides the outline in if it still sticks out.
    /// Positions already inside stay where they are.
    /// </summary>
    public static Func<int, int, (int X, int Y)> Fit(IReadOnlyCollection<(int X, int Y)> pts, ScreenRect screen, int margin = 24)
    {
        if (pts.Count == 0) return (x, y) => (x, y);
        var (x0, x1, y0, y1) = Bounds(pts);
        double w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        double f = Math.Min(1, Math.Min((screen.Width - 2.0 * margin) / w, (screen.Height - 2.0 * margin) / h));
        double nw = w * f, nh = h * f;
        double left = Math.Clamp(x0, screen.X + margin, Math.Max(screen.X + margin, screen.X + screen.Width - margin - nw));
        double top = Math.Clamp(y0, screen.Y + margin, Math.Max(screen.Y + margin, screen.Y + screen.Height - margin - nh));
        return (x, y) => ((int)Math.Round(left + (x - x0) * f), (int)Math.Round(top + (y - y0) * f));
    }
}
