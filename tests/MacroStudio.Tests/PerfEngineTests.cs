using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Recording;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>Performance / timing / robustness budgets for the record-playback engine. Simulated clock and sinks only: no hooks, no SendInput.</summary>
public class PerfEngineTests
{
    sealed class NullSink : IInputSink { public long N; public void Send(in MacroEvent e) => N++; }

    static MacroEvent[] Moves(int n, double stepMs) =>
        Enumerable.Range(0, n).Select(i => new MacroEvent(i * stepMs, EventKind.MouseMove, X: i % 1900, Y: i % 1000)).ToArray();

    static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }

    static long Alloc(Action a)
    {
        a(); // warm-up (JIT, caches)
        long b = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - b;
    }

    // ---- Player hot path ----

    [Fact]
    public void Player_dispatches_200k_events_fast_and_without_allocating()
    {
        var ev = Moves(200_000, 0); // all due at once: pure dispatch cost
        var sink = new NullSink();
        var p = new Player(sink, new FakeClock());
        var secs = new List<double>();
        for (int r = 0; r < 3; r++) { var sw = Stopwatch.StartNew(); p.Run(ev, 0, ev.Length, 1); secs.Add(sw.Elapsed.TotalSeconds); }
        double evPerSec = ev.Length / Median(secs);
        Assert.True(evPerSec > 1_000_000, $"{evPerSec:N0} events/s");
        long bytes = Alloc(() => p.Run(ev, 0, ev.Length, 1));
        Assert.True(bytes / (double)ev.Length < 8, $"{bytes / (double)ev.Length:F1} B/event allocated by the player loop");
    }

    [Fact]
    public void Hours_long_looped_run_with_log_and_path_trace_does_not_grow_memory()
    {
        var log = new ExecutionLog();
        var trace = new PathTrace(new NullSink());
        var p = new Player(trace, new FakeClock()) { Log = log };
        var ev = Moves(1000, 8); // 8 s per loop
        p.Run(ev, 0, ev.Length, 5);
        GC.Collect(); long before = GC.GetTotalMemory(true);
        p.Run(ev, 0, ev.Length, 2000); // ~4.4 simulated hours, 2M events
        for (int i = 0; i < 1_000_000; i++) log.Add(i, "x", LogResult.Ok, "m");
        GC.Collect(); long grown = GC.GetTotalMemory(true) - before;
        Assert.True(grown < 2_000_000, $"memory grew {grown:N0} bytes");
        Assert.True(trace.Count <= 8000);
        Assert.Equal(ExecutionLog.Capacity, log.Snapshot().Count);
        Assert.True(trace.Clicks().Length <= 500);
    }

    [Fact]
    public void One_hour_macro_with_speed_changes_has_no_accumulated_drift()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock);
        var ev = Moves(360_000, 10); // 1 h at 100 Hz
        var p = new Player(sink, clock);
        double[] speeds = [1, 4, 0.5, 2];
        sink.OnSend = n => { if (n % 90_000 == 0) p.Speed = speeds[n / 90_000 % 4]; };
        p.Run(ev, 0, ev.Length, 1);
        // 90k events at each of speed 1, 4, 0.5, 2 (a change applies from the next event)
        double expected = 900_000 + 225_000 + 1_800_000 + 450_000;
        Assert.InRange(sink.Sent[^1].At, expected - 100, expected + 100);   // within 0.1 s over 53 min
        Assert.True(p.MaxLatenessMs < 1, $"max lateness {p.MaxLatenessMs} ms");
    }

    // ---- Stop latency (real clock, background thread; no input is injected) ----

    [Fact]
    public void Stop_during_a_long_wait_returns_within_100ms_even_paused_or_with_a_flow_host()
    {
        var ev = new[] { new MacroEvent(0, EventKind.MouseMove, X: 1, Y: 1), new MacroEvent(60_000, EventKind.MouseMove, X: 2, Y: 2) };
        var done = new ManualResetEventSlim();
        var p = new Player(new NullSink());
        p.Finished += done.Set;
        p.Start(ev, 0, ev.Length, 1);
        Thread.Sleep(100);
        var sw = Stopwatch.StartNew(); p.Stop();
        Assert.True(done.Wait(2000), "player did not finish");
        Assert.True(sw.ElapsedMilliseconds < 100, $"stop took {sw.ElapsedMilliseconds} ms");

        done.Reset();
        p = new Player(new NullSink()) { Flow = new NoFlow() };
        p.Finished += done.Set;
        p.Start(ev, 0, ev.Length, 1);
        Thread.Sleep(100); p.Pause(); Thread.Sleep(50);
        sw.Restart(); p.Stop();
        Assert.True(done.Wait(2000));
        Assert.True(sw.ElapsedMilliseconds < 100, $"stop while paused took {sw.ElapsedMilliseconds} ms");
    }

    sealed class NoFlow : IFlowHost
    {
        public FlowStep OnWhen(in MacroEvent e, int index) => FlowStep.Continue;
        public FlowStep Poll() => FlowStep.Continue;
    }

    // ---- exception paths ----

    sealed class FailingSink(Func<MacroEvent, bool> fails) : IInputSink
    {
        public readonly List<MacroEvent> Got = new();
        public void Send(in MacroEvent e) { if (fails(e)) throw new InvalidOperationException("SendInput failed"); Got.Add(e); }
    }

    [Fact]
    public void A_failing_sink_still_releases_every_held_key_and_button()
    {
        var ev = new MacroEvent[]
        {
            new(0, EventKind.KeyDown, 0x41), new(1, EventKind.KeyDown, 0x42), new(2, EventKind.ButtonDown, X: 5, Y: 5),
            new(3, EventKind.KeyDown, 0x43), new(4, EventKind.MouseMove, X: 9, Y: 9), new(5, EventKind.KeyUp, 0x41),
        };
        var sink = new FailingSink(e => e.Kind == EventKind.MouseMove);   // dies mid-macro
        var p = new Player(sink, new FakeClock()); p.Run(ev, 0, ev.Length, 1);
        Assert.NotNull(p.Error);
        Assert.Equal(new[] { 0x41, 0x42, 0x43 }, sink.Got.Where(e => e.Kind == EventKind.KeyUp).Select(e => e.Vk).OrderBy(v => v).ToArray());
        Assert.Contains(sink.Got, e => e.Kind == EventKind.ButtonUp);

        // one stuck release must not stop the others
        var sink2 = new FailingSink(e => (e.Kind == EventKind.KeyUp && e.Vk == 0x42) || e.Kind == EventKind.MouseMove);
        new Player(sink2, new FakeClock()).Run(ev, 0, ev.Length, 1);
        Assert.Contains(sink2.Got, e => e.Kind == EventKind.KeyUp && e.Vk == 0x43);
        Assert.Contains(sink2.Got, e => e.Kind == EventKind.ButtonUp);
    }

    // ---- nested flows ----

    sealed class AlwaysThere : ITargetFinder
    {
        public string? NameOf(string id) => "Pop";
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) => new Sighting(new Detection(new Rect(760, 380, 80, 40), 0.97, 1), 1, DateTime.UtcNow);
    }

    [Fact]
    public void Self_running_macro_is_cut_off_at_the_depth_limit_and_a_stop_unwinds_every_level_at_once()
    {
        var self = new MacroEvent[]
        {
            new(0, EventKind.MouseMove, X: 1, Y: 1),
            new(50, EventKind.When, Target: "pop", Flow: new FlowOptions { Condition = WhenCondition.Appears, WaitMs = 0, Do = FlowDo.RunMacro, Macro = "A" }),
            new(100, EventKind.MouseMove, X: 2, Y: 2),
        };
        (int Sends, int Failed) Run(int stopAfter)
        {
            var clock = new FakeClock(); var sink = new RecordingSink(clock); var log = new ExecutionLog();
            bool stop = false;
            sink.OnSend = n => { if (n == stopAfter) stop = true; };
            var p = new Player(sink, clock) { Log = log, ExternalStop = () => stop };
            p.Flow = new FlowRunner(new AlwaysThere(), clock, () => stop, sink, id => id == "A" ? self : null, (_, _) => (sink, sink), () => 1, log);
            p.Run(self, 0, self.Length, 1);
            return (sink.Sent.Count, log.Count(LogResult.Failed));
        }
        var full = Run(int.MaxValue);
        Assert.InRange(full.Sends, 2 * 7, 3 * 7);       // MaxDepth 6 => 7 nested levels, then "nested too deeply"
        Assert.True(full.Failed >= 1);
        var cut = Run(5);
        Assert.True(cut.Sends <= 5 + 1, $"{cut.Sends - 5} events were still sent after Stop");
    }

    // ---- per-event cost of other engine parts ----

    [Fact]
    public void AdaptingSink_on_a_different_monitor_layout_does_not_allocate_per_event()
    {
        // RED: MapPoint rebuilds two sorted lists (OrderBy.ThenBy.ToList) for EVERY event.
        static DesktopInfo D(params (int x, int y, int w, int h)[] m) { var l = m.Select(r => new ScreenRect(r.x, r.y, r.w, r.h)).ToList(); return new DesktopInfo(new ScreenRect(0, 0, l.Max(r => r.X + r.Width), l.Max(r => r.Y + r.Height)), l); }
        var a = new AdaptingSink(new NullSink(), D((0, 0, 1920, 1080), (1920, 0, 1920, 1080)), null, D((0, 0, 2560, 1440), (2560, 0, 2560, 1440)), new EnvInfo(96, 3, 10));
        var ev = Moves(50_000, 0);
        long bytes = Alloc(() => { foreach (var e in ev) a.Send(in e); });
        Assert.True(bytes / (double)ev.Length < 32, $"{bytes / (double)ev.Length:F0} B per event");
    }

    [Fact]
    public void Record_diagnostics_per_event_cost_is_allocation_free()
    {
        // Was RED (325 B/event of string building); fixed with pre-built keys. What the hook thread and the recorder consumer run per input: Count("hook: mouse " + kind), Count($"stored: {kind}"), Max(...).
        // String concat + enum ToString allocate on the hook thread for every mouse move (the hook has a tiny time limit).
        var log = new RecordLog();
        var kinds = new[] { EventKind.MouseMove, EventKind.KeyDown, EventKind.ButtonDown };
        void PerEvent(int i)
        {
            var k = kinds[i % 3];
            log.Count(RecordLog.HookMouseKey[(int)k]);                 // the keys the hook and the consumer really use: built once, not per event
            log.Count(RecordLog.StoredKey[(int)k]);
            log.Max("mouse hook callback took (ms)", 0.01);
        }
        for (int i = 0; i < 1000; i++) PerEvent(i);
        long b = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200_000; i++) PerEvent(i);
        double ms = sw.Elapsed.TotalMilliseconds;
        double perEv = (GC.GetAllocatedBytesForCurrentThread() - b) / 200_000.0;
        Assert.True(perEv < 16, $"{perEv:F0} B/event on the hook/consumer path ({ms:F0} ms per 200k)");
    }
}
