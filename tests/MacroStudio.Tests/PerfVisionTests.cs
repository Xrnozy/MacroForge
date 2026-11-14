using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>Performance and robustness budgets for the detection pipeline on a 1920x1080 frame. Budgets come from measurements on a dev PC with margin; "RED" tests encode what the app should do but does not yet.</summary>
public sealed class PerfVisionTests : IDisposable
{
    readonly ITestOutputHelper _o;
    readonly Mat _frame, _present, _absent;
    static readonly Point At = new(700, 500);

    public PerfVisionTests(ITestOutputHelper o)
    {
        _o = o;
        using var desk = Desktop.Make();
        _frame = new Mat(desk, new Rect(0, 0, 1920, 1080)).Clone();
        _present = RealButtons.Decode(RealButtons.Retry);
        _absent = RealButtons.Decode(RealButtons.Modifiers);
        using var d = new Mat(_frame, new Rect(At.X, At.Y, _present.Width, _present.Height));
        _present.CopyTo(d);
    }

    public void Dispose() { _frame.Dispose(); _present.Dispose(); _absent.Dispose(); }

    static double Median(Func<double> f, int n = 7) { var l = Enumerable.Range(0, n).Select(_ => f()).OrderBy(x => x).ToList(); return l[n / 2]; }
    static double Ms(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
    static long PrivateMb() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64 >> 20; }

    [Fact]
    public void Present_target_in_full_hd_frame_is_found_fast()
    {
        var det = new Detector();
        det.Find(_frame, _present, 0.85); // warm-up
        double ms = Median(() => Ms(() => { var r = det.Find(_frame, _present, 0.85); Assert.Equal(At.X, r[0].Rect.X); Assert.Equal(At.Y, r[0].Rect.Y); }));
        _o.WriteLine($"present full-frame find: {ms:F1} ms");
        Assert.True(ms < 40, $"{ms:F1} ms");
    }

    [Fact]
    public void Absent_target_with_all_stages_is_bounded()
    {
        // RED: measured ~1600 ms (multi-thread) / ~2300 ms (1 thread): 7-scale sweep 330 ms + patches 730 ms + shape 160 ms + AKAZE 500-1000 ms
        var det = new Detector();
        det.Find(_frame, _absent, 0.85);
        double ms = Median(() => Ms(() => Assert.Empty(det.Find(_frame, _absent, 0.85))), 5);
        _o.WriteLine($"absent all-stages find: {ms:F1} ms");
        Assert.True(ms < 600, $"{ms:F1} ms");
    }

    [Fact]
    public void Absent_target_template_only_is_cheap()
    {
        // RED: measured ~290 ms; the two likely scales (Light) cost ~20 ms, the other 5 sweep scales ~55 ms each

        var det = new Detector();
        det.Find(_frame, _absent, 0.85, occlusion: false);
        double ms = Median(() => Ms(() => Assert.Empty(det.Find(_frame, _absent, 0.85, occlusion: false))));
        _o.WriteLine($"absent template-only find: {ms:F1} ms");
        Assert.True(ms < 150, $"{ms:F1} ms");
    }

    [Fact]
    public void Search_with_hint_is_much_cheaper_than_full_frame()
    {
        var det = new Detector();
        var hint = new Rect(At.X - 100, At.Y - 100, 330, 220);
        det.Find(_frame, _present, 0.85, hint: hint); det.Find(_frame, _present, 0.85);
        double hinted = Median(() => Ms(() => det.Find(_frame, _present, 0.85, hint: hint)));
        double full = Median(() => Ms(() => det.Find(_frame, _present, 0.85)));
        _o.WriteLine($"hinted {hinted:F1} ms, full {full:F1} ms");
        Assert.True(hinted < full * 0.6 + 2, $"hinted {hinted:F1} vs full {full:F1}");
    }

    [Fact]
    public void Tracked_verify_is_submillisecond_class()
    {
        var det = new Detector();
        using var ctx = new FrameContext(_frame);
        var hit = det.Light(ctx, _present, 0.85, 1, null, out _)[0];
        det.Verify(ctx, _present, hit, 0.85, 0.4, true);
        double ms = Median(() => { var sw = Stopwatch.StartNew(); for (int i = 0; i < 20; i++) Assert.NotNull(det.Verify(ctx, _present, hit, 0.85, 0.4, true)); return sw.Elapsed.TotalMilliseconds / 20; });
        _o.WriteLine($"verify: {ms:F3} ms");
        Assert.True(ms < 2, $"{ms:F3} ms");
    }

    [Fact]
    public void Managed_allocations_per_search_are_small()
    {
        var det = new Detector();
        det.Find(_frame, _present, 0.85);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) det.Find(_frame, _present, 0.85);
        long per = (GC.GetAllocatedBytesForCurrentThread() - a0) / 10;
        _o.WriteLine($"managed bytes per search: {per}");
        Assert.True(per < 200_000, $"{per} bytes");
    }

    [Fact]
    public void Repeated_searches_do_not_grow_native_memory()
    {
        var det = new Detector();
        for (int i = 0; i < 5; i++) { det.Find(_frame, _absent, 0.85, occlusion: false); det.Find(_frame, _present, 0.85); }
        long m0 = PrivateMb();
        for (int i = 0; i < 30; i++) { det.Find(_frame, _absent, 0.85, occlusion: false); det.Find(_frame, _present, 0.85); }
        long m1 = PrivateMb();
        _o.WriteLine($"private MB {m0} -> {m1}");
        Assert.True(m1 - m0 < 40, $"grew {m1 - m0} MB");
    }

    [Fact]
    public void Watcher_idle_cpu_with_static_screen_is_low()
    {
        // GREEN guard: measured 1.2%, once 4.3% when a 1 s deep stage fell into the window; budget leaves room for that
        var info = new DesktopInfo(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]);
        var dir = Path.Combine(Path.GetTempPath(), "ms-pv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TargetStore(dir);
            var targets = new[] { store.Add("p", _present, 96, info.Virtual, 0.85), store.Add("a", _absent, 96, info.Virtual, 0.85) };
            using var w = new TargetWatcher(store, r => new Mat(_frame, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), info, 96, threaded: true);
            using var h = w.Watch(targets);
            Thread.Sleep(6000); // settle: initial searches and the deep stages' first runs
            using var p = Process.GetCurrentProcess();
            var c0 = p.TotalProcessorTime; var t0 = Stopwatch.StartNew();
            Thread.Sleep(5000);
            p.Refresh();
            double share = (p.TotalProcessorTime - c0).TotalMilliseconds / t0.Elapsed.TotalMilliseconds;
            _o.WriteLine($"idle watcher CPU share of one core: {share:P1}; stats work {w.Stats.WorkMs:F1} ms delay {w.Stats.DelayMs:F0} ms deep {w.Stats.DeepScans}");
            Assert.True(share < 0.08, $"{share:P1}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static DesktopInfo OneMonitor => new(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]);

    [Fact]
    public void No_single_watcher_tick_blocks_for_long()
    {
        // RED: one deep stage (patches / AKAZE / shape) of an absent target runs inside a single Tick: ~0.7-1 s each, so a macro waiting on another target is stalled
        var dir = Path.Combine(Path.GetTempPath(), "ms-pv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TargetStore(dir);
            var t = store.Add("a", _absent, 96, OneMonitor.Virtual, 0.85);
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var w = new TargetWatcher(store, r => new Mat(_frame, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), OneMonitor, 96, threaded: false, clock: () => clock);
            using var h = w.Watch([t]);
            double max = 0;
            for (int i = 0; i < 30; i++) { var sw = Stopwatch.StartNew(); double delay = w.Tick(); max = Math.Max(max, sw.Elapsed.TotalMilliseconds); clock = clock.AddMilliseconds(Math.Max(delay, 3500)); }
            _o.WriteLine($"max tick {max:F0} ms");
            Assert.True(max < 300, $"{max:F0} ms");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Disposing_a_busy_watcher_returns_quickly()
    {
        // GREEN guard (3 ms measured when the thread sleeps between ticks): Dispose joins the thread for up to 2 s while a long deep stage runs, then tears down state the thread still uses
        var dir = Path.Combine(Path.GetTempPath(), "ms-pv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TargetStore(dir);
            var t = store.Add("a", _absent, 96, OneMonitor.Virtual, 0.85);
            var w = new TargetWatcher(store, r => new Mat(_frame, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), OneMonitor, 96, threaded: true);
            using var h = w.Watch([t]);
            Thread.Sleep(1500); // light scan done, a deep stage is running
            var sw = Stopwatch.StartNew(); w.Dispose();
            _o.WriteLine($"dispose took {sw.Elapsed.TotalMilliseconds:F0} ms");
            Assert.True(sw.Elapsed.TotalMilliseconds < 300, $"{sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
