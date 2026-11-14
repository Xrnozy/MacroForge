using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;
using MacroStudio.Storage;

namespace MacroStudio.Tests;

public class CoreTests
{
    internal static MacroEvent Key(double t, EventKind k, int vk = 0x41) => new(t, k, vk, 30);
    static MacroEvent Btn(double t, EventKind k, int x = 10, int y = 20) => new(t, k, X: x, Y: y);

    static Macro Sample() => new()
    {
        Name = "s",
        Desktop = new DesktopInfo(new ScreenRect(-1920, 0, 3840, 1080), [new ScreenRect(-1920, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1080)]),
        Events =
        [
            new(0, EventKind.MouseMove, X: 500, Y: 300),
            new(10.25, EventKind.MouseMove, X: 800, Y: 300),
            Btn(20, EventKind.ButtonDown, 800, 300),
            Btn(80, EventKind.ButtonUp, 800, 300),
            Key(100, EventKind.KeyDown), Key(150, EventKind.KeyUp),
            new(200, EventKind.Wheel, X: 800, Y: 300, Delta: -120),
            new(210, EventKind.HWheel, X: 800, Y: 300, Delta: 30), // hi-res fractional notch
            new(300, EventKind.ButtonDown, Button: MouseBtn.X2, X: -5, Y: 7), new(310, EventKind.ButtonUp, Button: MouseBtn.X2, X: -5, Y: 7),
        ],
    };

    static EditSession Session()
    {
        var s = new EditSession();
        s.Load(Sample(), null);
        return s;
    }

    [Fact]
    public void Serializer_round_trips_everything_and_stays_line_per_event()
    {
        var m = Sample();
        var json = MacroSerializer.Serialize(m);
        var back = MacroSerializer.Deserialize(json);
        Assert.Equal(m.Events.Count, back.Events.Count);
        for (int i = 0; i < m.Events.Count; i++) Assert.Equal(m.Events[i] with { Id = 0 }, back.Events[i] with { Id = 0 });
        Assert.Equal(m.Desktop!.Virtual, back.Desktop!.Virtual);
        Assert.Contains("\"kind\":\"Wheel\"", json);
        Assert.Contains("\"delta\":-120", json);
        Assert.Equal(back.Events.Select((_, i) => i + 1), back.Events.Select(e => e.Id));
    }

    [Fact]
    public void Serializer_rejects_newer_version()
    {
        Assert.Throws<NotSupportedException>(() => MacroSerializer.Deserialize("{\"version\":99,\"events\":[]}"));
    }

    [Fact]
    public void Analysis_pairs_and_flags_problems()
    {
        var ev = new[]
        {
            Key(0, EventKind.KeyDown, 0x10), Key(10, EventKind.KeyDown, 0x41), Key(20, EventKind.KeyUp, 0x10), Key(30, EventKind.KeyUp, 0x41),
            Key(40, EventKind.KeyUp, 0x42),     // up without down
            Key(50, EventKind.KeyDown, 0x43),   // never released
        };
        var a = Analysis.Of(ev, null);
        Assert.Equal(2, a.Partner[0]); Assert.Equal(3, a.Partner[1]);
        Assert.NotEqual(a.Row[0], a.Row[1]);            // overlapping keys get separate timeline rows
        Assert.Equal("Release without press", a.Issues[4]);
        Assert.Equal("Never released", a.Issues[5]);
    }

    [Fact]
    public void Analysis_flags_coordinates_outside_desktop()
    {
        var d = new DesktopInfo(new ScreenRect(0, 0, 100, 100), []);
        var a = Analysis.Of([new MacroEvent(0, EventKind.MouseMove, X: 500, Y: 5)], d);
        Assert.Contains(0, a.Issues.Keys);
    }

    [Fact]
    public void Coordinates_round_trip_on_negative_origin_desktop()
    {
        foreach (var (ox, w) in new[] { (0, 1920), (-1920, 3840), (-2560, 6400) })
            for (int x = ox; x < ox + w; x += 7)
                Assert.Equal(x, Coords.FromAbsolute(Coords.ToAbsolute(x, ox, w), ox, w));
    }

    [Fact]
    public void Delete_removes_both_halves_of_a_press_and_undo_restores()
    {
        var s = Session();
        int downId = s.Events[2].Id;
        s.Delete([downId]);
        Assert.Equal(8, s.Events.Length);
        s.Undo();
        Assert.Equal(10, s.Events.Length);
        s.Redo();
        Assert.Equal(8, s.Events.Length);
    }

    [Fact]
    public void SetDelay_ripples_later_events()
    {
        var s = Session();
        int id = s.Events[2].Id;                      // click at t=20, previous event t=10.25
        s.SetDelay(id, 100, ripple: true);
        Assert.Equal(110.25, s.Events[2].T, 6);
        Assert.Equal(170.25, s.Events[3].T, 6);       // release moved by the same amount
        s.SetDelay(s.Events[0].Id, 0, ripple: false);
        Assert.Equal(0, s.Events[0].T);
    }

    [Fact]
    public void Modify_resorts_and_keeps_ids_stable()
    {
        var s = Session();
        int id = s.Events[1].Id;
        s.Modify([id], e => e with { T = 95 });
        Assert.True(s.Events.Zip(s.Events.Skip(1)).All(p => p.First.T <= p.Second.T));
        Assert.Equal(id, s.Events.Single(e => e.T == 95).Id);
    }

    [Fact]
    public void Coalesced_drag_is_one_undo_step()
    {
        var s = Session();
        int id = s.Events[0].Id;
        for (int x = 1; x <= 20; x++) s.Modify([id], e => e with { X = 500 + x }, "drag");
        s.EndCoalesce();
        s.Undo();
        Assert.Equal(500, s.Events[0].X);
    }

    [Fact]
    public void Duplicate_and_paste_ripple_and_assign_new_ids()
    {
        var s = Session();
        int last = s.Events[^1].T == 310 ? s.Events[^1].Id : 0;
        var key = s.Events[4].Id;
        var ids = s.Duplicate([key]);
        Assert.Equal(2, ids.Count);
        Assert.Equal(12, s.Events.Length);
        Assert.Equal(12, s.Events.Select(e => e.Id).Distinct().Count());
        var text = s.CopyText([key]);
        var pasted = s.PasteText(text, 0);
        Assert.Equal(2, pasted.Count);
        Assert.Empty(s.PasteText("not json", 0));
        Assert.NotEqual(0, last);
    }

    [Fact]
    public void Move_swaps_order_but_keeps_time_slots()
    {
        var s = Session();
        var a = s.Events[4]; var b = s.Events[5];
        s.Move(a.Id, 1);
        Assert.Equal(b.Id, s.Events[4].Id);
        Assert.Equal(a.T, s.Events[4].T);
        Assert.Equal(a.Id, s.Events[5].Id);
        Assert.Equal(b.T, s.Events[5].T);
    }

    [Fact]
    public void PositionAt_interpolates()
    {
        var s = Session();
        var p = s.PositionAt(5.125)!.Value;
        Assert.Equal(650, p.X, 3);
    }

    // ---- adapting to another PC ----

    sealed class Capture : IInputSink
    {
        public List<MacroEvent> Got = new();
        public void Send(in MacroEvent e) => Got.Add(e);
    }

    static DesktopInfo Desk(params (int x, int y, int w, int h)[] m)
    {
        var mons = m.Select(r => new ScreenRect(r.x, r.y, r.w, r.h)).ToList();
        int minX = mons.Min(r => r.X), minY = mons.Min(r => r.Y);
        return new DesktopInfo(new ScreenRect(minX, minY, mons.Max(r => r.X + r.Width) - minX, mons.Max(r => r.Y + r.Height) - minY), mons);
    }

    [Fact]
    public void Adapter_is_identity_on_same_layout()
    {
        var d = Desk((0, 0, 1920, 1080));
        var cap = new Capture();
        var a = new AdaptingSink(cap, d, new EnvInfo(96, 3, 10), d, new EnvInfo(96, 3, 10));
        Assert.False(a.IsAdapting);
        a.Send(new MacroEvent(0, EventKind.MouseMove, X: 123, Y: 456));
        Assert.Equal((123, 456), (cap.Got[0].X, cap.Got[0].Y));
    }

    [Fact]
    public void Adapter_scales_resolution_per_monitor_and_keeps_corners()
    {
        var rec = Desk((0, 0, 1920, 1080), (1920, 0, 1920, 1080));
        var cur = Desk((0, 0, 2560, 1440), (2560, 0, 2560, 1440));
        var a = new AdaptingSink(new Capture(), rec, null, cur, new EnvInfo(144, 3, 10));
        Assert.Equal((0, 0), a.MapPoint(0, 0));
        Assert.Equal((2559, 1439), a.MapPoint(1919, 1079));            // far corner of monitor 1
        Assert.Equal((2560, 0), a.MapPoint(1920, 0));                  // origin of monitor 2
        Assert.Equal((5119, 1439), a.MapPoint(3839, 1079));            // far corner of monitor 2
        var mid = a.MapPoint(960, 540);
        Assert.InRange(mid.X, 1279, 1281);
    }

    [Fact]
    public void Adapter_falls_back_to_virtual_scaling_when_monitor_count_differs()
    {
        var rec = Desk((0, 0, 1920, 1080), (1920, 0, 1920, 1080));
        var cur = Desk((0, 0, 1920, 1080));
        var a = new AdaptingSink(new Capture(), rec, null, cur, new EnvInfo(96, 3, 10));
        Assert.Equal((1919, 1079), a.MapPoint(3839, 1079));
        Assert.Equal((0, 0), a.MapPoint(0, 0));
    }

    [Fact]
    public void Adapter_scales_wheel_by_scroll_lines_and_never_drops_a_step()
    {
        var d = Desk((0, 0, 1920, 1080));
        var cap = new Capture();
        var a = new AdaptingSink(cap, d, new EnvInfo(96, 6, 10), d, new EnvInfo(96, 3, 10)); // recorded at 6 lines, now 3: double the delta
        a.Send(new MacroEvent(0, EventKind.Wheel, X: 5, Y: 5, Delta: -120));
        Assert.Equal(-240, cap.Got[0].Delta);
        var cap2 = new Capture();
        var b = new AdaptingSink(cap2, d, new EnvInfo(96, 1, 10), d, new EnvInfo(96, 100, 10)); // 100 = invalid/page mode: left alone
        b.Send(new MacroEvent(0, EventKind.Wheel, X: 5, Y: 5, Delta: 120));
        Assert.Equal(120, cap2.Got[0].Delta);
        var cap3 = new Capture();
        var c = new AdaptingSink(cap3, d, new EnvInfo(96, 1, 10), d, new EnvInfo(96, 30, 10)); // would round to 0
        c.Send(new MacroEvent(0, EventKind.Wheel, X: 5, Y: 5, Delta: 10));
        Assert.Equal(1, cap3.Got[0].Delta);
    }

    [Fact]
    public void Env_round_trips_in_file()
    {
        var m = Sample(); m.Env = new EnvInfo(144, 5, 12);
        Assert.Equal(m.Env, MacroSerializer.Deserialize(MacroSerializer.Serialize(m)).Env);
    }

    // ---- playback ----

    internal sealed class FakeClock : IClock
    {
        public double Now;
        public double NowMs => Now;
        public void Sleep(double ms) => Now += Math.Max(ms, 0.01);
    }

    internal sealed class RecordingSink(FakeClock clock) : IInputSink
    {
        public List<(double At, MacroEvent E)> Sent = new();
        public Action<int>? OnSend;
        public void Send(in MacroEvent e) { Sent.Add((clock.Now, e)); OnSend?.Invoke(Sent.Count); }
    }

    [Fact]
    public void Player_fires_on_schedule_without_drift_and_scales_with_speed()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock);
        var ev = Enumerable.Range(0, 100).Select(i => new MacroEvent(i * 10.0, EventKind.MouseMove, X: i, Y: 0)).ToArray();
        var p = new Player(sink, clock) { Speed = 2 };
        p.Run(ev, 0, ev.Length, 1);
        Assert.Equal(100, sink.Sent.Count);
        Assert.InRange(sink.Sent[^1].At, 495, 496);   // 990ms of macro at 2x
    }

    [Fact]
    public void Player_stop_releases_held_keys_and_buttons()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock);
        var ev = new[] { Key(0, EventKind.KeyDown, 0x11), Btn(5, EventKind.ButtonDown), new MacroEvent(10, EventKind.MouseMove), Key(1000, EventKind.KeyUp, 0x11) };
        var p = new Player(sink, clock);
        sink.OnSend = n => { if (n == 3) p.Stop(); };
        p.Run(ev, 0, ev.Length, 1);
        Assert.Contains(sink.Sent, s => s.E.Kind == EventKind.KeyUp && s.E.Vk == 0x11);
        Assert.Contains(sink.Sent, s => s.E.Kind == EventKind.ButtonUp);
    }

    [Fact]
    public void Player_releases_when_sink_throws()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock);
        var ev = new[] { Key(0, EventKind.KeyDown), new MacroEvent(5, EventKind.MouseMove) };
        sink.OnSend = n => { if (n == 2) throw new InvalidOperationException("boom"); };
        var p = new Player(sink, clock);
        p.Run(ev, 0, ev.Length, 1);
        Assert.NotNull(p.Error);
        Assert.Contains(sink.Sent, s => s.E.Kind == EventKind.KeyUp);
    }

    [Fact]
    public void Player_pause_does_not_cause_catch_up_burst()
    {
        var clock = new FakeClock(); var sink = new RecordingSink(clock);
        var ev = Enumerable.Range(0, 10).Select(i => new MacroEvent(i * 100.0, EventKind.MouseMove)).ToArray();
        var p = new Player(sink, clock);
        int sleeps = 0;
        sink.OnSend = n => { if (n == 3) p.Pause(); };
        // resume after the player has been idle for a while, measured in fake time
        var resumer = new Thread(() => { while (clock.Now < 5000) Thread.Sleep(1); p.Resume(); });
        resumer.Start();
        p.Run(ev, 0, ev.Length, 1);
        resumer.Join();
        _ = sleeps;
        Assert.Equal(10, sink.Sent.Count);
        double gap = sink.Sent[3].At - sink.Sent[2].At;
        Assert.True(gap >= 5000 + 90, $"events after resume should keep their spacing, gap={gap}");
        Assert.InRange(sink.Sent[4].At - sink.Sent[3].At, 99, 101);
    }

    [Fact]
    public void Player_step_mode_fires_one_event_per_step_even_when_stepped_right_after_start()
    {
        // Regression: Start() used to reset the step counter inside the new thread, wiping a Step() issued immediately after it. Repeat to catch the race.
        for (int round = 0; round < 25; round++)
        {
            var clock = new FakeClock(); var sink = new RecordingSink(clock);
            var ev = Enumerable.Range(0, 3).Select(i => new MacroEvent(i * 100.0, EventKind.MouseMove)).ToArray();
            var p = new Player(sink, clock);
            p.Start(ev, 0, 3, 1, stepMode: true);
            for (int n = 1; n <= 3; n++)
            {
                p.Step();
                SpinWait.SpinUntil(() => sink.Sent.Count >= n, 2000);
                Assert.Equal(n, sink.Sent.Count);
            }
            Assert.True(SpinWait.SpinUntil(() => !p.IsRunning, 2000));
        }
    }
}
