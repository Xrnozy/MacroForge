using System.IO;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>D6: the scheduler itself — publish churn, deep-stage pacing when a target comes back, OpenCV thread scoping.</summary>
public sealed class D6WatcherTests : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 3840, 1080), [new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080)]);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-d6-" + Guid.NewGuid().ToString("N"));
    readonly TargetStore _store;
    readonly Mat _orig, _desk;
    readonly List<Target> _absent;
    DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public D6WatcherTests()
    {
        _store = new TargetStore(_dir);
        _orig = Desktop.Make();
        _desk = _orig.Clone();
        var (_, _, absent) = Desktop.Targets(_orig);
        _absent = absent.Select((m, i) => _store.Add("absent" + i, m, 96, Info.Virtual, 0.85)).ToList();
    }

    public void Dispose() { _desk.Dispose(); _orig.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    TargetWatcher Watcher() => new(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => _clock);

    /// <summary>One step of the scheduler; the simulated clock moves on by the delay it asked for. Returns that delay.</summary>
    double Tick(TargetWatcher w)
    {
        double d = w.Tick();
        _clock = _clock.AddMilliseconds(d);
        return d;
    }

    static void Paste(Mat desk, Mat img, int x, int y) { using var d = new Mat(desk, new Rect(x, y, img.Width, img.Height)); img.CopyTo(d); }

    /// <summary>A target that stays absent on a static screen reports the same state at every tick: republishing an identical record is churn.</summary>
    [Fact]
    public void Published_state_is_the_same_object_while_nothing_changes()
    {
        using var w = Watcher();
        using var h = w.Watch(_absent.Take(1));
        Tick(w);
        var first = w.Get(_absent[0].Id);
        Assert.NotNull(first);
        for (int i = 0; i < 4; i++) Tick(w);
        var again = w.Get(_absent[0].Id);
        Assert.Same(first, again);
    }

    /// <summary>The watcher runs its own work with few OpenCV threads, but must hand the previous setting back afterwards:
    /// other users of OpenCV (the rest of the app, other tests) must not run single-threaded because of it.</summary>
    [Fact]
    public void A_watcher_tick_leaves_the_processs_opencv_thread_count_as_it_found_it()
    {
        int prev = Cv2.GetNumThreads();
        try
        {
            Cv2.SetNumThreads(2);
            using var w = Watcher();
            using var h = w.Watch(_absent.Take(1));
            Tick(w);
            Assert.Equal(2, Cv2.GetNumThreads());
        }
        finally { Cv2.SetNumThreads(prev); }
    }

    /// <summary>
    /// The deep-stage spacing grows to 15 s while a target stays absent — a sensible CPU saving on a screen that does not change.
    /// But when the target then appears (covered, so only the expensive stages can find it), the grown gap must not delay them:
    /// fresh pixels deserve the initial spacing again.
    /// </summary>
    [Fact]
    public void A_target_that_reappears_partly_covered_after_a_long_absence_does_not_wait_for_the_grown_backoff()
    {
        var t = _absent[0];
        using var w = Watcher();
        using var h = w.Watch([t]);

        // let the expensive stages run the static screen to exhaustion: the spacing grows 3 s -> 15 s while nothing is found
        int guard = 0;
        while (w.Stats.DeepScans < Info.Monitors.Count * Detector.DeepStages && guard++ < 400) Tick(w);
        Assert.True(w.Stats.DeepScans >= Info.Monitors.Count * Detector.DeepStages, "deep stages never settled");

        // it appears with the right 38% hidden: the cheap search cannot match it, only the part-voting stage can
        var r = new Rect(500, 900, 72, 56);
        using (var img = _store.Image(t.Id).Clone()) Paste(_desk, img, r.X, r.Y);
        Cv2.Rectangle(_desk, new Rect(r.X + r.Width * 62 / 100, r.Y, r.Width - r.Width * 62 / 100, r.Height), new Scalar(20, 20, 20), -1);

        // the backoff grown on the old screen must not delay the deep stages on the NEW one (used to be up to 15 s per stage)
        long scans = w.Stats.DeepScans;
        double waited = 0;
        while (w.Stats.DeepScans == scans && waited < 30000) waited += Tick(w);
        Assert.True(waited < 8000, $"first deep stage on the new screen started after {waited:0} ms of grown backoff");

        double total = waited;
        while (w.Get(t.Id)?.Hit == null && total < 60000) total += Tick(w);
        Assert.NotNull(w.Get(t.Id)?.Hit);
        Assert.True(total < 20000, $"covered target found after {total:0} ms");
    }
}
