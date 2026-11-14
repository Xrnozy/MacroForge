using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>
/// For "keep the app visible while playing": a replayed mouse event that lands on this app's own window must reach the
/// window underneath instead, so the window never swallows a click meant for the target. Before each mouse event it
/// asks for the window to be click-through while the pointer is over it, and solid again once the pointer is elsewhere.
/// </summary>
public sealed class WindowShield(IInputSink inner, Func<(int L, int T, int R, int B)> bounds, Action<bool> setPassThrough) : IInputSink
{
    bool _through;

    public void Send(in MacroEvent e)
    {
        if (e.Kind is EventKind.MouseMove or EventKind.ButtonDown or EventKind.ButtonUp or EventKind.Wheel or EventKind.HWheel)
        {
            var b = bounds();
            bool over = e.X >= b.L && e.X < b.R && e.Y >= b.T && e.Y < b.B;
            if (over != _through) { setPassThrough(over); _through = over; }   // mark the new state only once it was applied: a failed toggle is retried by the next event
        }
        inner.Send(in e);
    }
}
