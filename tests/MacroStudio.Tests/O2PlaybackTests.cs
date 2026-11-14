using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>
/// O2: playback-execution hardening — cross-level "last seen" click positions, the same one-look debounce for
/// watching Disappears blocks that waiting steps already have, stop propagation through Poll, throttled Prioritise
/// calls, and errors that must not be swallowed.
/// </summary>
public class O2PlaybackTests
{
    /// <summary>Scripted screen: each target id has its own visibility function; every look costs 10 virtual ms.</summary>
    sealed class Screen(FakeClock clock) : ITargetFinder
    {
        public readonly Dictionary<string, Func<bool>> Vis = new();
        public int Polls;
        public Func<int, (int X, int Y)>? Where;      // scripted top-left per look (default: fixed)
        public string? NameOf(string id) => Vis.ContainsKey(id) ? id : null;
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id)
        {
            clock.Now += 10; Polls++;
            if (!(Vis.TryGetValue(id, out var v) && v())) return null;
            var (x, y) = Where?.Invoke(Polls) ?? (760, 380);
            return new Sighting(new Detection(new Rect(x, y, 80, 40), 0.97, 1), (long)clock.Now, DateTime.UtcNow);
        }
    }

    sealed class Rig
    {
        public FakeClock Clock = new(); public RecordingSink Out; public Screen Scr; public ExecutionLog Log = new();
        public Dictionary<string, MacroEvent[]> Lib = new();
        public Func<MacroEvent[], Func<int>, (IInputSink, IInputSink)>? Chain;
        public Rig() { Out = new RecordingSink(Clock); Scr = new Screen(Clock); }
        public Player Play(MacroEvent[] main, int loops = 1)
        {
            var p = new Player(Out, Clock) { Log = Log };
            p.Flow = new FlowRunner(Scr, Clock, () => p.StopRequested, Out, id => Lib.GetValueOrDefault(id), Chain ?? ((_, _) => (Out, Out)), () => 1, Log);
            p.Run(main, 0, main.Length, loops);
            return p;
        }
        public int[] Xs => Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => s.E.X).ToArray();
    }

    static MacroEvent Mark(double t, int x) => new(t, EventKind.MouseMove, X: x, Y: 0);
    static MacroEvent When(double t, string target, FlowOptions f, StepOptions? o = null) => new(t, EventKind.When, Target: target, Flow: f, Step: o);

    /// <summary>
    /// The "click it first" position for a Disappears watcher must be the last sighting — and sightings are also made
    /// while a CONNECTED macro's own Poll scans the shared watchers. Without a shared record, the click has nowhere to land.
    /// </summary>
    [Fact]
    public void Click_first_after_a_disappearance_seen_by_the_nested_runner_clicks_the_last_spot()
    {
        var r = new Rig();
        int looks = 0;
        // "pop" is seen at arming and for the first looks inside the connected macro, then is gone for good;
        // "go" is always visible (it only launches the connected macro; both When steps are due back to back, so the
        // main player's own Poll never gets a look at "pop").
        r.Scr.Vis["pop"] = () => ++looks <= 3;
        r.Scr.Vis["go"] = () => true;
        r.Lib["menu"] = [Mark(0, 50), Mark(3000, 51)];
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([
            When(0, "pop", new FlowOptions { Always = true, Condition = WhenCondition.Disappears, ClickFirst = true, Macro = "fix" }, new StepOptions()),
            When(0, "go", new FlowOptions { Macro = "menu", WaitMs = 100 }),
        ]);
        Assert.Contains(r.Out.Sent, s => s.E.Kind == EventKind.ButtonDown && s.E.X == 800 && s.E.Y == 400);   // the last seen centre
        Assert.Contains(r.Out.Sent, s => s.E.Kind == EventKind.ButtonUp);
        Assert.Equal([50, 800, 100, 51], r.Xs);                              // approach to the click point, handler inside the nested wait
        Assert.DoesNotContain(r.Log.Snapshot(), l => l.Message.Contains("never seen"));
    }

    /// <summary>
    /// A watching Disappears block must debounce like every waiting step does (200 ms of continuous absence): one missed
    /// frame is detection noise, not a disappearance. Only the real disappearance may act.
    /// </summary>
    [Fact]
    public void A_disappears_watcher_ignores_a_single_missed_look()
    {
        var r = new Rig();
        int looks = 0;
        r.Scr.Vis["pop"] = () => { var n = ++looks; return n != 4 && n < 8; };   // one missed frame at look 4; really gone from look 8 on
        r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, "pop", new FlowOptions { Always = true, Condition = WhenCondition.Disappears, Macro = "fix" }), Mark(10, 1), Mark(6000, 2)]);
        Assert.Equal(1, r.Xs.Count(x => x == 100));
        Assert.Equal([1, 100, 2], r.Xs);
    }

    /// <summary>An executor's Idle hook breaks its wait only when Poll says Stop — a plain stop request must reach it, not just a halt.</summary>
    [Fact]
    public void Poll_reports_stop_once_the_run_is_stopping()
    {
        var r = new Rig();
        bool stop = false;
        r.Scr.Vis["pop"] = () => false;
        var flow = new FlowRunner(r.Scr, r.Clock, () => stop, r.Out, _ => null, (_, _) => (r.Out, r.Out));
        Assert.Equal(FlowStep.Continue, flow.OnWhen(When(0, "pop", new FlowOptions { Always = true, Macro = "x" }), 0));
        Assert.Equal(FlowStep.Continue, flow.Poll());
        stop = true;
        Assert.Equal(FlowStep.Stop, flow.Poll());
    }

    /// <summary>Every Find used to wake the watcher thread for a frame grab; a wait loop asks dozens of times a second. Once per few hundred ms per target keeps the boost window alive.</summary>
    [Fact]
    public void Find_prioritises_the_watcher_at_most_once_per_throttle_window()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-o2-" + Guid.NewGuid().ToString("N"));
        var store = new TargetStore(dir);
        try
        {
            var now = DateTime.UtcNow;
            var desk = new DesktopInfo(new ScreenRect(0, 0, 100, 100), [new ScreenRect(0, 0, 100, 100)]);
            using var watcher = new TargetWatcher(store, _ => new Mat(10, 10, MatType.CV_8UC3), desk, 96, threaded: false);
            using var finder = new ScreenTargetFinder(watcher, [new Target { Id = "a" }, new Target { Id = "b" }], () => now);
            finder.Find("a"); finder.Find("a"); finder.Find("a");
            Assert.Equal(1, finder.PrioritiseCalls);
            now = now.AddMilliseconds(500);
            finder.Find("a");
            Assert.Equal(2, finder.PrioritiseCalls);
            finder.Find("b");                                                  // per target, not per call
            Assert.Equal(3, finder.PrioritiseCalls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A stop requested while the pointer is settling onto the target must abandon the click entirely: "landed" is only
    /// meaningful when the pointer really arrived. The aim point is not a landed position — clicking it is the click the user tried to prevent.
    /// </summary>
    [Fact]
    public void Stop_while_settling_onto_the_target_never_sends_the_click()
    {
        var clock = new FakeClock(); var inner = new RecordingSink(clock); var log = new ExecutionLog();
        bool stop = false;
        var fin = new Screen(clock);
        fin.Vis["abc"] = () => true;
        // Still through the wait and the last-moment re-check, then sliding faster than the pointer can chase so the
        // settle loop keeps steering: the stop lands inside it (~look 20+ is past the glide re-aims too).
        fin.Where = n => { if (n >= 25) stop = true; return (n < 4 ? 760 : 760 + (n - 3) * 30, 380); };
        var ex = new StepExecutor(inner, fin, clock, () => stop, log, readCursor: () => (0, 0));
        ex.Send(new MacroEvent(0, EventKind.TargetClick, Target: "abc", Delta: 2000,
            Step: new StepOptions { RequireStable = false, Retries = 0, Move = MoveMode.Glide }));
        Assert.DoesNotContain(inner.Sent, s => s.E.Kind == EventKind.ButtonDown);
        Assert.DoesNotContain(log.Snapshot(), l => l.Result == LogResult.Ok);
    }

    /// <summary>Out of memory is not "the connected macro could not run": it must end the run like any other serious failure.</summary>
    [Fact]
    public void An_out_of_memory_while_starting_a_connected_macro_ends_the_run()
    {
        var r = new Rig();
        r.Scr.Vis["pop"] = () => true;
        r.Lib["m"] = [Mark(0, 5)];
        r.Chain = (_, _) => throw new OutOfMemoryException();
        var p = r.Play([When(0, "pop", new FlowOptions { Macro = "m", WaitMs = 50 })]);
        Assert.IsType<OutOfMemoryException>(p.Error);
    }
}
