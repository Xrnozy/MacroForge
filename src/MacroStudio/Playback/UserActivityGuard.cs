using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Playback;

/// <summary>
/// Decides whether physical input is the user taking over. Macro Studio's own injected input never reaches this (the hook
/// ignores injected events), so anything seen here was typed or moved by a person. Pure logic, no hooks: easy to test.
/// </summary>
public sealed class UserActivityDetector(IReadOnlySet<int> ignoredKeys, double graceMs = 1500, double minMovePx = 15, Func<RawInput, bool>? ignore = null)
{
    const double MoveWindowMs = 300;
    double _startMs = -1, _lastMoveMs = -1, _path;
    (int X, int Y)? _lastPos;

    public void Reset(double nowMs) { _startMs = nowMs; _lastPos = null; _path = 0; _lastMoveMs = -1; }

    /// <summary>True when this event shows the user is active. The first moments after starting are ignored (the user just clicked Play).</summary>
    public bool Feed(RawInput r, double nowMs)
    {
        if (_startMs < 0) _startMs = nowMs;
        if (nowMs - _startMs < graceMs) return false;
        if (r.Kind != EventKind.KeyDown && ignore?.Invoke(r) == true)
        {
            if (r.Kind == EventKind.MouseMove) { _lastPos = null; _path = 0; }   // an ignored move breaks the gesture: the hop out of our window is not user input either
            return false;                                                       // e.g. the pointer is over this app's own window: looking at it is not taking over
        }
        switch (r.Kind)
        {
            case EventKind.KeyDown: return !ignoredKeys.Contains(r.Vk);   // hotkeys of this app are not "the user taking over"
            case EventKind.ButtonDown or EventKind.Wheel or EventKind.HWheel: return true;
            case EventKind.MouseMove:
                if (_lastPos is { } p && _lastMoveMs >= 0 && nowMs - _lastMoveMs <= MoveWindowMs) _path += Math.Sqrt((double)(r.X - p.X) * (r.X - p.X) + (double)(r.Y - p.Y) * (r.Y - p.Y));
                else _path = 0;                                           // a pause in movement starts a new gesture
                _lastPos = (r.X, r.Y); _lastMoveMs = nowMs;
                return _path >= minMovePx;                                // a nudge of the desk is not a takeover, a real sweep is
        }
        return false;
    }
}

/// <summary>Watches for the user touching the mouse or keyboard while a macro plays, and calls back once (debounced).</summary>
public sealed class UserActivityGuard : IDisposable
{
    readonly InputHooks _hooks;
    readonly UserActivityDetector _detector;
    readonly Action _onUser;
    long _lastFire;

    public UserActivityGuard(Action onUser, IReadOnlySet<int> ignoredKeys, Func<RawInput, bool>? ignore = null)
    {
        _onUser = onUser;
        _detector = new UserActivityDetector(ignoredKeys, ignore: ignore);
        _detector.Reset(Environment.TickCount64);
        _hooks = new InputHooks(OnRaw); // IgnoreInjected stays true: our own playback is invisible to it
        try { _hooks.Start(); } catch { _hooks.Dispose(); throw; }   // a failed start must not leave a half-installed hook behind
    }

    void OnRaw(RawInput r)
    {
        long now = Environment.TickCount64;
        if (_disposed || !_detector.Feed(r, now) || now - _lastFire < 2000) return;
        _lastFire = now;
        _onUser(); // cheap and non-blocking: the caller only queues work
    }

    volatile bool _disposed;
    public void Dispose() { if (_disposed) return; _disposed = true; _hooks.Dispose(); }
}
