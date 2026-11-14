using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;

namespace MacroStudio.Tests;

/// <summary>Touches the real desktop: moves the cursor briefly and restores it. Skipped when no interactive desktop is available.</summary>
[Collection("real desktop")]
public class LiveInputTests
{
    [Fact]
    public void Injected_moves_land_on_exact_pixels_and_hooks_capture_them_with_wheel_delta()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return; // no desktop session
        var (ox, oy) = Screens.CursorPos();
        var got = new List<RawInput>();
        using var hooks = new InputHooks(r => { lock (got) got.Add(r); }) { IgnoreInjected = false };
        hooks.Start();
        try
        {
            var inj = new InputInjector(desk.Virtual);
            var v = desk.Virtual;
            var targets = new[] { (v.X + 10, v.Y + 10), (v.X + v.Width - 11, v.Y + v.Height - 11), (v.X + v.Width / 2, v.Y + v.Height / 2), (v.X + 777, v.Y + 333) };
            foreach (var (x, y) in targets)
            {
                // a person using the mouse during the test can nudge the cursor between inject and read: retry before judging
                (int, int) pos = default;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    inj.Send(new MacroEvent(0, EventKind.MouseMove, X: x, Y: y));
                    SpinWait.SpinUntil(() => Screens.CursorPos() == (x, y), 150);
                    pos = Screens.CursorPos();
                    if (pos == (x, y)) break;
                }
                Assert.Equal((x, y), pos);
            }
            var (wx, wy) = targets[3];
            inj.Send(new MacroEvent(0, EventKind.HWheel, X: wx, Y: wy, Delta: 120));
            inj.Send(new MacroEvent(0, EventKind.HWheel, X: wx, Y: wy, Delta: -60)); // fractional hi-res step
            Thread.Sleep(100);
            lock (got)
            {
                Assert.Contains(got, r => r.Kind == EventKind.MouseMove && r.X == targets[1].Item1 && r.Y == targets[1].Item2);
                var wheels = got.Where(r => r.Kind == EventKind.HWheel).Select(r => r.Delta).ToList();
                Assert.Equal([120, -60], wheels);
            }
        }
        finally
        {
            inj_restore(ox, oy, desk);
        }
    }

    static void inj_restore(int x, int y, DesktopInfo d) => new InputInjector(d.Virtual).Send(new MacroEvent(0, EventKind.MouseMove, X: x, Y: y));
}
