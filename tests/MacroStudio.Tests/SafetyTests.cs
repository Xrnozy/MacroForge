using MacroStudio.Model;
using MacroStudio.Monitoring;
using MacroStudio.Native;
using MacroStudio.Playback;

namespace MacroStudio.Tests;

public class SafetyTests
{
    static RawInput Key(int vk) => new(0, EventKind.KeyDown, vk, 0, false, 0, 0, default, 0);
    static RawInput Btn() => new(0, EventKind.ButtonDown, 0, 0, false, 5, 5, MouseBtn.Left, 0);
    static RawInput Move(int x, int y) => new(0, EventKind.MouseMove, 0, 0, false, x, y, default, 0);

    static UserActivityDetector Detector() => new(new HashSet<int> { 0x77, 0x78, 0x79, 0x7A }, graceMs: 1000, minMovePx: 15);

    [Fact]
    public void User_input_right_after_starting_is_ignored_then_keys_and_buttons_count()
    {
        var d = Detector(); d.Reset(0);
        Assert.False(d.Feed(Key(0x41), 500));            // still in the grace period: the user just clicked Play
        Assert.False(d.Feed(Btn(), 900));
        Assert.True(d.Feed(Key(0x41), 1500));
        Assert.True(d.Feed(Btn(), 1600));
        Assert.True(d.Feed(new RawInput(0, EventKind.Wheel, 0, 0, false, 1, 1, default, 120), 1700));
    }

    [Fact]
    public void The_apps_own_hotkeys_are_not_a_takeover()
    {
        var d = Detector(); d.Reset(0);
        foreach (int vk in new[] { 0x77, 0x78, 0x79, 0x7A }) Assert.False(d.Feed(Key(vk), 5000));
    }

    [Fact]
    public void A_real_mouse_sweep_counts_but_a_tiny_nudge_does_not()
    {
        var d = Detector(); d.Reset(0);
        Assert.False(d.Feed(Move(100, 100), 2000));
        Assert.False(d.Feed(Move(103, 101), 2050));      // a few pixels: the desk was bumped
        Assert.False(d.Feed(Move(105, 102), 2100));

        var d2 = Detector(); d2.Reset(0);
        bool fired = false;
        for (int i = 0; i < 10; i++) fired |= d2.Feed(Move(100 + i * 12, 100), 2000 + i * 30);   // a deliberate sweep
        Assert.True(fired);

        var d3 = Detector(); d3.Reset(0);                // small moves separated by pauses never add up to a gesture
        bool any = false;
        for (int i = 0; i < 20; i++) any |= d3.Feed(Move(100 + i * 8, 100), 2000 + i * 1000);
        Assert.False(any);
    }

    [Fact]
    public void Display_changes_are_described()
    {
        var m1 = new ScreenRect(0, 0, 1920, 1080); var m2 = new ScreenRect(1920, 0, 1920, 1080);
        var a = new DisplaySnapshot(new ScreenRect(0, 0, 3840, 1080), [m1, m2], 96);
        Assert.Null(new DisplaySnapshot(a.Virtual, [m1, m2], 96).DifferenceFrom(a));
        Assert.Contains("scaling", new DisplaySnapshot(a.Virtual, [m1, m2], 144).DifferenceFrom(a));
        Assert.Contains("monitors", new DisplaySnapshot(m1, [m1], 96).DifferenceFrom(a));
        Assert.Contains("resolution", new DisplaySnapshot(new ScreenRect(0, 0, 3840, 1440), [m1, new ScreenRect(1920, 0, 1920, 1440)], 96).DifferenceFrom(a));
    }

    [Fact]
    public void Perf_monitor_warns_about_growing_memory_and_creeping_cpu_but_not_steady_use()
    {
        var t0 = new DateTime(2026, 1, 1);
        var steady = new PerfMonitor();
        for (int i = 0; i < 200; i++) steady.Record(t0.AddSeconds(i * 10), 4 + (i % 3), 300 + (i % 5));
        Assert.Null(steady.Warning());

        var leak = new PerfMonitor();
        for (int i = 0; i < 200; i++) leak.Record(t0.AddSeconds(i * 10), 4, 300 + i * 2);          // +400 MB over ~33 minutes
        Assert.Contains("memory is growing", leak.Warning());

        var creep = new PerfMonitor();
        for (int i = 0; i < 200; i++) creep.Record(t0.AddSeconds(i * 10), 4 + i * 0.1, 300);       // 4% -> 24%
        Assert.Contains("CPU use has doubled", creep.Warning());

        var young = new PerfMonitor();
        for (int i = 0; i < 10; i++) young.Record(t0.AddSeconds(i * 10), 80, 300 + i * 100);
        Assert.Null(young.Warning());                                                              // too little data to judge
    }

    [Fact]
    public void Perf_history_is_bounded_for_multi_day_sessions()
    {
        var m = new PerfMonitor();
        var t0 = new DateTime(2026, 1, 1);
        for (int i = 0; i < PerfMonitor.Capacity + 5000; i++) m.Record(t0.AddSeconds(i * 10), 3, 300);   // ~4 days of points
        Assert.Equal(PerfMonitor.Capacity, m.HistoryCount);
    }

    [Fact]
    public void Perf_monitor_reads_this_process()
    {
        var m = new PerfMonitor();
        m.Sample(DateTime.UtcNow);
        Thread.Sleep(120);
        m.Sample(DateTime.UtcNow);
        Assert.True(m.RamMb > 20);
        Assert.InRange(m.CpuPercent, 0, 1600);
    }
}
