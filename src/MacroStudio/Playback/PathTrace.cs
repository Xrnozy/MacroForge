using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>Remembers where the pointer was actually sent during playback (the optimized path), for display and for keeping the window out of its way.</summary>
public sealed class PathTrace(IInputSink inner, int capacity = 8000) : IInputSink
{
    readonly object _l = new();
    readonly List<(int X, int Y)> _pts = new();
    readonly List<(int X, int Y, bool Down)> _clicks = new();

    public void Send(in MacroEvent e)
    {
        if (e.Kind == EventKind.ButtonDown) lock (_l) { _clicks.Add((e.X, e.Y, true)); if (_clicks.Count > 500) _clicks.RemoveAt(0); }
        if (e.HasPos() && !e.IsTarget())
            lock (_l)
            {
                if (_pts.Count == 0 || _pts[^1] != (e.X, e.Y)) _pts.Add((e.X, e.Y));
                if (_pts.Count > capacity) _pts.RemoveRange(0, capacity / 4);
            }
        inner.Send(in e);
    }

    public (int X, int Y)[] Snapshot() { lock (_l) return _pts.ToArray(); }
    /// <summary>Where the pointer was actually pressed (every button-down sent), oldest first.</summary>
    public (int X, int Y, bool Down)[] Clicks() { lock (_l) return _clicks.ToArray(); }
    public int Count { get { lock (_l) return _pts.Count; } }

    /// <summary>The recorded positions in order, as the player will map them to this PC. Read-only analysis of the recording.</summary>
    public static List<(int X, int Y)> Recorded(IEnumerable<MacroEvent> events, Func<(int X, int Y), (int X, int Y)> map)
        => events.Where(e => e.HasPos() && !e.IsTarget()).Select(e => map((e.X, e.Y))).ToList();
}
