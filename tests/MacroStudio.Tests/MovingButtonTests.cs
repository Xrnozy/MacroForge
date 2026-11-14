using System.IO;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>Full chain (watcher, finder, executor, trajectory sink) while the button is dragged around DURING playback: where does the click land?</summary>
public sealed class MovingButtonTests : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 3840, 1080), [new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080)]);

    sealed class Pointer : IInputSink
    {
        public readonly List<(string What, int X, int Y, Rect Button, double Ms)> Got = new();
        public (int X, int Y) Pos = (50, 50);
        public Func<Rect> Button = () => default;
        readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public void Send(in MacroEvent e)
        {
            lock (Got)
            {
                if (e.HasPos()) Pos = (e.X, e.Y);
                if (e.Kind is EventKind.ButtonDown or EventKind.MouseMove) Got.Add((e.Kind.ToString(), e.X, e.Y, Button(), _sw.ElapsedMilliseconds));
            }
        }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-mv-" + Guid.NewGuid().ToString("N"));
    readonly Mat _orig = Desktop.Make();
    readonly Mat _desk;
    readonly object _deskLock = new();
    readonly TargetStore _store;
    Rect _btn;                                                     // where the button is drawn right now

    public MovingButtonTests() { _store = new TargetStore(_dir); _desk = _orig.Clone(); }
    public void Dispose() { _desk.Dispose(); _orig.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    void Place(Mat img, Rect from, Rect to)
    {
        lock (_deskLock)
        {
            Cv2.Rectangle(_desk, from, new Scalar(90, 120, 60), -1);
            using var d = new Mat(_desk, to); img.CopyTo(d);
            _btn = to;
        }
    }

    [Theory]
    [InlineData(MoveMode.Optimized)]
    [InlineData(MoveMode.FollowPath)]
    [InlineData(MoveMode.Glide)]
    public void The_click_lands_on_the_button_even_while_it_is_being_dragged_around(MoveMode mode)
    {
        var (present, where, _) = Desktop.Targets(_orig);
        var tuner = new Detector();
        int k = Enumerable.Range(0, present.Count).OrderBy(i => tuner.AutoThreshold(_orig, where[i])).First();
        var was = where[k];
        var t = _store.Add("Btn", present[k], 96, Info.Virtual, Math.Round(tuner.AutoThreshold(_orig, where[k]), 2), new ScreenRect(was.X, was.Y, was.Width, was.Height));
        using var img = _store.Image(t.Id).Clone();
        _btn = was;
        var (rx, ry) = ClickPoint.FromRecorded(new ScreenRect(was.X, was.Y, was.Width, was.Height), was.X + was.Width / 2, was.Y + was.Height / 2);

        // recorded: a long wander, then the click in the middle of the button
        var rec = new List<MacroEvent>();
        for (int i = 0; i < 40; i++)
        {
            double f = i / 39.0;
            rec.Add(new MacroEvent(i * 25, EventKind.MouseMove, X: (int)(50 + (was.X + was.Width / 2 - 50) * f + 120 * Math.Sin(f * 9)), Y: (int)(50 + (was.Y + was.Height / 2 - 50) * f + 120 * Math.Cos(f * 7))));
        }
        rec.Add(new MacroEvent(1100, EventKind.TargetClick, Button: MouseBtn.Left, Delta: 8000, Target: t.Id,
            Step: new StepOptions { RelX = rx, RelY = ry, Move = mode, FallbackX = was.X + was.Width / 2, FallbackY = was.Y + was.Height / 2, RequireStable = false }));
        var events = rec.ToArray();

        // the user drags the button around while the macro runs: every 40 ms it jumps along a path across both monitors, and stops after 2.5 s
        Place(img, was, new Rect(2300, 500, was.Width, was.Height));
        using var watcher = new TargetWatcher(_store, r => { lock (_deskLock) return new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(); }, Info, 96, threaded: true);
        using var finder = new ScreenTargetFinder(watcher, [t]);
        SpinWait.SpinUntil(() => finder.Find(t.Id) is not null, 8000);

        var sink = new Pointer { Button = () => _btn };
        var log = new ExecutionLog();
        Player? player = null;
        var exec = new StepExecutor(sink, finder, new RealClock(), () => player?.StopRequested == true, log, () => player?.CurrentIndex ?? -1, () => true, () => 1, () => true, () => sink.Pos);
        var chain = new TrajectorySink(exec, events, TrajectoryPlan.Build(events), finder, () => player?.CurrentIndex ?? -1, p => p, () => true, log);
        player = new Player(chain) { Log = log };

        var cts = new CancellationTokenSource();
        var mover = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cur = _btn;
            while (sw.ElapsedMilliseconds < 2500 && !cts.IsCancellationRequested)
            {
                double f = sw.ElapsedMilliseconds / 2500.0;
                var next = new Rect((int)(2300 - 1800 * f + 150 * Math.Sin(f * 12)), (int)(500 - 250 * f + 120 * Math.Cos(f * 10)), was.Width, was.Height);
                Place(img, cur, next); cur = next;
                Thread.Sleep(40);
            }
        });

        player.Start(events, 0, events.Length, 1);
        Assert.True(SpinWait.SpinUntil(() => !player.IsRunning, 25000), "playback did not finish");
        cts.Cancel(); mover.Wait();
        Assert.Null(player.Error);

        List<(string What, int X, int Y, Rect Button, double Ms)> got; lock (sink.Got) got = sink.Got.ToList();
        var down = Assert.Single(got, e => e.What == "ButtonDown");
        var c = new Point(down.Button.X + down.Button.Width / 2, down.Button.Y + down.Button.Height / 2);
        double err = Math.Sqrt(Math.Pow(down.X - c.X, 2) + Math.Pow(down.Y - c.Y, 2));
        var path = got.Where(e => e.What == "MouseMove").ToList();
        double len = 0; for (int i = 1; i < path.Count; i++) len += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
        double maxJump = 0; for (int i = 1; i < path.Count; i++) maxJump = Math.Max(maxJump, Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2)));
        Console.WriteLine($"[{mode}] click ({down.X},{down.Y}) button now {down.Button} centre ({c.X},{c.Y}) err {err:0.0}px inside={down.Button.Contains(down.X, down.Y)} moves={path.Count} pathLen={len:0} maxStep={maxJump:0} at {down.Ms:0}ms");
        foreach (var l in log.Snapshot().Where(x => x.Result is LogResult.Warn or LogResult.Failed)) Console.WriteLine("   log: " + l.Message);
        Assert.True(down.Button.Contains(down.X, down.Y), $"[{mode}] clicked ({down.X},{down.Y}) outside the button {down.Button}");
    }
}
