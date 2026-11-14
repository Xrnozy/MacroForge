using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>O3: playback engine regressions — step mode, guards, shield, Discord capture, log ring.</summary>
public class O3PlaybackTests
{
    sealed class Cap : IInputSink { public readonly List<MacroEvent> Got = new(); public void Send(in MacroEvent e) => Got.Add(e); }
    static RawInput Move(int x, int y) => new(0, EventKind.MouseMove, 0, 0, false, x, y, default, 0);

    // ---- UserActivityDetector ----

    [Fact]
    public void A_move_ignored_over_our_own_window_does_not_lend_its_distance_to_the_next_gesture()
    {
        // Watching the app's own window is not a takeover — and the pointer leaving that window must not count the whole hop as one sweep.
        var d = new UserActivityDetector(new HashSet<int>(), graceMs: 0, minMovePx: 15, ignore: r => r.X is >= 100 and < 200);
        d.Reset(0);
        Assert.False(d.Feed(Move(10, 0), 2000));
        Assert.False(d.Feed(Move(150, 0), 2050));   // over the app's own window: ignored
        Assert.False(d.Feed(Move(500, 0), 2100));   // leaving it: the 390 px hop across the window is not user input either
        Assert.False(d.Feed(Move(510, 0), 2130));
        Assert.True(d.Feed(Move(545, 0), 2160));    // but a real sweep afterwards still pauses
    }

    // ---- WindowShield ----

    [Fact]
    public void A_pass_through_toggle_that_fails_is_retried_on_the_next_mouse_event()
    {
        var calls = new List<bool>(); var fail = true;
        var sink = new WindowShield(new Cap(), () => (0, 0, 100, 100), on => { if (fail) throw new InvalidOperationException("busy"); calls.Add(on); });
        Assert.Throws<InvalidOperationException>(() => sink.Send(new MacroEvent(0, EventKind.MouseMove, X: 50, Y: 50)));
        fail = false;
        sink.Send(new MacroEvent(0, EventKind.MouseMove, X: 60, Y: 60));
        Assert.Equal([true], calls);    // without the retry the window stays solid and swallows a click meant for the target
    }

    [Fact]
    public void The_shield_toggles_only_when_the_pointer_crosses_the_window_edge()
    {
        var calls = new List<bool>();
        var sink = new WindowShield(new Cap(), () => (0, 0, 100, 100), on => calls.Add(on));
        sink.Send(new MacroEvent(0, EventKind.MouseMove, X: 150, Y: 50));   // outside
        sink.Send(new MacroEvent(1, EventKind.MouseMove, X: 50, Y: 50));    // inside: pass-through on
        sink.Send(new MacroEvent(2, EventKind.ButtonDown, X: 50, Y: 50));   // still inside: no toggle
        sink.Send(new MacroEvent(3, EventKind.ButtonUp, X: 50, Y: 50));
        sink.Send(new MacroEvent(4, EventKind.MouseMove, X: 150, Y: 50));   // outside again: solid
        Assert.Equal([true, false], calls);
    }

    // ---- Player ----

    [Fact]
    public void A_finished_handler_that_throws_does_not_break_the_run_or_hide_its_error()
    {
        var p = new Player(new Cap(), new FakeClock());
        p.Finished += () => throw new InvalidOperationException("the window is already gone");
        p.Run([new MacroEvent(0, EventKind.KeyDown, 0x41), new MacroEvent(1, EventKind.KeyUp, 0x41)], 0, 2, 1);
        Assert.Null(p.Error);
    }

    [Fact]
    public void A_step_issued_just_before_resume_does_not_fire_two_events_at_the_next_step()
    {
        // A token counted while paused is spent on ONE event; a token left unconsumed by Resume must not linger for the next Step.
        var sink = new Cap();
        var p = new Player(sink);                                                   // the real clock makes the paused poll sleep ~10 ms
        var ev = Enumerable.Range(0, 10).Select(i => new MacroEvent(i * 60.0, EventKind.MouseMove, X: i, Y: 0)).ToArray();
        p.Start(ev, 0, ev.Length, 1, stepMode: true);
        p.Step();
        Assert.True(SpinWait.SpinUntil(() => sink.Got.Count >= 1, 3000), "the first step never fired");
        p.Step(); p.Resume();                                                       // lands inside the ~10 ms pause poll: any leftover token must be dropped
        Assert.True(SpinWait.SpinUntil(() => sink.Got.Count >= 2, 3000));
        p.Pause();
        Thread.Sleep(30);                                                           // let the pause land between events (they are 60 ms apart)
        int before = sink.Got.Count;
        p.Step();
        Thread.Sleep(80);                                                           // a stale token fires a second event within microseconds
        Assert.Equal(before + 1, sink.Got.Count);
        p.Resume();
        Assert.True(SpinWait.SpinUntil(() => !p.IsRunning, 3000));
    }

    // ---- DiscordSink ----

    [Fact]
    public void A_discord_step_with_a_screenshot_does_not_hold_up_playback()
    {
        // Capturing and JPEG-encoding the whole desktop takes tens of ms: on the playback thread every following event is late.
        var log = new ExecutionLog();
        var grabDone = new ManualResetEventSlim();
        var sink = new DiscordSink(new Cap(), () => "https://example.test/not-a-hook", log, () => 7,
            screenshot: () => { Thread.Sleep(300); grabDone.Set(); return [9]; });
        var sw = Stopwatch.StartNew();
        sink.Send(new MacroEvent(0, EventKind.Discord, Extended: true, Target: "halfway"));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 250, $"the step blocked {sw.ElapsedMilliseconds} ms on the screenshot");
        Assert.True(grabDone.Wait(3000), "the screenshot never ran");
        Assert.True(SpinWait.SpinUntil(() => log.Total > 0, 3000), "the result was never logged");
    }

    // ---- ExecutionLog ----

    [Fact]
    public void The_log_ring_stays_bounded_and_scoped_entries_carry_their_source()
    {
        var log = new ExecutionLog(); var view = log.Scoped("sub");
        for (int i = 0; i < ExecutionLog.Capacity + 50; i++) log.Add(i, "x", i % 3 == 0 ? LogResult.Failed : LogResult.Ok, "m");
        view.Add(1, "click", LogResult.Warn, "done");
        Assert.Equal(ExecutionLog.Capacity, log.Snapshot().Count);
        Assert.Equal(log.Total, view.Total);
        Assert.Equal(1, log.Count(LogResult.Warn));
        Assert.Contains(log.Snapshot(), x => x.What == "[sub] click");
    }
}
