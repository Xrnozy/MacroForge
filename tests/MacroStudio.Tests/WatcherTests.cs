using System.Diagnostics;
using System.IO;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>The scheduler on a synthetic two-monitor desktop with a simulated clock: no sleeping, no real screen.</summary>
public sealed class WatcherTests : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 3840, 1080), [new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080)]);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-watch-" + Guid.NewGuid().ToString("N"));
    readonly TargetStore _store;
    readonly Mat _orig, _desk;
    readonly List<Target> _present = new(), _absent = new();
    readonly List<Rect> _where;
    DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    double _lastDelay;

    public WatcherTests()
    {
        _store = new TargetStore(_dir);
        _orig = Desktop.Make();
        _desk = _orig.Clone();
        var (present, where, absent) = Desktop.Targets(_orig);
        _where = where;
        var tuner = new Detector();
        // thresholds the way the app sets them: between the best look-alike on the screen and a perfect match
        for (int i = 0; i < present.Count; i++) _present.Add(_store.Add("present" + i, present[i], 96, Info.Virtual, Math.Round(tuner.AutoThreshold(_orig, where[i]), 2)));
        for (int i = 0; i < absent.Count; i++) _absent.Add(_store.Add("absent" + i, absent[i], 96, Info.Virtual, 0.85));
    }

    public void Dispose() { _desk.Dispose(); _orig.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    TargetWatcher Watcher() => new(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => _clock);

    /// <summary>Step the scheduler, advancing the simulated clock by the delay it asked for.</summary>
    void Run(TargetWatcher w, int ticks, Func<bool>? until = null)
    {
        for (int i = 0; i < ticks; i++)
        {
            _lastDelay = w.Tick();
            _clock = _clock.AddMilliseconds(_lastDelay);
            if (until?.Invoke() == true) return;
        }
    }

    bool At(TargetWatcher w, Target t, Rect where, int tol = 1) =>
        w.Get(t.Id)?.Hit is { } h && Math.Abs(h.Rect.X - where.X) <= tol && Math.Abs(h.Rect.Y - where.Y) <= tol;

    bool AllPresentFound(TargetWatcher w) => _present.Select((t, i) => At(w, t, _where[i])).All(x => x);

    /// <summary>Wipe a place on the desktop (the target is gone from there).</summary>
    static void Blank(Mat desk, Rect r) => Cv2.Rectangle(desk, r, new Scalar(90, 120, 60), -1);

    /// <summary>The present target with the lowest threshold: nothing else on the desktop looks like it.</summary>
    int Lonely() => Enumerable.Range(0, _present.Count).OrderBy(i => _present[i].Threshold).First();
    static void Paste(Mat desk, Mat img, int x, int y) { using var d = new Mat(desk, new Rect(x, y, img.Width, img.Height)); img.CopyTo(d); }

    [Fact]
    public void Finds_every_present_target_on_both_monitors_and_leaves_absent_ones_alone()
    {
        Assert.Equal(4, _where.Count);
        using var w = Watcher();
        using var h = w.Watch(_present.Concat(_absent));
        Run(w, 40, () => AllPresentFound(w));
        Assert.True(AllPresentFound(w));
        foreach (var t in _absent) Assert.Null(w.Get(t.Id)?.Hit);
    }

    [Fact]
    public void A_static_screen_costs_almost_nothing_and_the_scheduler_backs_off()
    {
        using var w = Watcher();
        using var h = w.Watch(_present.Concat(_absent));
        Run(w, 40, () => AllPresentFound(w));
        long prev = -1;
        for (int i = 0; i < 60 && w.Stats.DeepScans != prev; i++) { prev = w.Stats.DeepScans; Run(w, 40); } // settle: the expensive cycle for absent targets runs to its end
        long light = w.Stats.LightScans, deep = w.Stats.DeepScans;
        var sw = Stopwatch.StartNew();
        Run(w, 20);
        double perTick = sw.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"WATCH static screen, 8 targets: {perTick:0.0} ms per tick, delay now {_lastDelay:0} ms, duty {w.Stats.Duty:P1}");
        Assert.Equal(light, w.Stats.LightScans);                        // nothing re-searched: pixels did not change
        Assert.Equal(deep, w.Stats.DeepScans);
        Assert.True(_lastDelay >= 400, $"expected back-off, delay {_lastDelay}");
        Assert.True(perTick < 40, $"{perTick} ms per idle tick");
        Assert.True(AllPresentFound(w));
    }

    [Fact]
    public void A_target_that_moves_across_monitors_is_found_again_at_once()
    {
        using var w = Watcher();
        using var h = w.Watch(_present);
        Run(w, 40, () => AllPresentFound(w));
        int k = Lonely();
        var from = _where[k];
        int nx = from.X < 1920 ? 2700 : 300, ny = 760;
        Blank(_desk, from);
        using (var img = _store.Image(_present[k].Id).Clone()) Paste(_desk, img, nx, ny);
        var to = new Rect(nx, ny, from.Width, from.Height);
        Run(w, 6, () => At(w, _present[k], to));
        Assert.True(At(w, _present[k], to), "target not re-found after moving to the other monitor");
    }

    [Fact]
    public void A_new_target_appearing_on_a_static_screen_is_noticed()
    {
        using var w = Watcher();
        using var h = w.Watch(_present.Concat(_absent));
        Run(w, 50);
        long light = w.Stats.LightScans;
        using (var img = _store.Image(_absent[0].Id).Clone()) Paste(_desk, img, 500, 900);
        Run(w, 20, () => At(w, _absent[0], new Rect(500, 900, 72, 56)));
        Assert.True(At(w, _absent[0], new Rect(500, 900, 72, 56)));
        Assert.True(w.Stats.LightScans > light);
    }

    [Fact]
    public void A_target_that_gets_covered_is_found_by_the_expensive_stages_and_then_tracked_cheaply()
    {
        using var w = Watcher();
        using var h = w.Watch(_present);
        Run(w, 40, () => AllPresentFound(w));
        int k = Lonely();
        var r = _where[k];
        Cv2.Rectangle(_desk, new Rect(r.X + r.Width * 62 / 100, r.Y, r.Width - r.Width * 62 / 100, r.Height), new Scalar(20, 20, 20), -1); // right 38% hidden
        Run(w, 120, () => w.Get(_present[k].Id)?.Hit?.Method is "patches" or "features");
        var hit = w.Get(_present[k].Id)?.Hit;
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Center.X, r.X + r.Width / 2 - 6, r.X + r.Width / 2 + 6);
        Assert.InRange(hit.Value.Center.Y, r.Y + r.Height / 2 - 6, r.Y + r.Height / 2 + 6);

        long scans = w.Stats.LightScans + w.Stats.DeepScans;
        Run(w, 15);
        Assert.NotNull(w.Get(_present[k].Id)?.Hit);                      // still tracked while covered
        Assert.Equal(scans, w.Stats.LightScans + w.Stats.DeepScans);     // by the cheap re-check, not by searching again
    }

    [Fact]
    public void Prioritising_a_target_speeds_the_scheduler_up()
    {
        using var w = Watcher();
        using var h = w.Watch(_present);
        Run(w, 40, () => AllPresentFound(w));
        Run(w, 10);
        double idle = _lastDelay;
        w.Prioritise(_present[0].Id);
        using (var img = _store.Image(_absent[0].Id).Clone()) Paste(_desk, img, 100, 100);   // something changes
        double boosted = w.Tick();
        Assert.True(w.Stats.Boosted);
        Assert.True(boosted < idle, $"boosted delay {boosted} should be below idle {idle}");
    }

    [Fact]
    public void Watching_is_reference_counted_and_releases_state()
    {
        using var w = Watcher();
        var a = w.Watch(_present.Take(1));
        var b = w.Watch(_present.Take(1));
        Run(w, 20, () => At(w, _present[0], _where[0]));
        Assert.NotNull(w.Get(_present[0].Id));
        a.Dispose();
        Assert.NotNull(w.Get(_present[0].Id));
        b.Dispose();
        Assert.Null(w.Get(_present[0].Id));
    }

    [Fact]
    public void Eight_targets_stay_within_the_cpu_budget_while_searching_and_are_nearly_free_when_settled()
    {
        using var w = Watcher();
        using var h = w.Watch(_present.Concat(_absent));
        var sw = Stopwatch.StartNew();
        Run(w, 40, () => AllPresentFound(w));
        Console.WriteLine($"WATCH 8 targets: all present targets found after {sw.Elapsed.TotalMilliseconds:0} ms of work (per-frame API: ~2900 ms for ONE tick)");

        // phase 1: absent targets still run their expensive stages. CPU share = work / (work + sleep)
        double work = 0, slept = 0;
        for (int i = 0; i < 60; i++)
        {
            var t = Stopwatch.StartNew(); double d = w.Tick(); work += t.Elapsed.TotalMilliseconds; slept += d; _clock = _clock.AddMilliseconds(d);
        }
        double searchingDuty = work / (work + slept);

        long prev = -1;
        for (int i = 0; i < 60 && w.Stats.DeepScans != prev; i++) { prev = w.Stats.DeepScans; Run(w, 40); }
        work = 0; slept = 0;
        for (int i = 0; i < 40; i++)
        {
            var t = Stopwatch.StartNew(); double d = w.Tick(); work += t.Elapsed.TotalMilliseconds; slept += d; _clock = _clock.AddMilliseconds(d);
        }
        double settledDuty = work / (work + slept);
        Console.WriteLine($"WATCH 8 targets: CPU share of one core, absent targets still being searched {searchingDuty:P1}, screen static {settledDuty:P2}");
        Assert.True(searchingDuty < 0.32, $"{searchingDuty:P1}");
        Assert.True(settledDuty < 0.03, $"{settledDuty:P2}");
    }

    [Fact]
    public void Macro_finder_reads_the_watchers_answer_in_real_time()
    {
        using var w = new TargetWatcher(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: true);
        using var finder = new ScreenTargetFinder(w, _present.Take(1).ToList());
        Assert.Equal("present0", finder.NameOf(_present[0].Id));
        Assert.Null(finder.NameOf("not-in-library"));

        Sighting? p = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000 && (p = finder.Find(_present[0].Id)) == null) Thread.Sleep(20);
        Console.WriteLine($"WATCH macro finder: target reported after {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(p);
        Assert.InRange(p!.Value.Center.X, _where[0].X + _where[0].Width / 2 - 1, _where[0].X + _where[0].Width / 2 + 1);
        Assert.InRange(p.Value.Center.Y, _where[0].Y + _where[0].Height / 2 - 1, _where[0].Y + _where[0].Height / 2 + 1);
        Assert.True(w.Stats.Boosted);                                   // waiting for a target switches the watcher to its fast mode

        // the target disappears: the finder must stop reporting it (a stale hit would click an empty spot)
        Cv2.Rectangle(_desk, _where[0], new Scalar(90, 120, 60), -1);
        sw.Restart();
        while (sw.ElapsedMilliseconds < 8000 && finder.Find(_present[0].Id) != null) Thread.Sleep(20);
        Assert.Null(finder.Find(_present[0].Id));
    }

    [Fact]
    public void History_confirmations_and_disappearance_are_tracked()
    {
        using var w = Watcher();
        using var h = w.Watch(_present);
        Run(w, 40, () => AllPresentFound(w));
        int k = Lonely();
        var id = _present[k].Id;
        long seq = w.Get(id)!.Seq;
        Run(w, 3);
        Assert.True(w.Get(id)!.Seq > seq, "each confirmation should be a new observation");
        Assert.Null(w.Get(id)!.LostUtc);
        Assert.Contains(w.History(id), x => !x.Lost && x.Rect.X == _where[k].X);

        Cv2.Rectangle(_desk, _where[k], new Scalar(90, 120, 60), -1);        // the target disappears
        Run(w, 6, () => w.Get(id)!.Hit == null);
        Assert.Null(w.Get(id)!.Hit);
        Assert.NotNull(w.Get(id)!.LostUtc);
        Assert.True(w.History(id)[^1].Lost);
        Assert.True(w.History(id).Count <= 32);
    }

    [Fact]
    public void Identical_looking_targets_are_told_apart_by_what_is_next_to_them()
    {
        using var button = _store.Image(_absent[0].Id).Clone();             // a distinctive 72x56 pattern
        var a = new Rect(400, 500, button.Width, button.Height);
        var b = new Rect(1200, 500, button.Width, button.Height);
        Paste(_desk, button, a.X, a.Y); Paste(_desk, button, b.X, b.Y);      // two pixel-identical buttons
        Cv2.Rectangle(_desk, new Rect(a.X - 24, a.Y - 24, 24, a.Height + 48), new Scalar(30, 30, 220), -1);   // a red band next to A
        Cv2.Rectangle(_desk, new Rect(b.X - 24, b.Y - 24, 24, b.Height + 48), new Scalar(40, 200, 40), -1);   // a green band next to B

        Target Register(string name, Rect r)
        {
            var ctx = new Rect(r.X - 24, r.Y - 24, r.Width + 48, r.Height + 48);
            using var context = new Mat(_desk, ctx).Clone();
            return _store.Add(name, button, 96, Info.Virtual, 0.85, new ScreenRect(r.X, r.Y, r.Width, r.Height), context, 24);
        }
        var tA = Register("button next to red", a);
        var tB = Register("button next to green", b);

        using var w = Watcher();
        using var h = w.Watch([tA, tB]);
        Run(w, 20, () => At(w, tA, a) && At(w, tB, b));
        Assert.True(At(w, tA, a), $"A resolved to {w.Get(tA.Id)?.Hit?.Rect}");
        Assert.True(At(w, tB, b), $"B resolved to {w.Get(tB.Id)?.Hit?.Rect}");

        // without saved surroundings the two cannot be distinguished: the same pattern resolves to one spot for both
        var plain = _store.Add("plain", button, 96, Info.Virtual, 0.85);
        using var h2 = w.Watch([plain]);
        Run(w, 20, () => w.Get(plain.Id)?.Hit != null);
        Assert.NotNull(w.Get(plain.Id)?.Hit);
    }
}
