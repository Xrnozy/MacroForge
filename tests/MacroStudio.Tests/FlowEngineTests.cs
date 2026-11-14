using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>Nested-macro engine: restart propagation, run-wide counters, watchers, pause, cleanup. Simulated clock unless a test needs real threads.</summary>
public class FlowEngineTests
{
    sealed class Screen(IClock clock, Action? onLook = null) : ITargetFinder
    {
        public Func<double, bool> Visible = _ => true;
        public string? NameOf(string id) => id == "pop" ? "Popup" : null;
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id)
        {
            onLook?.Invoke();
            return id == "pop" && Visible(clock.NowMs) ? new Sighting(new Detection(new Rect(760, 380, 80, 40), 0.97, 1), (long)clock.NowMs, DateTime.UtcNow) : null;
        }
    }

    static MacroEvent Mark(double t, int x) => new(t, EventKind.MouseMove, X: x, Y: 0);
    static MacroEvent When(double t, FlowOptions f) => new(t, EventKind.When, Target: "pop", Flow: f, Step: new StepOptions());

    sealed class Rig
    {
        public FakeClock Clock = new(); public RecordingSink Out; public Screen Scr; public ExecutionLog Log = new();
        public Dictionary<string, MacroEvent[]> Lib = new();
        public FlowRunner? Flow; public Player? P;
        public Func<MacroEvent[], Func<int>, (IInputSink, IInputSink)>? Chain;
        public Rig() { Out = new RecordingSink(Clock); Scr = new Screen(Clock, () => Clock.Now += 10); }
        public Player Play(MacroEvent[] main, int loops = 1)
        {
            var p = P = new Player(Out, Clock) { Log = Log };
            p.Flow = Flow = new FlowRunner(Scr, Clock, () => p.StopRequested, Out, id => Lib.GetValueOrDefault(id), Chain ?? ((_, _) => (Out, Out)), () => 1, Log);
            p.Run(main, 0, main.Length, loops);
            return p;
        }
        public int[] Xs => Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => s.E.X).ToArray();
    }

    [Fact]
    public void Restart_inside_a_connected_macro_restarts_the_main_macro_and_releases_held_keys()
    {
        var r = new Rig();
        r.Lib["menu"] = [new MacroEvent(0, EventKind.KeyDown, Vk: 65), Mark(5, 50), When(10, new FlowOptions { Do = FlowDo.Restart, MaxRuns = 2 })];
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "menu", WaitMs = 100 }), Mark(20, 2)]);
        Assert.Equal([1, 50, 1, 50, 1, 50, 2], r.Xs);             // two restarts of the MAIN macro (a sub-only restart would never show a second 1)
        Assert.Equal(3, r.Out.Sent.Count(s => s.E.Kind == EventKind.KeyDown));
        Assert.Equal(3, r.Out.Sent.Count(s => s.E.Kind == EventKind.KeyUp));   // every pass released its key: at each restart, and at the normal end of the last one
    }

    [Fact]
    public void After_restart_in_a_deep_macro_reaches_the_top()
    {
        var r = new Rig();
        r.Lib["a"] = [Mark(0, 50), When(5, new FlowOptions { Macro = "b", WaitMs = 100 }), Mark(20, 59)];
        r.Lib["b"] = [Mark(0, 60), When(5, new FlowOptions { Do = FlowDo.Restart, MaxRuns = 1 })];
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "a", WaitMs = 100 }), Mark(20, 2)]);
        Assert.Equal([1, 50, 60, 1, 50, 60, 59, 2], r.Xs);        // 59 (rest of a) is skipped on the restart pass only
    }

    [Fact]
    public void MaxRuns_of_a_nested_block_counts_across_all_calls_of_the_run()
    {
        var r = new Rig();
        r.Lib["menu"] = [When(0, new FlowOptions { Macro = "fix", WaitMs = 50, MaxRuns = 2 })];
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Macro = "menu", After = FlowAfter.Restart, WaitMs = 50, MaxRuns = 6 })]);
        Assert.Equal(2, r.Xs.Count(x => x == 100));               // a fresh child runner per call used to reset this and run it 6 times
    }

    [Fact]
    public void A_runaway_loop_is_stopped_by_the_run_wide_cap_with_a_clear_message()
    {
        var r = new Rig();
        r.Lib["tick"] = [Mark(0, 7)];
        var p = r.Play([When(0, new FlowOptions { Macro = "tick", After = FlowAfter.Restart, WaitMs = 20, MaxRuns = 0 }), Mark(10, 2)]);
        Assert.Equal(FlowRunState.MaxNestedRuns, r.Xs.Count(x => x == 7));
        Assert.DoesNotContain(2, r.Xs);
        Assert.Contains(r.Log.Snapshot(50), e => e.Result == LogResult.Failed && e.Message.Contains("10,000"));
        Assert.Null(p.Error);
        Assert.Equal(FlowStep.Stop, r.Flow!.Poll());                // the halted run answers Stop to anyone who polls (executors' Idle hook)
    }

    /// <summary>The popup from <see cref="Screen"/> plus a target "slow" that never shows.</summary>
    sealed class SlowFinder(Screen inner, FakeClock clock) : ITargetFinder
    {
        public string? NameOf(string id) => id == "slow" ? "Slow" : inner.NameOf(id);
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) { if (id != "slow") return inner.Find(id); clock.Now += 10; return null; }
    }

    [Fact]
    public void A_watcher_fires_while_a_connected_macro_waits_for_a_click_target()
    {
        var r = new Rig(); r.Scr.Visible = t => t is > 1000 and < 1500;
        var fm = new SlowFinder(r.Scr, r.Clock);
        r.Lib["menu"] = [Mark(0, 50), new MacroEvent(10, EventKind.WaitTarget, Target: "slow", Delta: 4000, Extended: true), Mark(20, 51)];
        r.Lib["fix"] = [Mark(0, 100)];
        r.Chain = (_, _) => { var ex = new StepExecutor(r.Out, fm, r.Clock, () => false, r.Log); return (ex, ex); };
        var p = r.P = new Player(r.Out, r.Clock) { Log = r.Log };
        p.Flow = r.Flow = new FlowRunner(fm, r.Clock, () => p.StopRequested, r.Out, id => r.Lib.GetValueOrDefault(id), r.Chain, () => 1, r.Log);
        var main = new[] { When(0, new FlowOptions { Always = true, Macro = "fix" }), When(10, new FlowOptions { Macro = "menu", Condition = WhenCondition.NotFound, WaitMs = 10 }), Mark(5000, 2) };
        p.Run(main, 0, main.Length, 1);
        Assert.Contains(100, r.Xs);
        Assert.True(r.Xs.ToList().IndexOf(100) > r.Xs.ToList().IndexOf(50));
    }

    [Fact]
    public void An_endless_main_loop_is_not_halted_by_the_nested_run_cap()
    {
        var r = new Rig(); r.Lib["tick"] = [Mark(0, 7)];
        r.Out.OnSend = n => { if (n >= 30_000) r.P!.Stop(); };
        r.Play([When(0, new FlowOptions { Macro = "tick", WaitMs = 20, MaxRuns = 0 })], loops: 0);
        Assert.True(r.Xs.Count(x => x == 7) >= 30_000);
        Assert.DoesNotContain(r.Log.Snapshot(), e => e.Result == LogResult.Failed);
    }

    [Fact]
    public void A_watcher_acts_once_per_appearance()
    {
        var r = new Rig(); r.Scr.Visible = t => t is > 100 and < 6000;       // one long appearance, far beyond the 2 s cooldown
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Always = true, Macro = "fix", MaxRuns = 0 }), Mark(10, 1), Mark(8000, 2)]);
        Assert.Equal(1, r.Xs.Count(x => x == 100));
        Assert.Equal(2, r.Xs[^1]);

        var again = new Rig(); again.Scr.Visible = t => t is > 100 and < 600 || t is > 3000 and < 3500;    // it goes away and comes back: that is a new appearance
        again.Lib["fix"] = [Mark(0, 100)];
        again.Play([When(0, new FlowOptions { Always = true, Macro = "fix", MaxRuns = 0 }), Mark(10, 1), Mark(8000, 2)]);
        Assert.Equal(2, again.Xs.Count(x => x == 100));
    }

    [Fact]
    public void Watchers_armed_in_a_connected_macro_are_gone_when_it_ends()
    {
        var r = new Rig(); r.Scr.Visible = t => t > 1500;
        r.Lib["menu"] = [When(0, new FlowOptions { Always = true, Macro = "fix" }), Mark(10, 50)];
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Macro = "menu", Condition = WhenCondition.NotFound, WaitMs = 50 }), Mark(5000, 2)]);     // the popup shows up long after "menu" has ended
        Assert.Equal([50, 2], r.Xs);
    }

    [Fact]
    public void Nothing_stays_armed_after_the_run_ends_or_restarts()
    {
        var r = new Rig(); r.Lib["fix"] = [Mark(0, 100)];
        r.Scr.Visible = _ => false;
        r.Play([When(0, new FlowOptions { Always = true, Macro = "fix" }), Mark(10, 1)]);
        r.Scr.Visible = _ => true; r.Clock.Now += 5000;
        Assert.Equal(FlowStep.Continue, r.Flow!.Poll());
        Assert.DoesNotContain(100, r.Xs);
    }

    [Fact]
    public void A_parent_watcher_keeps_looking_while_a_connected_macro_runs()
    {
        var r = new Rig(); r.Scr.Visible = t => t is > 500 and < 900;
        r.Lib["menu"] = [Mark(0, 50), Mark(2000, 51)];
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Always = true, Macro = "fix" }), When(10, new FlowOptions { Macro = "menu", WaitMs = 10, Condition = WhenCondition.NotFound }), Mark(20, 2)]);
        Assert.Equal([50, 100, 51, 2], r.Xs);
    }

    [Fact]
    public void A_watcher_that_restarts_from_inside_a_nested_macro_restarts_the_main_macro()
    {
        var r = new Rig(); r.Scr.Visible = t => t > 300;
        r.Lib["menu"] = [Mark(0, 50), Mark(1000, 51)];
        r.Play([Mark(0, 1), When(0, new FlowOptions { Always = true, Do = FlowDo.Restart, MaxRuns = 1 }), When(10, new FlowOptions { Macro = "menu", Condition = WhenCondition.NotFound, WaitMs = 10 }), Mark(5000, 2)]);
        Assert.Equal([1, 50, 1, 2], r.Xs);                      // the watcher fired inside the nested macro: it stopped, the main macro started over (not just the sub macro)
    }

    [Fact]
    public void Stop_inside_a_nested_macro_stops_every_level()
    {
        var r = new Rig();
        r.Lib["a"] = [Mark(0, 50), When(5, new FlowOptions { Macro = "b", WaitMs = 100 }), Mark(500, 59)];
        r.Lib["b"] = [Mark(0, 60), Mark(500, 61)];
        r.Out.OnSend = n => { if (n == 2) r.P!.Stop(); };
        r.Play([When(0, new FlowOptions { Macro = "a", WaitMs = 100 }), Mark(500, 2)]);
        Assert.Equal([50, 60], r.Xs);
        Assert.Equal("", r.Flow!.Trail.Text);
    }

    [Fact]
    public void A_failing_connected_macro_releases_its_keys_and_leaves_the_trail_clean()
    {
        var r = new Rig();
        var sink = new ThrowOn(r.Out, 666);
        r.Chain = (_, _) => (sink, sink);
        EventInfo.MacroNameOf = id => id;
        try
        {
            r.Lib["bad"] = [new MacroEvent(0, EventKind.KeyDown, Vk: 65), Mark(5, 666), Mark(10, 3)];
            r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "bad", WaitMs = 100 }), Mark(20, 2)]);
        }
        finally { EventInfo.MacroNameOf = null; }
        Assert.Equal([1, 2], r.Xs);                                  // the main macro carries on
        Assert.Contains(r.Out.Sent, s => s.E.Kind == EventKind.KeyUp && s.E.Vk == 65);
        Assert.Equal("", r.Flow!.Trail.Text);
        Assert.True(r.Log.Count(LogResult.Failed) >= 1);
    }

    [Fact]
    public void A_chain_that_cannot_be_built_is_logged_and_the_run_goes_on()
    {
        var r = new Rig(); r.Lib["m"] = [Mark(0, 5)];
        r.Chain = (_, _) => throw new InvalidOperationException("no chain");
        r.Play([When(0, new FlowOptions { Macro = "m", WaitMs = 50 }), Mark(10, 2)]);
        Assert.Equal([2], r.Xs);
        Assert.True(r.Log.Count(LogResult.Failed) >= 1);
        Assert.Equal("", r.Flow!.Trail.Text);
    }

    sealed class ThrowOn(IInputSink inner, int x) : IInputSink
    {
        public void Send(in MacroEvent e) { if (e.Kind == EventKind.MouseMove && e.X == x) throw new InvalidOperationException("boom"); inner.Send(in e); }
    }

    [Fact]
    public void Five_hundred_nested_calls_leak_no_threads_or_handles()
    {
        var b = new Rig(); b.Lib["tick"] = [Mark(0, 7)];
        GC.Collect(); GC.WaitForPendingFinalizers();
        var proc = Process.GetCurrentProcess();
        int threads = proc.Threads.Count, handles = proc.HandleCount;
        b.Play([When(0, new FlowOptions { Macro = "tick", After = FlowAfter.Restart, WaitMs = 20, MaxRuns = 500 }), Mark(10, 2)]);
        Assert.Equal(500, b.Xs.Count(x => x == 7));
        GC.Collect(); GC.WaitForPendingFinalizers();
        proc.Refresh();
        Assert.InRange(proc.Threads.Count - threads, -10, 3);
        Assert.InRange(proc.HandleCount - handles, -50, 20);
    }

    [Fact]
    public void Polling_with_nothing_armed_allocates_nothing()
    {
        var r = new Rig();
        var flow = new FlowRunner(r.Scr, r.Clock, () => false, r.Out, _ => null, (_, _) => (r.Out, r.Out));
        flow.Poll();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++) flow.Poll();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024);
    }

    // ---- real threads: pause / step reach the nested player ----

    sealed class LockedSink : IInputSink
    {
        public readonly List<int> Xs = new();
        public void Send(in MacroEvent e) { if (e.Kind == EventKind.MouseMove) lock (Xs) Xs.Add(e.X); }
        public int[] Snap() { lock (Xs) return Xs.ToArray(); }
    }

    static (Player P, LockedSink S, ManualResetEventSlim Done) StartReal(MacroEvent[] main, MacroEvent[] menu, bool step)
    {
        var sink = new LockedSink(); var clock = new RealClock(); var done = new ManualResetEventSlim();
        var scr = new Screen(clock);
        var p = new Player(sink, clock);
        p.Flow = new FlowRunner(scr, clock, () => p.StopRequested, sink, id => id == "menu" ? menu : null, (_, _) => (sink, sink));
        p.Finished += done.Set;
        p.Start(main, 0, main.Length, 1, step);
        return (p, sink, done);
    }

    [Fact]
    public void Pausing_the_main_player_pauses_the_connected_macro_and_resume_continues_there()
    {
        var (p, s, done) = StartReal([When(0, new FlowOptions { Macro = "menu", WaitMs = 100 }), Mark(10, 2)], [Mark(0, 50), Mark(400, 51), Mark(500, 52)], false);
        SpinWait.SpinUntil(() => s.Snap().Length >= 1, 2000);
        p.Pause();
        Thread.Sleep(100); int n = s.Snap().Length;
        Thread.Sleep(900);                                          // far past 51 and 52 due times
        Assert.Equal(n, s.Snap().Length);
        p.Resume();
        Assert.True(done.Wait(3000));
        Assert.Equal([50, 51, 52, 2], s.Snap());
    }

    [Fact]
    public void Step_treats_a_when_block_as_one_step_and_the_connected_macro_runs_through()
    {
        var (p, s, done) = StartReal([Mark(0, 1), When(10, new FlowOptions { Macro = "menu", WaitMs = 100 }), Mark(20, 2)], [Mark(0, 50), Mark(10, 51)], true);
        void Step() { p.Step(); Thread.Sleep(300); }
        Step();
        Assert.Equal([1], s.Snap());
        Step();                                                    // the When step: the whole connected macro runs
        Assert.Equal([1, 50, 51], s.Snap());
        Step();
        Assert.True(done.Wait(2000));
        Assert.Equal([1, 50, 51, 2], s.Snap());
    }
}
