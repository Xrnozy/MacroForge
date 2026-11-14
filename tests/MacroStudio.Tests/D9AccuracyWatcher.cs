using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// D9: end-to-end accuracy + latency of the <see cref="TargetWatcher"/> scheduler on the synthetic two-monitor desktop.
/// Capture is injected from a Mat (no real screen), the clock is simulated (no sleeping): each Tick() runs real matching
/// work and the fake clock advances by the delay the scheduler asked for. Measures: ticks and REAL work-ms until a
/// planted target is reported, position error, the Ambiguous flag for look-alikes, and the watcher's own counters
/// (captures, samples, light/deep scans, duty). Lines are BENCH-D9-W prefixed and diffable across branches.
/// </summary>
public sealed class D9AccuracyWatcher : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 3840, 1080), [new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080)]);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-d9w-" + Guid.NewGuid().ToString("N"));
    readonly TargetStore _store;
    readonly Mat _desk;
    readonly Detector _tuner = new();
    DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public D9AccuracyWatcher() { _store = new TargetStore(_dir); _desk = D9Lab.Whole(); }
    public void Dispose() { _desk.Dispose(); D9Lab.Reset(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    TargetWatcher Watcher(IReadOnlyList<int>? dpis = null) =>
        new(_store, r => new Mat(_desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => _clock, monitorDpis: dpis);

    Target Reg(string name, Mat img, Rect? cap = null, double? thr = null, Mat? context = null, int pad = 0)
        => _store.Add(name, img, 96, Info.Virtual, thr ?? 0.85,
            cap is { } c ? new ScreenRect(c.X, c.Y, c.Width, c.Height) : null, context, pad);

    static bool Near(Detection? h, Rect r, int tol = 4) =>
        h is { } d && Math.Abs(d.Center.X - (r.X + r.Width / 2)) <= tol && Math.Abs(d.Center.Y - (r.Y + r.Height / 2)) <= tol;

    double Run(TargetWatcher w, int ticks, Func<bool>? until = null)
    {
        double d = 250;
        for (int i = 0; i < ticks; i++)
        {
            d = w.Tick();
            _clock = _clock.AddMilliseconds(Math.Max(1, d));
            if (until?.Invoke() == true) break;
        }
        return d;
    }

    /// <summary>Tick until <paramref name="ok"/> accepts this target's hit (or the budget runs out). Returns ticks used and real work-ms spent.</summary>
    (int Ticks, double WorkMs, double SimMs) Until(TargetWatcher w, string id, Func<Detection, bool> ok, int maxTicks = 90)
    {
        double work = 0; var t0 = _clock;
        for (int i = 0; i < maxTicks; i++)
        {
            var s = Stopwatch.StartNew();
            double d = w.Tick();
            work += s.Elapsed.TotalMilliseconds;
            _clock = _clock.AddMilliseconds(Math.Max(1, d));
            var h = w.Get(id)?.Hit;
            if (h is { } hit && ok(hit)) return (i + 1, work, (_clock - t0).TotalMilliseconds);
        }
        return (-1, work, (_clock - t0).TotalMilliseconds);
    }

    /// <summary>Tick until every wanted target is near its rect (first hit tick/work recorded per id).</summary>
    (int Found, double WorkMs) UntilAll(TargetWatcher w, Dictionary<string, (Rect R, int Tol)> want, Dictionary<string, (int Tick, double Ms)> first, int maxTicks = 90)
    {
        double work = 0; var got = new HashSet<string>();
        for (int i = 0; i < maxTicks && got.Count < want.Count; i++)
        {
            var s = Stopwatch.StartNew();
            double d = w.Tick();
            work += s.Elapsed.TotalMilliseconds;
            _clock = _clock.AddMilliseconds(Math.Max(1, d));
            foreach (var (id, (r, tol)) in want)
                if (!got.Contains(id) && Near(w.Get(id)?.Hit, r, tol)) { got.Add(id); first[id] = (i + 1, work); }
        }
        return (got.Count, work);
    }

    static void Line(string s) => Console.WriteLine("BENCH-D9-W " + s);
    static string Stats(TargetWatcher w)
    {
        FormattableString msg = $"caps={w.Stats.Captures} px={w.Stats.Pixels / 1e6:0.0}M samples={w.Stats.Samples} light={w.Stats.LightScans} deep={w.Stats.DeepScans} work={w.Stats.WorkMs:0.0}ms delay={w.Stats.DelayMs:0}ms duty={w.Stats.Duty:P1}";
        return FormattableString.Invariant(msg);
    }

    [Fact]
    public void Planted_targets_across_both_monitors_are_found_with_bounded_latency()
    {
        var play = D9Lab.Button("PLAY", new Scalar(40, 140, 230));
        var save = D9Lab.Button("SAVE", new Scalar(60, 160, 60));
        var icon = D9Lab.Icon(48, 7);
        var icon2 = D9Lab.Icon(48, 11);
        var ghost = D9Lab.Icon(48, 21);                              // registered, never planted

        var r1 = D9Lab.Paste(_desk, play, 320, 720);
        var r2 = D9Lab.Paste(_desk, icon, 1500, 220);
        var r3 = D9Lab.Paste(_desk, icon2, 2440, 780);                 // monitor 2
        var r4u = D9Lab.Paste(_desk, save, 830, 160);                  // registration appearance
        double thrSave = _tuner.AutoThreshold(_desk, r4u);
        var r4 = D9Lab.Paste(_desk, D9Lab.ScaleIt(save, 1.1), 830, 160); // what the screen actually shows (10% bigger)

        var targets = new List<Target>
        {
            Reg("play@m1", play, r1, _tuner.AutoThreshold(_desk, r1)),
            Reg("icon@m1", icon, r2, _tuner.AutoThreshold(_desk, r2)),
            Reg("icon@m2", icon2, r3, _tuner.AutoThreshold(_desk, r3)),
            Reg("save-110%@m1", save, r4, thrSave),
            Reg("ghost-icon", ghost, null, 0.85),
            Reg("ghost-button", D9Lab.Button("EXIT", new Scalar(140, 60, 140)), null, 0.85),
        };

        using var w = Watcher();
        using var h = w.Watch(targets);
        var want = new Dictionary<string, (Rect, int)>
        {
            [targets[0].Id] = (r1, 4), [targets[1].Id] = (r2, 4), [targets[2].Id] = (r3, 4), [targets[3].Id] = (r4, 6),
        };
        var first = new Dictionary<string, (int, double)>();
        var (found, work) = UntilAll(w, want, first);
        foreach (var t in targets)
        {
            var s = w.Get(t.Id);
            string hit = s?.Hit is { } d ? $"{d.Method}@({d.Rect.X},{d.Rect.Y}) s={d.Score:0.00}" : s?.Note ?? "no state";
            string lat = first.TryGetValue(t.Id, out var f) ? $"tick {f.Item1}, {f.Item2:0.0} ms work" : "not found";
            Line(FormattableString.Invariant($"{t.Name,-15} {hit,-34} {lat}"));
        }
        Line($"summary: {found}/4 present found in {work:0.0} ms of scheduler work; {Stats(w)}");
        Assert.Equal(4, found);
        foreach (var t in targets.Skip(4)) Assert.Null(w.Get(t.Id)?.Hit);   // absent targets must never report a hit
    }

    [Fact]
    public void Identical_twins_on_two_monitors_resolve_by_surroundings_and_flag_ambiguity_without_them()
    {
        var icon = D9Lab.Icon(64, 9);
        var a = new Rect(500, 300, 64, 64);          // monitor 1
        var b = new Rect(2600, 300, 64, 64);         // monitor 2, pixel-identical
        D9Lab.Paste(_desk, icon, a.X, a.Y);
        D9Lab.Paste(_desk, icon, b.X, b.Y);
        Cv2.Rectangle(_desk, new Rect(a.X - 22, a.Y - 10, 18, a.Height + 20), new Scalar(30, 30, 220), -1);   // red band beside A
        Cv2.Rectangle(_desk, new Rect(b.X + b.Width + 4, b.Y - 10, 18, b.Height + 20), new Scalar(40, 200, 40), -1); // green band beside B

        Mat Ctx(Rect r, int pad)
        {
            var c = new Rect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad);
            return new Mat(_desk, c).Clone();
        }
        var tA = Reg("twin-A-red-side", icon, a, 0.8, Ctx(a, 24), 24);
        var tB = Reg("twin-B-green-side", icon, b, 0.8, Ctx(b, 24), 24);
        var tP = Reg("twin-no-context", icon, a, 0.8);            // recorded on A, surroundings not saved

        using var w = Watcher();
        using var h = w.Watch([tA, tB, tP]);
        var (tickA, workA, simA) = Until(w, tA.Id, d => Near(d, a), 60);
        var (tickB, workB, simB) = Until(w, tB.Id, d => Near(d, b), 60);
        Run(w, 15);
        var pa = w.Get(tA.Id)?.Hit; var pb = w.Get(tB.Id)?.Hit; var ps = w.Get(tP.Id);
        Line(FormattableString.Invariant($"twin-A -> {(pa is { } x ? $"({x.Rect.X},{x.Rect.Y}) {x.Method} {x.Score:0.00}" : "miss")} tick {tickA}, sim {simA:0} ms"));
        Line(FormattableString.Invariant($"twin-B -> {(pb is { } y ? $"({y.Rect.X},{y.Rect.Y}) {y.Method} {y.Score:0.00}" : "miss")} tick {tickB}, sim {simB:0} ms"));
        Line($"no-context -> {(ps?.Hit is { } p ? $"({p.Rect.X},{p.Rect.Y}) {p.Method}" : "miss")} ambiguous={ps?.Ambiguous} note={ps?.Note}; {Stats(w)}");
        Assert.True(pa is { } ha && Near(ha, a), $"twin A resolved to {pa?.Rect}");
        Assert.True(pb is { } hb && Near(hb, b), $"twin B resolved to {pb?.Rect}");
        Assert.NotNull(ps?.Hit);
        Assert.True(ps!.Hit is { } pp && (Near(pp, a) || Near(pp, b)), $"no-context twin resolved to {ps.Hit?.Rect}");
        // two pixel-identical answers with nothing to tell them apart must be signalled, not silently guessed
        Assert.True(ps.Ambiguous, "identical twins without saved surroundings should be flagged Ambiguous (a silent guess clicks the wrong button half the time)");

        // Expect(): "the step was recorded over there" must flip the no-context twin to the other monitor's copy
        w.Expect(tP.Id, 2650, 340);
        var (tickE, _, _) = Until(w, tP.Id, d => Near(d, b), 40);
        var pe = w.Get(tP.Id)?.Hit;
        Line($"after Expect(2650,340) -> {(pe is { } e ? $"({e.Rect.X},{e.Rect.Y}) {e.Method}" : "miss")} in {tickE} ticks");
        Assert.True(pe is { } ee && Near(ee, b), $"Expect() did not move the answer to monitor 2: {pe?.Rect}");
    }

    [Fact]
    public void A_target_on_a_125_percent_monitor_is_found_at_that_monitors_scale()
    {
        var play = D9Lab.Button("PLAY", new Scalar(40, 140, 230));
        var ru = D9Lab.Paste(_desk, play, 2200, 600);                          // registration appearance @96dpi
        double thr = _tuner.AutoThreshold(_desk, ru);
        var shown = D9Lab.ScaleIt(play, 1.25);                                 // monitor 2 runs at 120dpi = 125%
        var r = D9Lab.Paste(_desk, shown, 2200, 600);                          // (covers the unscaled paste)
        var t = Reg("play@125dpi", play, r, thr);

        using var w = Watcher([96, 120]);
        using var h = w.Watch([t]);
        var (ticks, work, sim) = Until(w, t.Id, hh => Near(hh, r, 6), 60);
        var hit = w.Get(t.Id)?.Hit;
        Line(FormattableString.Invariant($"125% monitor target -> {(hit is { } hd ? $"({hd.Rect.X},{hd.Rect.Y}) {hd.Rect.Width}x{hd.Rect.Height} {hd.Method} scale={hd.Scale:0.00} s={hd.Score:0.00}" : "miss")} after {ticks} ticks / {work:0.0} ms work / {sim:0} ms sim; {Stats(w)}"));
        Assert.NotNull(hit);
        Assert.True(Near(hit, r, 6), $"hit at {hit?.Rect}, expected near {r}");
        Assert.InRange(hit!.Value.Scale, 1.1, 1.4);                            // found at monitor scale, not at 1.0
        Assert.True(Math.Abs(hit.Value.Rect.Width - r.Width) <= 4, $"rect {hit.Value.Rect} vs planted {r}");
    }

    [Fact]
    public void A_target_appearing_on_a_static_screen_is_noticed_quickly()
    {
        var play = D9Lab.Button("PLAY", new Scalar(40, 140, 230));
        var r = new Rect(700, 650, play.Width, play.Height);
        using var orig = new Mat(_desk, r).Clone();                            // pre-paste background
        D9Lab.Paste(_desk, play, r.X, r.Y);
        double thr = _tuner.AutoThreshold(_desk, r);
        using (var back = new Mat(_desk, r)) orig.CopyTo(back);                // target removed again: screen static

        var t = Reg("popup-button", play, r, thr);
        using var w = Watcher();
        using var h = w.Watch([t]);
        Run(w, 12);                                                            // static screen: scheduler backs off
        var t0 = _clock;
        D9Lab.Paste(_desk, play, r.X, r.Y);                                    // the button appears
        var (ticks, work, sim) = Until(w, t.Id, d => Near(d, r), 60);
        Line(FormattableString.Invariant($"appearing target: found after {ticks} ticks / {work:0.0} ms work / {sim:0} ms simulated (paste at {_clock - t0:g}); {Stats(w)}"));
        Assert.True(ticks > 0, "a newly appeared target on an otherwise static screen was never noticed");
    }

    /// <summary>
    /// The monitor "did it change?" check samples 1/8 of the pixels (nearest). A sub-16px change may sit between sample
    /// points: this case measures whether a tiny appearing target is noticed at all. If it is not, the miss is a real
    /// blind spot to fix, not a flaky test (the asserted behaviour is the correct one).
    /// </summary>
    [Fact]
    public void A_tiny_target_appearing_on_a_static_screen_is_still_noticed()
    {
        var tiny = D9Lab.TinyIcon(14, 5);
        var r = new Rect(1200, 320, tiny.Width, tiny.Height);
        using var orig = new Mat(_desk, r).Clone();
        D9Lab.Paste(_desk, tiny, r.X, r.Y);
        double thr = _tuner.AutoThreshold(_desk, r);
        using (var back = new Mat(_desk, r)) orig.CopyTo(back);

        var t = Reg("tiny-popup", tiny, r, thr);
        using var w = Watcher();
        using var h = w.Watch([t]);
        Run(w, 12);
        D9Lab.Paste(_desk, tiny, r.X, r.Y);
        var (ticks, work, sim) = Until(w, t.Id, d => Near(d, r, 6), 120);
        Line(FormattableString.Invariant($"tiny appearing target (14px): {(ticks > 0 ? $"found after {ticks} ticks / {work:0.0} ms work / {sim:0} ms sim" : "NEVER found - the 1/8 change sample misses sub-16px changes")}; {Stats(w)}"));
        Assert.True(ticks > 0, "a 14px target appearing on a static monitor was never noticed (monitor-version sampling blind spot)");
    }

    /// <summary>An occluded-after-found target keeps reporting (part voting), and the CPU/duty counters stay bounded.</summary>
    [Fact]
    public void Covered_after_tracking_stays_hit_and_the_scheduler_reports_its_cost()
    {
        var play = D9Lab.Button("PLAY", new Scalar(40, 140, 230));
        var r = D9Lab.Paste(_desk, play, 400, 700);
        var t = Reg("tracked-then-covered", play, r, _tuner.AutoThreshold(_desk, r));
        using var w = Watcher();
        using var h = w.Watch([t]);
        var (ticks, work, _) = Until(w, t.Id, hh => Near(hh, r), 60);
        Assert.True(ticks > 0, "never found");

        D9Lab.CoverRight(_desk, r, 0.35);                                       // popup covers the right third
        var (ticks2, work2, _) = Until(w, t.Id, hh => Near(hh, r, 6) && hh.Method is "patches" or "features" or "template", 60);
        var hit = w.Get(t.Id)?.Hit;
        Line(FormattableString.Invariant($"35%-covered after tracking -> {(hit is { } hd ? $"({hd.Rect.X},{hd.Rect.Y}) {hd.Method} v={hd.Visible:P0} s={hd.Score:0.00}" : "miss")}; {Stats(w)}"));
        Assert.True(hit is { } d2 && Near(d2, r, 6), $"covered target lost: {hit?.Rect} note={w.Get(t.Id)?.Note}");

        // measure the scheduler's own cost model: work vs chosen delay, searching vs settled
        double wsum = 0, dsum = 0;
        for (int i = 0; i < 30; i++)
        {
            var s = Stopwatch.StartNew(); double delay = w.Tick(); wsum += s.Elapsed.TotalMilliseconds; dsum += delay;
            _clock = _clock.AddMilliseconds(Math.Max(1, delay));
        }
        Line(FormattableString.Invariant($"settled covered-tracking duty={wsum / (wsum + dsum):P2} work/tick={wsum / 30:0.00}ms delay={dsum / 30:0}ms; {Stats(w)}"));
        Assert.True(wsum / (wsum + dsum) < 0.25, "covered tracked target should stay cheap");
    }

    /// <summary>Scheduler cost report over a searching phase and a settled phase (diffable counters, soft assertions).</summary>
    [Fact]
    public void Scheduler_cost_model_searching_vs_settled()
    {
        var play = D9Lab.Button("PLAY", new Scalar(40, 140, 230));
        var ghost = D9Lab.Icon(48, 21);
        var r = D9Lab.Paste(_desk, play, 500, 750);
        var tp = Reg("present", play, r, _tuner.AutoThreshold(_desk, r));
        var tg = Reg("ghost", ghost, null, 0.85);
        using var w = Watcher();
        using var h = w.Watch([tp, tg]);
        var (ticks, work, sim) = Until(w, tp.Id, hh => Near(hh, r), 60);
        Assert.True(ticks > 0);

        double DutyOf(int n)
        {
            double wsum = 0, dsum = 0;
            for (int i = 0; i < n; i++)
            {
                var s = Stopwatch.StartNew(); double delay = w.Tick(); wsum += s.Elapsed.TotalMilliseconds; dsum += delay;
                _clock = _clock.AddMilliseconds(Math.Max(1, delay));
            }
            return wsum / (wsum + dsum);
        }
        double searching = DutyOf(40);                                          // ghost still runs expensive stages
        long light = w.Stats.LightScans, deep = w.Stats.DeepScans, caps = w.Stats.Captures;
        long prev = -1;
        for (int i = 0; i < 40 && w.Stats.DeepScans != prev; i++) { prev = w.Stats.DeepScans; Run(w, 30); }  // let the ghost's deep cycle end
        double settled = DutyOf(30);
        Line(FormattableString.Invariant($"duty: searching={searching:P1} settled={settled:P2} | scans light={light} deep={deep} caps={caps}; {Stats(w)}"));
        Assert.True(settled < 0.05, $"a settled watcher should cost ~nothing, duty was {settled:P2}");
        Assert.True(searching > settled || searching < 0.5, $"searching duty {searching:P1} over the 50%-of-a-core budget");
    }
}
