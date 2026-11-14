using MacroStudio.Model;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;

namespace MacroStudio.Tests;

public class VisibleModeTests
{
    sealed class Null : IInputSink { public void Send(in MacroEvent e) { } }

    [Fact]
    public void Replayed_pointer_events_over_the_app_window_pass_through_it_and_only_then()
    {
        var calls = new List<bool>();
        var shield = new WindowShield(new Null(), () => (100, 100, 500, 400), calls.Add);
        foreach (var (x, y) in new[] { (10, 10), (200, 200), (250, 250), (600, 600), (499, 399), (500, 400) })
        { var e = new MacroEvent(0, EventKind.MouseMove, X: x, Y: y); shield.Send(in e); }
        Assert.Equal([true, false, true, false], calls);              // toggled only when the pointer enters or leaves the window
        var k = new MacroEvent(0, EventKind.KeyDown, 0x41);
        shield.Send(in k);
        Assert.Equal(4, calls.Count);                                 // keys never touch the window state
    }

    [Fact]
    public void Looking_at_the_app_window_is_not_taking_over_but_using_the_target_app_is()
    {
        bool Over(RawInput r) => r.X is >= 100 and < 500 && r.Y is >= 100 and < 400;
        var d = new UserActivityDetector(new HashSet<int>(), graceMs: 0, ignore: Over);
        double t = 1000;
        RawInput Move(int x, int y) => new(0, EventKind.MouseMove, 0, 0, false, x, y, MouseBtn.Left, 0);
        RawInput Click(int x, int y) => new(0, EventKind.ButtonDown, 0, 0, false, x, y, MouseBtn.Left, 0);
        Assert.False(d.Feed(Move(150, 150), t)); Assert.False(d.Feed(Move(400, 300), t += 20));
        Assert.False(d.Feed(Click(300, 200), t += 20));                // clicking the app window: fine
        Assert.True(d.Feed(Click(900, 900), t += 20));                 // clicking the target app: the user took over
    }
}

public class WindowPlacerTests
{
    static readonly ScreenRect[] Two = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];

    static List<(int X, int Y)> Path(params (int, int)[] pts) => WindowPlacer.Along(pts);

    [Fact]
    public void The_window_goes_to_the_monitor_that_no_path_touches_at_full_size()
    {
        var busy = Path((100, 100), (1500, 900), (300, 700));
        var (r, cost) = WindowPlacer.Choose(Two, busy, 1200, 800);
        Assert.Equal(0, cost);
        Assert.True(r.X >= 1920, $"placed at {r}");
        Assert.Equal((1200, 800), (r.Width, r.Height));
    }

    [Fact]
    public void With_paths_on_both_monitors_it_shrinks_into_the_free_corner_instead_of_covering_a_path()
    {
        var busy = Path((0, 0), (1900, 0), (1900, 100), (3800, 100), (3800, 1000), (2000, 1000), (1000, 0))   // across the top and right edges of both
            .Concat(Path((10, 1070), (10, 1000))).ToList();
        var (r, cost) = WindowPlacer.Choose(Two, busy, 1400, 900);
        Assert.Equal(0, cost);
        Assert.True(r.Width < 1400 || r.Height < 900, $"had to shrink, got {r}");
        Assert.True(r.Width >= 480 && r.Height >= 340);
    }

    [Fact]
    public void When_nothing_is_free_it_picks_the_spot_with_the_fewest_paths_in_the_way()
    {
        var busy = new List<(int X, int Y)>();
        for (int x = 0; x < 3840; x += 20) for (int y = 0; y < 1080; y += 20) busy.Add((x, y + (x < 1920 ? 0 : 7)));
        var dense = busy.Concat(busy).Concat(busy.Where(p => p.X < 1920)).ToList();                      // the first monitor is busier
        var (r, cost) = WindowPlacer.Choose(Two, dense, 1000, 700);
        Assert.True(cost > 0);
        Assert.True(r.X >= 1920, $"the less busy monitor should win, got {r}");
    }

    [Fact]
    public void The_window_stays_inside_a_monitor()
    {
        var (r, _) = WindowPlacer.Choose([new ScreenRect(-1280, 0, 1280, 720)], [], 2000, 1500);
        Assert.True(r.X >= -1280 && r.Y >= 0 && r.X + r.Width <= 0 && r.Y + r.Height <= 720, $"{r}");
    }
}
