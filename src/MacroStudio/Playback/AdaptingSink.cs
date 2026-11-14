using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>
/// Remaps a recording to the current PC before injection: positions follow the monitor layout and resolution,
/// scroll distance follows the Windows "lines per notch" setting. Absolute injection already ignores pointer speed
/// and mouse hardware DPI, so those need no correction; they are recorded for reference only.
/// </summary>
public sealed class AdaptingSink : IInputSink
{
    readonly IInputSink _inner;
    readonly DesktopInfo? _rec, _cur;
    readonly double _wheelScale;
    readonly bool _identity;

    public AdaptingSink(IInputSink inner, DesktopInfo? recorded, EnvInfo? recordedEnv, DesktopInfo current, EnvInfo currentEnv)
    {
        _inner = inner; _rec = recorded; _cur = current;
        // Same notches must scroll the same distance: apps scroll (notch × lines), so scale by recordedLines / currentLines.
        _wheelScale = recordedEnv is { WheelLines: > 0 and < 100 } && currentEnv.WheelLines is > 0 and < 100
            ? (double)recordedEnv.WheelLines / currentEnv.WheelLines : 1;
        if (recorded != null) { _rm = Ordered(recorded.Monitors); _cm = Ordered(current.Monitors); }
        _identity = recorded == null || (recorded.Virtual == current.Virtual && recorded.Monitors.SequenceEqual(current.Monitors));
    }

    public bool IsAdapting => !_identity || _wheelScale != 1;

    static List<ScreenRect> Ordered(List<ScreenRect> m) => m.OrderBy(r => r.X).ThenBy(r => r.Y).ToList();
    readonly List<ScreenRect>? _rm, _cm; // ordered once, not per event

    public (int X, int Y) MapPoint(int x, int y)
    {
        if (_identity || _rec == null || _cur == null) return (x, y);
        var rm = _rm!; var cm = _cm!;
        if (rm.Count == cm.Count && rm.Count > 0)
        {
            int i = -1;
            for (int j = 0; j < rm.Count; j++) if (rm[j].Contains(x, y)) { i = j; break; }
            if (i >= 0)
            {
                var a = rm[i]; var b = cm[i]; // same monitor slot, same relative spot inside it
                return (b.X + (int)Math.Round((x - a.X) * (b.Width - 1.0) / Math.Max(1, a.Width - 1)),
                        b.Y + (int)Math.Round((y - a.Y) * (b.Height - 1.0) / Math.Max(1, a.Height - 1)));
            }
        }
        var rv = _rec.Virtual; var cv = _cur.Virtual; // different layouts: scale across the whole virtual desktop
        int nx = cv.X + (int)Math.Round((x - rv.X) * (cv.Width - 1.0) / Math.Max(1, rv.Width - 1));
        int ny = cv.Y + (int)Math.Round((y - rv.Y) * (cv.Height - 1.0) / Math.Max(1, rv.Height - 1));
        return (Math.Clamp(nx, cv.X, cv.X + cv.Width - 1), Math.Clamp(ny, cv.Y, cv.Y + cv.Height - 1));
    }

    public void Send(in MacroEvent e)
    {
        if (!e.HasPos()) { _inner.Send(in e); return; } // keys and Discord messages have no position; target clicks resolve their own live position
        var (x, y) = MapPoint(e.X, e.Y);
        int delta = e.IsWheel() && _wheelScale != 1 ? (int)Math.Round(e.Delta * _wheelScale) : e.Delta;
        if (e.IsWheel() && delta == 0 && e.Delta != 0) delta = Math.Sign(e.Delta); // never drop a scroll step
        var m = e with { X = x, Y = y, Delta = delta };
        _inner.Send(in m);
    }
}
