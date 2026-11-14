using System.IO;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>Real watcher + real executor + real player + real clock, on a synthetic two-monitor desktop.</summary>
public sealed class EndToEndTests : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 3840, 1080), [new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080)]);

    sealed class Collect : IInputSink
    {
        public readonly List<MacroEvent> Got = new();
        public void Send(in MacroEvent e) { lock (Got) Got.Add(e); }
        public List<MacroEvent> Snapshot() { lock (Got) return Got.ToList(); }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-e2e-" + Guid.NewGuid().ToString("N"));
    readonly Mat _orig = Desktop.Make();
    readonly Mat _desk;
    readonly TargetStore _store;

    public EndToEndTests() { _store = new TargetStore(_dir); _desk = _orig.Clone(); }
    public void Dispose() { _desk.Dispose(); _orig.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    (Target T, Rect Where) Register()
    {
        var (present, where, _) = Desktop.Targets(_orig);
        var tuner = new Detector();
        int k = Enumerable.Range(0, present.Count).OrderBy(i => tuner.AutoThreshold(_orig, where[i])).First(); // one with no look-alike on screen
        var t = _store.Add("Play", present[k], 96, Info.Virtual, Math.Round(tuner.AutoThreshold(_orig, where[k]), 2), new ScreenRect(where[k].X, where[k].Y, where[k].Width, where[k].Height));
        return (t, where[k]);
    }

    static void Wipe(Mat desk, Rect r) => Cv2.Rectangle(desk, r, new Scalar(90, 120, 60), -1);
    static void Paste(Mat desk, Mat img, int x, int y) { using var d = new Mat(desk, new Rect(x, y, img.Width, img.Height)); img.CopyTo(d); }

    [Fact]
    public void A_recorded_corner_click_follows_the_button_to_its_new_place_and_the_rest_of_the_macro_continues()
    {
        var (t, was) = Register();

        // what the user did while recording: clicked the bottom-right corner of the button
        var (rx, ry) = ClickPoint.FromRecorded(new ScreenRect(was.X, was.Y, was.Width, was.Height), was.Right - 1, was.Bottom - 1);

        // ...and at playback time the button is somewhere else, on the other monitor
        Wipe(_desk, was);
        int nx = was.X < 1920 ? 2600 : 250, ny = 640;
        using (var img = _store.Image(t.Id).Clone()) Paste(_desk, img, nx, ny);

        var events = new[]
        {
            new MacroEvent(0, EventKind.MouseMove, X: 50, Y: 50),
            new MacroEvent(200, EventKind.TargetClick, Button: MouseBtn.Left, Delta: 8000, Target: t.Id, Step: new StepOptions { RelX = rx, RelY = ry, FallbackX = was.Right - 1, FallbackY = was.Bottom - 1 }),
            new MacroEvent(500, EventKind.KeyDown, 0x41, 0x1E),
            new MacroEvent(560, EventKind.KeyUp, 0x41, 0x1E),
        };

        using var watcher = new TargetWatcher(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: true);
        using var finder = new ScreenTargetFinder(watcher, [t]);
        var sink = new Collect(); var log = new ExecutionLog();
        Player? player = null;
        var exec = new StepExecutor(sink, finder, new RealClock(), () => player?.StopRequested == true, log, () => player?.CurrentIndex ?? -1, () => true);
        player = new Player(exec) { Log = log };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        player.Start(events, 0, events.Length, 1);
        Assert.True(SpinWait.SpinUntil(() => !player.IsRunning, 15000), "playback did not finish");
        var total = sw.ElapsedMilliseconds;
        Assert.Null(player.Error);

        var got = sink.Snapshot();
        var down = Assert.Single(got, e => e.Kind == EventKind.ButtonDown);
        var expected = ClickPoint.Compute(new Detection(new Rect(nx, ny, was.Width, was.Height), 1, 1), new StepOptions { RelX = rx, RelY = ry });
        Assert.InRange(down.X, expected.X - 1, expected.X + 1);                         // the same corner of the button, at its new position
        Assert.InRange(down.Y, expected.Y - 1, expected.Y + 1);
        Assert.NotEqual((was.Right - 1, was.Bottom - 1), (down.X, down.Y));             // and not where it used to be
        Assert.Contains(got, e => e.Kind == EventKind.ButtonUp);
        Assert.Contains(got, e => e.Kind == EventKind.KeyDown && e.Vk == 0x41);          // the macro went on after the adaptive step
        Assert.True(got.FindIndex(e => e.Kind == EventKind.ButtonDown) < got.FindIndex(e => e.Kind == EventKind.KeyDown));
        Assert.Contains(log.Snapshot(), x => x.Result == LogResult.Ok && x.Index == 1);
        Console.WriteLine($"E2E adaptive click: button moved ({was.X},{was.Y}) -> ({nx},{ny}); clicked ({down.X},{down.Y}); whole macro took {total} ms");
    }

    [Fact]
    public void When_the_button_never_appears_playback_stops_with_a_clear_error_and_nothing_is_clicked()
    {
        var (t, was) = Register();
        Wipe(_desk, was);                                                                 // the button is gone for good
        var events = new[] { new MacroEvent(0, EventKind.TargetClick, Delta: 600, Target: t.Id, Step: new StepOptions { Retries = 1, RetryDelayMs = 100 }), new MacroEvent(300, EventKind.KeyDown, 0x41) };

        using var watcher = new TargetWatcher(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: true);
        using var finder = new ScreenTargetFinder(watcher, [t]);
        var sink = new Collect(); var log = new ExecutionLog();
        Player? player = null;
        player = new Player(new StepExecutor(sink, finder, new RealClock(), () => player?.StopRequested == true, log, () => player?.CurrentIndex ?? -1, () => true)) { Log = log };
        player.Start(events, 0, events.Length, 1);
        Assert.True(SpinWait.SpinUntil(() => !player.IsRunning, 15000));
        Assert.NotNull(player.Error);
        Assert.Contains("not found", player.Error!.Message);
        Assert.DoesNotContain(sink.Snapshot(), e => e.Kind is EventKind.ButtonDown or EventKind.KeyDown);   // it neither clicked nor ran on blindly
        Assert.Contains(log.Snapshot(), x => x.Result == LogResult.Retry);
        Assert.Contains(log.Snapshot(), x => x.Result == LogResult.Failed);
    }

    [Fact]
    public void The_recorded_approach_is_bent_to_the_moved_button_in_real_time_and_the_recording_is_left_untouched()
    {
        var (t, was) = Register();
        var (rx, ry) = ClickPoint.FromRecorded(new ScreenRect(was.X, was.Y, was.Width, was.Height), was.Right - 1, was.Bottom - 1);

        // recorded: the pointer travels from (50,50) to the button's bottom-right corner over about a second, then clicks it
        var rec = new List<MacroEvent>();
        const int n = 25;
        for (int k = 0; k < n; k++)
        {
            double f = k / (double)(n - 1);
            rec.Add(new MacroEvent(k * 40, EventKind.MouseMove, X: (int)(50 + (was.Right - 1 - 50) * f), Y: (int)(50 + (was.Bottom - 1 - 50) * f)));
        }
        rec.Add(new MacroEvent(n * 40 + 60, EventKind.TargetClick, Button: MouseBtn.Left, Delta: 8000, Target: t.Id,
            Step: new StepOptions { RelX = rx, RelY = ry, Move = MoveMode.FollowPath, FallbackX = was.Right - 1, FallbackY = was.Bottom - 1 }));
        var events = rec.ToArray();
        var snapshot = events.ToArray();

        // at playback time the button is on the other monitor
        Wipe(_desk, was);
        int nx = was.X < 1920 ? 2400 : 300, ny = 600;
        using (var img = _store.Image(t.Id).Clone()) Paste(_desk, img, nx, ny);

        using var watcher = new TargetWatcher(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: true);
        using var finder = new ScreenTargetFinder(watcher, [t]);
        SpinWait.SpinUntil(() => finder.Find(t.Id) is not null, 8000);                       // let the watcher locate it before the macro starts
        var sink = new Collect(); var log = new ExecutionLog();
        Player? player = null;
        var exec = new StepExecutor(sink, finder, new RealClock(), () => player?.StopRequested == true, log, () => player?.CurrentIndex ?? -1, () => true, () => 1, () => true, () => (50, 50));
        var chain = new TrajectorySink(exec, events, TrajectoryPlan.Build(events), finder, () => player?.CurrentIndex ?? -1, p => p, () => true, log);
        player = new Player(chain) { Log = log };
        player.Start(events, 0, events.Length, 1);
        Assert.True(SpinWait.SpinUntil(() => !player.IsRunning, 20000), "playback did not finish");
        Assert.Null(player.Error);

        var got = sink.Snapshot();
        var down = Assert.Single(got, e => e.Kind == EventKind.ButtonDown);
        var expected = ClickPoint.Compute(new Detection(new Rect(nx, ny, was.Width, was.Height), 1, 1), new StepOptions { RelX = rx, RelY = ry });
        Assert.InRange(down.X, expected.X - 1, expected.X + 1);                             // the same corner of the button at its new place
        Assert.InRange(down.Y, expected.Y - 1, expected.Y + 1);
        var moves = got.Where(e => e.Kind == EventKind.MouseMove).ToList();
        Assert.Equal((events[0].X, events[0].Y), (moves[0].X, moves[0].Y));                 // the start of the path is the recorded start
        Assert.True(moves.Count >= n, "the whole recorded approach is replayed (plus any glide)");
        Assert.Contains(log.Snapshot(), x => x.What == "mouse path");
        Assert.Equal(snapshot, events);                                                     // the recording is exactly as it was
        Console.WriteLine($"E2E bent path: recorded corner ({was.Right - 1},{was.Bottom - 1}) -> clicked ({down.X},{down.Y}); {moves.Count} mouse steps");
    }
}
