using MacroStudio.Model;

namespace MacroStudio.Editing;

/// <summary>
/// One move + scale + rotate of screen positions: p' = pivot + Rotate(angle) · Scale(sx, sy) · (p - pivot) + (dx, dy).
/// Always applied to the ORIGINAL positions of a drag, so a long drag never accumulates rounding.
/// </summary>
public readonly record struct Xf(double Px, double Py, double Sx = 1, double Sy = 1, double RotDeg = 0, double Dx = 0, double Dy = 0)
{
    public (double X, double Y) Map(double x, double y)
    {
        double vx = (x - Px) * Sx, vy = (y - Py) * Sy, a = RotDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return (Px + vx * c - vy * s + Dx, Py + vx * s + vy * c + Dy);
    }

    public bool IsIdentity => Sx == 1 && Sy == 1 && RotDeg == 0 && Dx == 0 && Dy == 0;

    /// <summary>The event with its position moved (mouse moves, clicks, scrolls); a target click's saved "recorded click position" moves too, so it still lines up.</summary>
    public MacroEvent Apply(MacroEvent e)
    {
        if (e.HasPos())
        {
            var (x, y) = Map(e.X, e.Y);
            return e with { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        }
        if (e.Kind == EventKind.TargetClick && e.Step is { } st && (st.FallbackX != 0 || st.FallbackY != 0))
        {
            var (x, y) = Map(st.FallbackX, st.FallbackY);
            return e with { Step = st with { FallbackX = (int)Math.Round(x), FallbackY = (int)Math.Round(y) } };
        }
        return e;
    }

    /// <summary>The smallest rectangle around the positions of these events, or null when none has a position.</summary>
    public static (double X, double Y, double W, double H)? BoundsOf(IEnumerable<MacroEvent> events)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue; int n = 0;
        foreach (var e in events)
        {
            if (!e.HasPos()) continue;
            n++; minX = Math.Min(minX, e.X); minY = Math.Min(minY, e.Y); maxX = Math.Max(maxX, e.X); maxY = Math.Max(maxY, e.Y);
        }
        return n == 0 ? null : (minX, minY, maxX - minX, maxY - minY);
    }
}
