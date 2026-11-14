using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// Realistic nested-macro scenarios (main -> A -> B -> C, disconnect watchers, restart/stop/pause at depth, recursion, leaks).
/// Own rig: virtual clock with timers and a runaway guard, scripted screen, recording sink. Tests marked "RED until" expose behaviour the contract wants.
/// </summary>
public class FlowScenarioTests
{
    // ---------- rig ----------

    sealed class SClock : IClock
    {
        public double Now; public double NowMs => Now;
        public double Limit = 3_600_000; public Action? Runaway;
        readonly List<(double At, Action Do)> _timers = new();
        public void At(double ms, Action a) => _timers.Add((ms, a));
        public void Sleep(double ms)
        {
            Now += Math.Max(ms, 0.01);
            for (int i = 0; i < _timers.Count; i++)
                if (Now >= _timers[i].At) { var a = _timers[i].Do; _timers.RemoveAt(i--); a(); }
            if (Now > Limit) Runaway?.Invoke();
        }
    }

    sealed class Sink(SClock clock) : IInputSink
    {
        public readonly List<(double At, MacroEvent E)> Sent = new();
        public bool Keep = true; public int Count; public Action<MacroEvent>? OnSend; public Action? Runaway;
        public void Send(in MacroEvent e)
        {
            var ev = e;
            if (Keep) Sent.Add((clock.Now, ev));
            if (++Count > 200_000) Runaway?.Invoke();
            OnSend?.Invoke(ev);
        }
    }

    /// <summary>Targets are visible while their function says so; every look costs 10 virtual ms.</summary>
    sealed class Screen(SClock clock) : ITargetFinder
    {
        public readonly Dictionary<string, Func<double, bool>> Vis = new();
        public string? NameOf(string id) => Vis.ContainsKey(id) ? id : null;
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id)
        {
            clock.Now += 10;
            return Vis.TryGetValue(id, out var v) && v(clock.Now) ? new Sighting(new Detection(new Rect(760, 380, 80, 40), 0.97, 1), (long)clock.Now, DateTime.UtcNow) : null;
        }
    }

    sealed class Rig
    {
        public readonly SClock Clock = new(); public readonly Sink Out; public readonly Screen Scr; public readonly ExecutionLog Log = new();
        public readonly Dictionary<string, MacroEvent[]> Lib = new();
        public Player Top = null!; public FlowRunner Root = null!; public bool Runaway;
        public Rig()
        {
            Out = new Sink(Clock); Scr = new Screen(Clock);
            Clock.Runaway = Out.Runaway = () => { Runaway = true; Top?.Stop(); };
            foreach (var id in new[] { "pop", "menu", "aotr", "deep", "disc", "slow" }) Scr.Vis[id] = _ => true;
        }
        public bool RealExecutors;      // nested macros get a StepExecutor chain like the app builds (waiting click steps)
        public Player Run(MacroEvent[] main, int loops = 1)
        {
            var p = new Player(Out, Clock) { Log = Log }; Top = p;
            Root = new FlowRunner(Scr, Clock, () => p.StopRequested, Out, id => Lib.GetValueOrDefault(id), (_, _) => { if (!RealExecutors) return (Out, Out); var ex = new StepExecutor(Out, Scr, Clock, () => p.StopRequested, Log); return (ex, ex); }, () => 1, Log);
            p.Flow = Root;
            p.Run(main, 0, main.Length, loops);
            Assert.False(Runaway, "runaway: the run did not end by itself (virtual time or event cap hit)");
            return p;
        }
        public int[] Xs => Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => s.E.X).ToArray();
        public string[] Sigs => Out.Sent.Select(s => Sig(s.E)).ToArray();
        public bool LogHas(LogResult r, string text) => Log.Snapshot().Any(l => l.Result == r && l.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    static string Sig(MacroEvent e) => e.Kind switch
    {
        EventKind.MouseMove => "m" + e.X, EventKind.KeyDown => "d" + e.Vk, EventKind.KeyUp => "u" + e.Vk,
        EventKind.ButtonDown => "bd", EventKind.ButtonUp => "bu", _ => e.Kind.ToString()
    };

    static MacroEvent M(double t, int x) => new(t, EventKind.MouseMove, X: x, Y: 0);
    static MacroEvent KD(double t, int vk) => new(t, EventKind.KeyDown, Vk: vk);
    static MacroEvent KU(double t, int vk) => new(t, EventKind.KeyUp, Vk: vk);
    static MacroEvent W(double t, string target, FlowOptions f) => new(t, EventKind.When, Target: target, Flow: f);
    /// <summary>Check here, run <paramref name="macro"/>, unlimited acts.</summary>
    static MacroEvent Call(double t, string target, string macro, int waitMs = 500) => W(t, target, new FlowOptions { Macro = macro, WaitMs = waitMs, MaxRuns = 0 });

    static int[] Rep(int[] a, int n) => Enumerable.Range(0, n).SelectMany(_ => a).ToArray();
    static string[] Rep(string[] a, int n) => Enumerable.Range(0, n).SelectMany(_ => a).ToArray();

    // ---------- (a) chains ----------

    [Fact]
    public void Chain_main_menu_aotr_deeper_with_check_here_at_the_end_of_each_middle_macro()
    {
        var r = new Rig();
        r.Lib["menu"] = [M(0, 100), M(10, 101), Call(20, "aotr", "aotr")];       // the player selection is the LAST step of Main menu
        r.Lib["aotr"] = [M(0, 200), Call(10, "deep", "deep")];
        r.Lib["deep"] = [M(0, 300)];
        r.Run([M(0, 1), Call(10, "menu", "menu"), M(20, 2)]);
        Assert.Equal([1, 100, 101, 200, 300, 2], r.Xs);
        Assert.True(r.LogHas(LogResult.Ok, "'deep' finished"));
    }

    [Fact]
    public void Nesting_stops_at_depth_six_with_a_clear_log_and_the_main_macro_carries_on()
    {
        var r = new Rig();
        for (int k = 1; k <= 8; k++) r.Lib["n" + k] = k < 8 ? [M(0, k * 10), Call(10, "pop", "n" + (k + 1))] : [M(0, 80)];
        r.Run([Call(0, "pop", "n1"), M(10, 2)]);
        Assert.Equal([10, 20, 30, 40, 50, 60, 2], r.Xs);
        Assert.True(r.LogHas(LogResult.Failed, "deep"));
    }

    // ---------- (b) disconnect handling ----------

    static FlowOptions Disconnect(int maxRuns) => new() { Always = true, Macro = "rejoin", After = FlowAfter.Restart, MaxRuns = maxRuns };

    [Fact]
    public void Disconnect_popup_three_separate_times_during_long_waits_restarts_main_each_time()
    {
        var r = new Rig();
        r.Lib["rejoin"] = [M(0, 900), M(50, 901)];
        int Rejoins() => r.Xs.Count(x => x == 900);
        double[] appear = [1000, 4000, 9000];
        r.Scr.Vis["disc"] = t => { int k = Rejoins(); return k < 3 && t >= appear[k]; };
        r.Run([W(0, "disc", Disconnect(3)), M(10, 1), M(2000, 2), M(4000, 3), M(7000, 4)]);
        Assert.Equal([1, 900, 901, 1, 2, 900, 901, 1, 2, 3, 900, 901, 1, 2, 3, 4], r.Xs);
        Assert.Equal(3, r.Xs.Count(x => x == 900));
    }

    [Fact]
    public void Disconnect_popup_while_a_nested_macro_is_waiting_interrupts_it_and_restarts_main()
    {
        // RED until E/F fix: the watcher of the top-level macro is only polled by the top Player; while a nested macro runs nobody looks at it.
        var r = new Rig();
        r.Lib["rejoin"] = [M(0, 900), M(50, 901)];
        r.Lib["menu"] = [M(0, 100), M(4000, 101)];
        r.Scr.Vis["disc"] = t => !r.Xs.Contains(900) && t >= 1000;
        r.Run([W(0, "disc", Disconnect(3)), M(10, 1), Call(20, "menu", "menu"), M(30, 2)]);
        Assert.Equal([1, 100, 900, 901, 1, 100, 101, 2], r.Xs);
    }


    [Fact]
    public void Disconnect_popup_while_a_nested_macro_waits_for_a_click_target_restarts_main()
    {
        // RED until E/F fix: the app's nested StepExecutor has no Idle hook (MainWindow only sets it on the main executor), so a watcher is never
        // polled while a nested click/wait step waits for its target (the usual long wait inside a connected macro).
        var r = new Rig { RealExecutors = true };
        r.Scr.Vis["slow"] = _ => false;
        r.Lib["rejoin"] = [M(0, 900), M(50, 901)];
        r.Lib["menu"] = [M(0, 100), new MacroEvent(10, EventKind.WaitTarget, Target: "slow", Delta: 4000, Extended: true), M(20, 101)];
        r.Scr.Vis["disc"] = t => !r.Xs.Contains(900) && t >= 1000;
        r.Run([W(0, "disc", Disconnect(3)), M(10, 1), Call(20, "menu", "menu"), M(30, 2)]);
        Assert.Equal([1, 100, 900, 901, 1, 100, 101, 2], r.Xs);
    }
    [Fact]
    public void One_appearance_runs_the_handler_once_even_if_the_popup_stays_up()
    {
        // RED until E fixes: an 'Always' watcher re-fires every cooldown (2 s) while the same popup stays visible.
        var r = new Rig();
        r.Lib["fix"] = [M(0, 100)];
        r.Scr.Vis["pop"] = t => t is >= 1000 and <= 6000;
        r.Run([W(0, "pop", new FlowOptions { Always = true, Macro = "fix", MaxRuns = 0 }), M(10, 1), M(8000, 2)]);
        Assert.Equal([1, 100, 2], r.Xs);
    }

    // ---------- (c) repeated execution ----------

    [Fact]
    public void Loops_three_runs_the_nested_macro_identically_each_time_and_releases_its_held_key()
    {
        var r = new Rig();
        r.Lib["menu"] = [KD(0, 65), M(20, 100)];            // leaves A held: the nested player must release it every time
        r.Run([M(0, 1), Call(10, "menu", "menu"), M(20, 2)], loops: 3);
        Assert.Equal(Rep(["m1", "d65", "m100", "u65", "m2"], 3), r.Sigs);
    }

    [Fact]
    public void Infinite_loop_with_stop_ends_cleanly_after_the_requested_iterations()
    {
        var r = new Rig();
        r.Lib["menu"] = [KD(0, 65), M(20, 100)];
        int twos = 0;
        r.Out.OnSend = e => { if (e.Kind == EventKind.MouseMove && e.X == 2 && ++twos == 5) r.Top.Stop(); };
        r.Run([M(0, 1), Call(10, "menu", "menu"), M(20, 2)], loops: 0);
        Assert.Equal(Rep(["m1", "d65", "m100", "u65", "m2"], 5), r.Sigs);
        Assert.Equal("", r.Root.Trail.Text.Replace(" ", ""));
    }

    // ---------- (d) restart from depth 3 ----------

    [Fact]
    public void Restart_at_depth_three_restarts_the_main_macro_with_held_keys_released_at_every_level()
    {
        // RED until E fixes: a restart inside a nested player only restarts that player; the per-runner MaxRuns also resets on every call, so it never ends.
        var r = new Rig();
        r.Lib["a"] = [KD(0, 70), M(10, 10), Call(20, "pop", "b")];
        r.Lib["b"] = [KD(0, 71), M(10, 20), Call(20, "pop", "c")];
        r.Lib["c"] = [KD(0, 72), M(10, 30), W(20, "pop", new FlowOptions { Do = FlowDo.Restart, MaxRuns = 2 })];
        r.Run([M(0, 1), Call(10, "pop", "a")]);
        Assert.Equal(Rep([1, 10, 20, 30], 3), r.Xs);                 // 2 restarts, then the limit lets the third pass finish
        var held = new HashSet<int>();
        foreach (var s in r.Out.Sent)
        {
            if (s.E.Kind == EventKind.MouseMove && s.E.X == 1) Assert.Empty(held);     // main restarts from event 0 with nothing held
            if (s.E.Kind == EventKind.KeyDown) held.Add(s.E.Vk); else if (s.E.Kind == EventKind.KeyUp) held.Remove(s.E.Vk);
        }
        Assert.Empty(held);
    }

    // ---------- (e) stop at depth ----------

    [Fact]
    public void Stop_at_depth_three_in_the_middle_of_a_wait_ends_everything_quickly_and_releases_keys()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 10), Call(10, "pop", "b")];
        r.Lib["b"] = [M(0, 20), Call(10, "pop", "c")];
        r.Lib["c"] = [KD(0, 66), M(0, 30), M(60000, 31)];
        r.Clock.At(2000, () => r.Top.Stop());
        r.Run([M(0, 1), Call(10, "pop", "a"), M(20, 99)]);
        Assert.DoesNotContain(31, r.Xs); Assert.DoesNotContain(99, r.Xs);
        Assert.InRange(r.Clock.Now, 2000, 2200);
        Assert.Contains("u66", r.Sigs);
        Assert.Equal("", r.Root.Trail.Text);
    }

    // ---------- (f) pause / resume ----------

    [Fact]
    public void Pause_during_a_nested_macro_pauses_it_too_and_resume_continues_there()
    {
        // RED until E fixes: nested players have their own _paused flag, so the nested macro keeps firing while the top-level macro is "paused".
        var r = new Rig();
        r.Lib["a"] = [M(0, 100), M(1000, 101), M(2000, 102)];
        r.Clock.At(1500, () => r.Top.Pause());
        r.Clock.At(5000, () => r.Top.Resume());
        r.Run([M(0, 1), Call(10, "pop", "a"), M(20, 2)]);
        var during = r.Out.Sent.Where(s => s.At is > 1600 and < 4990).Select(s => Sig(s.E)).ToArray();
        Assert.Empty(during);
        var at102 = r.Out.Sent.First(s => s.E.X == 102 && s.E.Kind == EventKind.MouseMove).At;
        Assert.InRange(at102, 5000, 5400);
        Assert.Equal([1, 100, 101, 102, 2], r.Xs);
    }

    // ---------- (g) recursion ----------

    [Fact]
    public void A_macro_that_calls_itself_ends_with_a_clear_log()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 1), Call(10, "pop", "a")];
        r.Run([Call(0, "pop", "a"), M(10, 9)]);
        Assert.Equal(9, r.Xs[^1]);
        Assert.True(r.Xs.Length <= 8);
        Assert.True(r.LogHas(LogResult.Failed, "deep") || r.LogHas(LogResult.Failed, "itself") || r.LogHas(LogResult.Failed, "recurs") || r.LogHas(LogResult.Failed, "cycle"));
    }

    [Fact]
    public void Mutual_recursion_A_calls_B_calls_A_ends_with_a_clear_log()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 1), Call(10, "pop", "b")];
        r.Lib["b"] = [M(0, 2), Call(10, "pop", "a")];
        r.Run([Call(0, "pop", "a"), M(10, 9)]);
        Assert.Equal(9, r.Xs[^1]);
        Assert.True(r.Xs.Length <= 14);
        Assert.True(r.LogHas(LogResult.Failed, "deep") || r.LogHas(LogResult.Failed, "itself") || r.LogHas(LogResult.Failed, "recurs") || r.LogHas(LogResult.Failed, "cycle"));
    }

    // ---------- (h) KeepWaiting late appearance ----------

    [Fact]
    public void KeepWaiting_at_depth_two_waits_for_a_late_target_then_runs_its_macro_and_returns()
    {
        var r = new Rig();
        r.Scr.Vis["deep"] = t => t >= 30_000;
        r.Lib["a"] = [M(0, 100), W(10, "deep", new FlowOptions { KeepWaiting = true, WaitMs = 1000, Macro = "c", MaxRuns = 0 }), M(20, 101)];
        r.Lib["c"] = [M(0, 200)];
        r.Run([Call(0, "pop", "a"), M(10, 2)]);
        Assert.Equal([100, 200, 101, 2], r.Xs);
        Assert.InRange(r.Clock.Now, 30_000, 30_700);
        Assert.False(r.LogHas(LogResult.Warn, "carrying on"));
    }

    // ---------- (i) missing connected macro ----------

    [Fact]
    public void A_missing_connected_macro_mid_chain_is_logged_and_the_chain_carries_on()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 10), Call(10, "pop", "ghost"), M(20, 11)];
        r.Run([M(0, 1), Call(10, "pop", "a"), M(20, 2)]);
        Assert.Equal([1, 10, 11, 2], r.Xs);
        Assert.True(r.LogHas(LogResult.Failed, "not found"));
    }

    // ---------- (j) exception at depth 2 ----------

    [Fact]
    public void A_sink_exception_at_depth_two_releases_held_keys_and_leaves_the_trail_empty()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 10), Call(10, "pop", "b")];
        r.Lib["b"] = [KD(0, 65), M(10, 300), M(20, 301)];
        r.Out.OnSend = e => { if (e.Kind == EventKind.MouseMove && e.X == 300) throw new InvalidOperationException("boom"); };
        r.Run([M(0, 1), Call(10, "pop", "a"), M(20, 2)]);
        var sigs = r.Sigs;
        Assert.True(Array.IndexOf(sigs, "u65") > Array.IndexOf(sigs, "m300"), "the key held by the failing macro must be released");
        Assert.DoesNotContain("m301", sigs);
        Assert.Equal("", r.Root.Trail.Text);
        Assert.True(r.LogHas(LogResult.Failed, "boom"));
    }

    // ---------- (k) trail ----------

    [Fact]
    public void The_trail_names_the_running_macros_outermost_first_and_is_empty_afterwards()
    {
        var r = new Rig();
        r.Lib["menu"] = [M(0, 100), Call(10, "aotr", "aotr"), M(20, 101)];
        r.Lib["aotr"] = [M(0, 200), Call(10, "deep", "deep"), M(20, 201)];
        r.Lib["deep"] = [M(0, 300)];
        var seen = new List<(int X, string Trail)>();
        r.Out.OnSend = e => seen.Add((e.X, r.Root.Trail.Text));
        r.Run([M(0, 1), Call(10, "menu", "menu"), M(20, 2)]);
        string T(int x) => seen.First(s => s.X == x).Trail;
        Assert.DoesNotContain("menu", T(1));
        Assert.EndsWith("menu", T(100));
        Assert.EndsWith("menu › aotr", T(200));
        Assert.EndsWith("menu › aotr › deep", T(300));
        Assert.EndsWith("menu › aotr", T(201));
        Assert.EndsWith("menu", T(101));
        Assert.DoesNotContain("menu", T(2));
        Assert.DoesNotContain("menu", r.Root.Trail.Text);
    }

    // ---------- (l) / (m) leaks ----------

    [Fact]
    public void Three_hundred_nested_calls_do_not_grow_threads_or_handles()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 10), Call(10, "pop", "b")];
        r.Lib["b"] = [M(0, 20)];
        r.Out.Keep = false;
        var main = new[] { Call(0, "pop", "a") };
        r.Run(main, 5);
        GC.Collect(); GC.WaitForPendingFinalizers();
        var pr = Process.GetCurrentProcess(); pr.Refresh();
        int th = pr.Threads.Count, hd = pr.HandleCount;
        r.Run(main, 300);
        GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(100);
        pr.Refresh();
        Assert.InRange(pr.Threads.Count, 0, th + 3);
        Assert.InRange(pr.HandleCount, 0, hd + 40);
    }

    [Fact]
    public void Allocations_per_nested_call_stay_bounded_over_two_thousand_calls()
    {
        var r = new Rig();
        r.Lib["a"] = [M(0, 1), M(1, 2), M(2, 3), M(3, 4), M(4, 5)];
        r.Out.Keep = false;
        var main = new[] { Call(0, "pop", "a") };
        r.Run(main, 100);                                              // warm-up (JIT)
        GC.Collect(); long mem0 = GC.GetTotalMemory(true);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        r.Run(main, 500);
        long perA = (GC.GetAllocatedBytesForCurrentThread() - b0) / 500;
        b0 = GC.GetAllocatedBytesForCurrentThread();
        r.Run(main, 1500);
        long perB = (GC.GetAllocatedBytesForCurrentThread() - b0) / 1500;
        long mem1 = GC.GetTotalMemory(true);
        Assert.True(perB < 40_000, $"{perB} bytes per nested call");
        Assert.True(perB <= perA * 1.5 + 2000, $"per-call allocation grows: {perA} -> {perB}");
        Assert.True(mem1 - mem0 < 8_000_000, $"retained {mem1 - mem0} bytes");
    }
}
