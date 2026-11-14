using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Storage;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>The adaptive steps against a scripted screen and a simulated clock.</summary>
public class AdaptiveTests
{
    /// <summary>Target "abc" is visible at Where(poll) after VisibleAfter polls (or whenever VisibleWhen says so). Every look costs 15 ms of simulated time.</summary>
    sealed class FakeFinder(FakeClock clock) : ITargetFinder
    {
        public bool Known = true;
        public double Threshold = 0.85;
        public int VisibleAfter;                                       // looks before it shows up; int.MaxValue = never
        public Func<int, (int X, int Y)> Where = _ => (800, 400);
        public Func<int, double> ScoreAt = _ => 0.97;
        public Func<int, bool>? VisibleWhen;
        public int Polls;
        public Dictionary<string, Func<int, bool>> Others = new();     // other targets: visible at look n?

        public string? NameOf(string id) => Known ? (id == "abc" ? "Play" : id) : null;
        public double ThresholdOf(string id) => Threshold;

        public Sighting? Find(string id)
        {
            clock.Now += 15;
            if (id != "abc") return Others.TryGetValue(id, out var v) && v(Polls) ? Sight((10, 10)) : null;
            Polls++;
            bool visible = VisibleWhen?.Invoke(Polls) ?? Polls > VisibleAfter;
            return visible ? Sight(Where(Polls)) : null;
        }

        Sighting Sight((int X, int Y) p) => new(new Detection(new Rect(p.X - 40, p.Y - 20, 80, 40), ScoreAt(Polls), 1), Polls, DateTime.UtcNow);
    }

    static MacroEvent Smart(StepOptions? o = null, int dx = 0, int dy = 0, int timeout = 1000, bool skip = false) =>
        new(0, EventKind.TargetClick, X: dx, Y: dy, Delta: timeout, Extended: skip, Target: "abc", Button: MouseBtn.Right, Step: o);

    static (StepExecutor Ex, RecordingSink Out, FakeFinder Fin, ExecutionLog Log, FakeClock Clock) Rig(bool fallbackSafe = true, Func<bool>? stop = null)
    {
        var clock = new FakeClock(); var inner = new RecordingSink(clock); var fin = new FakeFinder(clock); var log = new ExecutionLog();
        return (new StepExecutor(inner, fin, clock, stop ?? (() => false), log, () => 7, () => fallbackSafe), inner, fin, log, clock);
    }

    [Fact]
    public void Step_options_round_trip_in_the_file()
    {
        var o = new StepOptions { RelX = 0.5, RelY = 0.5, MinConfidence = 0.9, Retries = 4, OnFail = FailAction.UseRecordedPosition, FallbackX = 1200, FallbackY = 700, Verify = VerifyMode.TargetGone, RecoveryKey = 0x1B };
        var m = new Macro { Events = [Smart(o, 3, -2, 2500), new MacroEvent(0, EventKind.WaitGone, Target: "abc", Delta: 4000)] };
        var back = MacroSerializer.Deserialize(MacroSerializer.Serialize(m));
        Assert.Equal(o, back.Events[0].Step);
        Assert.Equal((EventKind.WaitGone, "abc", 4000), (back.Events[1].Kind, back.Events[1].Target, back.Events[1].Delta));
    }

    [Fact]
    public void Click_lands_at_the_recorded_corner_of_the_target_wherever_it_is_now()
    {
        // originally clicked the bottom-right corner of the button
        var (rx, ry) = ClickPoint.FromRecorded(new ScreenRect(100, 200, 80, 40), 179, 239);
        Assert.Equal(0.4875, rx, 4); Assert.Equal(0.475, ry, 4);        // 39 px and 19 px from the centre of an 80x40 button

        var r = Rig();
        r.Fin.Where = _ => (1500, 900);                                // the button has moved: centre (1500,900), 80x40
        r.Ex.Send(Smart(new StepOptions { RelX = rx, RelY = ry, RequireStable = false, Retries = 0 }));
        Assert.Equal(2, r.Out.Sent.Count);
        Assert.Equal((EventKind.ButtonDown, MouseBtn.Right), (r.Out.Sent[0].E.Kind, r.Out.Sent[0].E.Button));
        Assert.Equal((1500 + 39, 900 + 19), (r.Out.Sent[0].E.X, r.Out.Sent[0].E.Y));   // the same pixel of the button, at its new place
        Assert.Equal(EventKind.ButtonUp, r.Out.Sent[1].E.Kind);
        Assert.True(r.Out.Sent[1].At - r.Out.Sent[0].At >= 60);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Ok && x.X == 1539 && x.Index == 7);
    }

    [Fact]
    public void Relative_anchor_scales_with_the_target_and_pixel_offset_adds()
    {
        var hit = new Detection(new Rect(1000, 500, 160, 80), 1, 2.0);   // the same button shown twice as big
        Assert.Equal(new Point(1000 + 160, 500 + 80), ClickPoint.Compute(hit, new StepOptions { RelX = 0.5, RelY = 0.5 }));
        Assert.Equal(new Point(1000 + 5, 500 + 40 - 3), ClickPoint.Compute(hit, new StepOptions { RelX = -0.5 }, 5, -3));
        var (rx, ry) = ClickPoint.FromRecorded(new ScreenRect(0, 0, 100, 100), 5000, -5000);
        Assert.Equal((2.0, -2.0), (rx, ry));                              // a stray click is clamped
    }

    [Fact]
    public void A_flickering_false_match_is_not_clicked_but_a_stable_one_is()
    {
        var r = Rig();
        r.Fin.Where = n => (n % 2 == 0 ? 300 : 900, 400);              // jumps between two places on every look
        Assert.Throws<TargetNotFoundException>(() => r.Ex.Send(Smart(new StepOptions { Retries = 0 }, timeout: 600)));
        Assert.Empty(r.Out.Sent);

        var r2 = Rig();
        r2.Ex.Send(Smart(new StepOptions { Retries = 0 }));
        Assert.Equal(2, r2.Out.Sent.Count);
    }

    [Fact]
    public void Waits_for_the_target_then_retries_until_it_shows_and_logs_the_retry()
    {
        var r = Rig();
        r.Fin.VisibleAfter = 10;                                        // longer than one 300 ms attempt (a look costs ~55 ms)
        r.Ex.Send(Smart(new StepOptions { Retries = 3, RetryDelayMs = 100, RequireStable = false }, timeout: 300));
        Assert.Equal(2, r.Out.Sent.Count);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Retry);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Ok && x.Message.Contains("retr"));
    }

    [Fact]
    public void Minimum_confidence_blocks_weak_detections()
    {
        var r = Rig(); r.Fin.ScoreAt = _ => 0.80;
        var ex = Assert.Throws<TargetNotFoundException>(() => r.Ex.Send(Smart(new StepOptions { MinConfidence = 0.9, Retries = 0, RequireStable = false }, timeout: 300)));
        Assert.Contains("confidence", ex.Message);
        Assert.Empty(r.Out.Sent);
        var r2 = Rig(); r2.Fin.ScoreAt = _ => 0.95;
        r2.Ex.Send(Smart(new StepOptions { MinConfidence = 0.9, Retries = 0, RequireStable = false }));
        Assert.Equal(2, r2.Out.Sent.Count);
    }

    [Fact]
    public void Low_margin_clicks_are_warned_about()
    {
        var r = Rig(); r.Fin.ScoreAt = _ => 0.86; r.Fin.Threshold = 0.85;
        r.Ex.Send(Smart(new StepOptions { RequireStable = false, Retries = 0 }));
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Warn && x.Message.Contains("margin"));
    }

    [Fact]
    public void Fallback_uses_the_recorded_position_only_when_the_screen_layout_is_unchanged()
    {
        var o = new StepOptions { Retries = 0, RequireStable = false, OnFail = FailAction.UseRecordedPosition, FallbackX = 1200, FallbackY = 700 };
        var r = Rig(fallbackSafe: true); r.Fin.VisibleAfter = int.MaxValue;
        r.Ex.Send(Smart(o, timeout: 300));
        Assert.Equal((1200, 700), (r.Out.Sent[0].E.X, r.Out.Sent[0].E.Y));
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Fallback);

        var other = Rig(fallbackSafe: false); other.Fin.VisibleAfter = int.MaxValue;
        var ex = Assert.Throws<StepFailedException>(() => other.Ex.Send(Smart(o, timeout: 300)));
        Assert.Contains("layout", ex.Message);
        Assert.Empty(other.Out.Sent);                                    // never click recorded coordinates on a different layout
    }

    [Fact]
    public void Skip_and_stop_policies_and_legacy_steps()
    {
        var r = Rig(); r.Fin.VisibleAfter = int.MaxValue;
        r.Ex.Send(Smart(new StepOptions { Retries = 0, OnFail = FailAction.Skip }, timeout: 200));
        Assert.Empty(r.Out.Sent);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Skipped);
        r.Ex.Send(Smart(null, timeout: 200, skip: true));                // legacy "skip if missing"
        var ex = Assert.Throws<TargetNotFoundException>(() => r.Ex.Send(Smart(null, timeout: 200)));
        Assert.Contains("'Play'", ex.Message);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Failed);
    }

    [Fact]
    public void Verification_runs_recovery_and_retries_until_the_click_works()
    {
        // the first click is swallowed by a popup so the target stays visible; Esc closes the popup and the retry works
        var r = Rig();
        bool popup = true;
        r.Fin.VisibleWhen = _ => popup || r.Out.Sent.Count(s => s.E.Kind == EventKind.ButtonDown) < 2;
        r.Out.OnSend = _ => { if (r.Out.Sent.Any(s => s.E.Kind == EventKind.KeyUp && s.E.Vk == 0x1B)) popup = false; };
        r.Ex.Send(Smart(new StepOptions { Retries = 2, RetryDelayMs = 50, RequireStable = false, Verify = VerifyMode.TargetGone, VerifyTimeoutMs = 400, RecoveryKey = 0x1B }));
        Assert.Contains(r.Out.Sent, s => s.E.Kind == EventKind.KeyDown && s.E.Vk == 0x1B);          // the recovery action ran
        Assert.Equal(2, r.Out.Sent.Count(s => s.E.Kind == EventKind.ButtonDown));                    // the second attempt clicked again
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Retry && x.Message.Contains("still visible"));
    }

    [Fact]
    public void Verification_that_another_target_appears()
    {
        var r = Rig();
        r.Fin.Others["next"] = n => n >= 1;
        r.Ex.Send(Smart(new StepOptions { Retries = 0, RequireStable = false, Verify = VerifyMode.OtherTargetAppears, VerifyTarget = "next" }));
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Ok);
        var never = Rig();
        Assert.Throws<TargetNotFoundException>(() => never.Ex.Send(Smart(new StepOptions { Retries = 0, RequireStable = false, Verify = VerifyMode.OtherTargetAppears, VerifyTarget = "next", VerifyTimeoutMs = 200 })));
    }

    [Fact]
    public void Repeat_clicks_on_the_same_spot_are_prevented()
    {
        var r = Rig();
        var o = new StepOptions { Retries = 0, RequireStable = false, MinRepeatMs = 5000 };
        r.Ex.Send(Smart(o));
        r.Ex.Send(Smart(o));                                             // immediately again: blocked
        Assert.Equal(2, r.Out.Sent.Count);
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Skipped && x.Message.Contains("repeat"));
    }

    [Fact]
    public void Wait_visible_and_wait_gone_with_timeouts()
    {
        var r = Rig(); r.Fin.VisibleAfter = 5;
        r.Ex.Send(new MacroEvent(0, EventKind.WaitTarget, Delta: 2000, Target: "abc"));
        Assert.Contains(r.Log.Snapshot(), x => x.Result == LogResult.Ok && x.Message == "visible");

        var gone = Rig(); gone.Fin.VisibleWhen = n => n < 6;             // disappears after a few looks
        gone.Ex.Send(new MacroEvent(0, EventKind.WaitGone, Delta: 2000, Target: "abc"));
        Assert.Contains(gone.Log.Snapshot(), x => x.Result == LogResult.Ok && x.Message == "gone");

        var flicker = Rig(); flicker.Fin.VisibleWhen = n => n % 3 != 0;   // one missed frame is not a disappearance
        Assert.Throws<TargetNotFoundException>(() => flicker.Ex.Send(new MacroEvent(0, EventKind.WaitGone, Delta: 600, Target: "abc")));

        var never = Rig(); never.Fin.VisibleAfter = int.MaxValue;
        Assert.Throws<TargetNotFoundException>(() => never.Ex.Send(new MacroEvent(0, EventKind.WaitTarget, Delta: 300, Target: "abc")));
        never.Ex.Send(new MacroEvent(0, EventKind.WaitTarget, Delta: 300, Extended: true, Target: "abc")); // "then continue"
    }

    [Fact]
    public void Stop_aborts_waiting_and_unknown_targets_fail_clearly()
    {
        bool stop = false;
        var r = Rig(stop: () => stop);
        r.Fin.VisibleWhen = n => { if (n > 3) stop = true; return false; };
        r.Ex.Send(Smart(new StepOptions(), timeout: 60_000));
        Assert.Empty(r.Out.Sent);
        Assert.True(r.Clock.Now < 2000);

        var u = Rig(); u.Fin.Known = false;
        Assert.Throws<TargetNotFoundException>(() => u.Ex.Send(Smart()));
    }

    [Fact]
    public void Other_events_pass_straight_through()
    {
        var r = Rig();
        r.Ex.Send(Key(0, EventKind.KeyDown));
        Assert.Single(r.Out.Sent);
    }

    [Fact]
    public void Execution_log_is_a_bounded_ring()
    {
        var log = new ExecutionLog();
        for (int i = 0; i < ExecutionLog.Capacity * 3 + 5; i++) log.Add(i, "x", LogResult.Ok, "m" + i);
        var snap = log.Snapshot();
        Assert.Equal(ExecutionLog.Capacity, snap.Count);
        Assert.Equal(ExecutionLog.Capacity * 3 + 4, snap[^1].Index);
        Assert.Equal(ExecutionLog.Capacity * 3 + 5, log.Total);
        Assert.Equal(snap[^1].Index - ExecutionLog.Capacity + 1, snap[0].Index);
    }

    [Fact]
    public void Player_pauses_at_a_breakpoint_and_resumes()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock); var log = new ExecutionLog();
        var ev = Enumerable.Range(0, 4).Select(i => new MacroEvent(i * 100.0, EventKind.MouseMove, X: i)).ToArray();
        var p = new Player(sink, clock) { Log = log, BreakAt = i => i == 2 };
        p.Start(ev, 0, 4, 1);
        Assert.True(SpinWait.SpinUntil(() => p.IsPaused, 3000));
        Thread.Sleep(50);
        Assert.Equal(2, sink.Sent.Count);                                // stopped before event 2
        Assert.Contains(log.Snapshot(), x => x.What == "debugger");
        p.Resume();
        Assert.True(SpinWait.SpinUntil(() => !p.IsRunning, 3000));
        Assert.Equal(4, sink.Sent.Count);
    }
}
