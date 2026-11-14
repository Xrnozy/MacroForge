using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>Round 2 vision performance: a watcher tick does one small slice of deep work, and the target store reads each file once.</summary>
public sealed class PerfRound2VisionTests : IDisposable
{
    readonly ITestOutputHelper _o;
    readonly Mat _frame;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-pr2-" + Guid.NewGuid().ToString("N"));
    static readonly DesktopInfo One = new(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]);

    public PerfRound2VisionTests(ITestOutputHelper o)
    {
        _o = o;
        using var desk = Desktop.Make();
        _frame = new Mat(desk, new Rect(0, 0, 1920, 1080)).Clone();
    }

    public void Dispose() { _frame.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    static void Paste(Mat dst, Mat src, int x, int y) { using var d = new Mat(dst, new Rect(x, y, src.Width, src.Height)); src.CopyTo(d); }

    /// <summary>Ticks a non-threaded watcher (simulated clock) until <paramref name="done"/>; returns the slowest tick in ms.</summary>
    double Drive(Mat screen, Target t, int maxTicks, Func<WatchState?, bool>? done, out WatchState? state, TargetStore store)
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var w = new TargetWatcher(store, r => new Mat(screen, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), One, 96, threaded: false, clock: () => clock);
        using var h = w.Watch([t]);
        var ticks = new List<double>(); state = null;
        for (int i = 0; i < maxTicks; i++)
        {
            var sw = Stopwatch.StartNew(); double delay = w.Tick(); ticks.Add(sw.Elapsed.TotalMilliseconds);
            clock = clock.AddMilliseconds(Math.Max(delay, 3500));
            state = w.Get(t.Id);
            if (done != null && done(state)) break;
        }
        // the slowest tick may be a GC pause or a scheduler hiccup on a busy machine: judge the second slowest, and cap the slowest loosely
        ticks.Sort();
        Assert.True(ticks[^1] < 400, $"slowest tick {ticks[^1]:F0} ms");
        return ticks.Count > 1 ? ticks[^2] : ticks[^1];
    }

    // ---------- 1. watcher ticks are small ----------

    [Fact]
    public void Absent_flat_button_never_blocks_a_tick_for_long()
    {
        using var tpl = RealButtons.Decode(RealButtons.Modifiers);
        var store = new TargetStore(_dir);
        var t = store.Add("a", tpl, 96, One.Virtual, 0.85);
        double max = Drive(_frame, t, 60, null, out _, store);
        _o.WriteLine($"flat absent target: max tick {max:F0} ms");
        Assert.True(max < 170, $"{max:F0} ms");
    }

    [Fact]
    public void Absent_textured_icon_never_blocks_a_tick_for_long()
    {
        using var icon = RealIcon();                                 // a detailed icon of another screen: nothing here matches it
        if (icon == null) return;
        Assert.Empty(new Detector().Find(_frame, icon, 0.85));
        var screen = _frame;
        var store = new TargetStore(_dir);
        var t = store.Add("icon", icon, 96, One.Virtual, 0.85);
        double max = Drive(screen, t, 80, null, out var st, store);
        _o.WriteLine($"textured absent target: max tick {max:F0} ms");
        Assert.Null(st?.Hit);
        Assert.True(max < 170, $"{max:F0} ms");
    }

    // ---------- 1b. sliced stages still find what the one-shot search finds ----------

    void SameAsOneShot(Mat screen, Mat tpl, string method, int maxTicks = 80)
    {
        var expect = new Detector().Find(screen, tpl, 0.85);
        Assert.NotEmpty(expect);
        Assert.Equal(method, expect[0].Method);
        var store = new TargetStore(_dir);
        var t = store.Add("t", tpl, 96, One.Virtual, 0.85);
        double max = Drive(screen, t, maxTicks, s => s?.Hit != null, out var st, store);
        _o.WriteLine($"{method}: found after slices, max tick {max:F0} ms");
        Assert.NotNull(st?.Hit);
        Assert.Equal(method, st!.Hit!.Value.Method);
        Assert.InRange(st.Hit.Value.Rect.X, expect[0].Rect.X - 2, expect[0].Rect.X + 2);
        Assert.InRange(st.Hit.Value.Rect.Y, expect[0].Rect.Y - 2, expect[0].Rect.Y + 2);
    }

    [Fact]
    public void Covered_button_is_still_found_by_sliced_part_voting()
    {
        using var tpl = RealButtons.Decode(RealButtons.Retry);
        using var screen = _frame.Clone();
        Paste(screen, tpl, 700, 500);
        Cv2.Rectangle(screen, new Rect(700 + 80, 500, 48, 24), new Scalar(20, 20, 20), -1);
        SameAsOneShot(screen, tpl, "patches");
    }

    [Fact]
    public void Recoloured_button_is_still_found_by_sliced_shape_stage()
    {
        using var tpl = RealButtons.Decode(RealButtons.Retry);
        using var screen = _frame.Clone();
        using var grey = new Mat(); Cv2.CvtColor(tpl, grey, ColorConversionCodes.BGR2GRAY); Cv2.CvtColor(grey, grey, ColorConversionCodes.GRAY2BGR); // greyed out
        Paste(screen, grey, 700, 500);
        SameAsOneShot(screen, tpl, "shape");
    }

    /// <summary>The "Broken Drive" icon of the real game screenshot at its own size (colourful and detailed), or null without the screenshot.</summary>
    static Mat? RealIcon()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Revolution", "logs", "debug_images", "inventory_detect.png");
            if (!File.Exists(p)) continue;
            using var real = Cv2.ImDecode(File.ReadAllBytes(p), ImreadModes.Color);
            return new Mat(real, new Rect(8, 455, 76, 76)).Clone();
        }
        return null;
    }

    [Fact]
    public void Rotated_covered_icon_is_still_found_by_sliced_feature_stage()
    {
        using var tpl = RealIcon();
        if (tpl == null) return;
        using var screen = _frame.Clone();
        using var rot = Cv2.GetRotationMatrix2D(new Point2f(38, 38), 12, 1.2);
        rot.Set(0, 2, rot.At<double>(0, 2) + 30); rot.Set(1, 2, rot.At<double>(1, 2) + 30);
        using var warped = new Mat();
        Cv2.WarpAffine(tpl, warped, rot, new Size(137, 137), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255));
        Paste(screen, warped, 1200, 300);
        Cv2.Rectangle(screen, new Rect(1200 + 98, 300 + 20, 38, 96), new Scalar(10, 10, 10), -1);
        SameAsOneShot(screen, tpl, "features");
    }

    // ---------- 3. small targets: what is still found without AKAZE ----------

    [Fact]
    public void Small_targets_without_feature_matching_document_what_is_found()
    {
        using var big = RealIcon();
        if (big == null) return;
        using var tpl = new Mat(); Cv2.Resize(big, tpl, new Size(28, 28), 0, 0, InterpolationFlags.Area);   // short side 28 px: below the feature matcher's 32 px floor
        using var blank = _frame.Clone();
        var det = new Detector();

        using (var s = blank.Clone()) { Paste(s, tpl, 900, 600); Assert.NotEmpty(det.Find(s, tpl, 0.85)); }   // as recorded

        using (var s = blank.Clone())                                                                           // 14 % larger
        {
            using var bigger = new Mat(); Cv2.Resize(tpl, bigger, new Size(32, 32), 0, 0, InterpolationFlags.Linear);
            Paste(s, bigger, 900, 600);
            var r = det.Find(s, tpl, 0.85);
            _o.WriteLine("resized 1.14x: " + (r.Count == 0 ? "not found" : r[0].Method));
            Assert.NotEmpty(r);
        }

        foreach (var deg in new[] { 15, 40 })                                                                    // rotated: only feature matching could cope with a lot of it
        {
            using var s = blank.Clone();
            using var rot = Cv2.GetRotationMatrix2D(new Point2f(14, 14), deg, 1.0);
            rot.Set(0, 2, rot.At<double>(0, 2) + 10); rot.Set(1, 2, rot.At<double>(1, 2) + 10);
            using var warped = new Mat(); Cv2.WarpAffine(tpl, warped, rot, new Size(48, 48), InterpolationFlags.Linear, BorderTypes.Replicate);
            Paste(s, warped, 900, 600);
            var near = det.Find(s, tpl, 0.85).Where(d => Math.Abs(d.Center.X - 924) < 20 && Math.Abs(d.Center.Y - 624) < 20).ToList();
            _o.WriteLine($"rotated {deg} deg: " + (near.Count == 0 ? "not found" : near[0].Method));
            Assert.DoesNotContain(near, d => d.Method == "features");     // below 32 px there is no feature matching
            if (deg == 40) Assert.Empty(near);                              // documented limitation: a strongly rotated small target is not found
        }
    }

    // ---------- 2. target store caches ----------

    TargetStore Seeded(int n, out List<string> ids)
    {
        var s = new TargetStore(_dir);
        using var img = RealButtons.Decode(RealButtons.Retry);
        ids = Enumerable.Range(0, n).Select(i => s.Add("t" + i, img, 96, One.Virtual, 0.85).Id).ToList();
        return s;
    }

    [Fact]
    public void Many_gets_read_each_target_file_at_most_once()
    {
        Seeded(20, out var ids);
        var cold = new TargetStore(_dir);                            // a second instance: nothing cached yet
        Assert.NotNull(cold.Get(ids[0]));
        var locks = Directory.GetFiles(_dir, "*.json").Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 500; i++) Assert.NotNull(cold.Get(ids[i % ids.Count]));   // any re-read of a locked file would fail
            Assert.Equal(20, cold.List().Count);
            _o.WriteLine($"500 Get + List over locked files: {sw.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(sw.Elapsed.TotalMilliseconds < 100, $"{sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally { foreach (var l in locks) l.Dispose(); }
    }

    [Fact]
    public void Edits_through_the_store_are_visible_immediately()
    {
        var s = Seeded(3, out var ids);
        Assert.NotNull(s.Get(ids[0]));
        var t = s.Get(ids[0])!; t.Threshold = 0.5; t.Owner = "rec1"; s.Save(t);
        Assert.Equal(0.5, s.Get(ids[0])!.Threshold);
        Assert.Equal("rec1", s.Get(ids[0])!.Owner);
        var c = s.Clone(ids[0], "rec2");
        Assert.Equal("rec2", s.Get(c.Id)!.Owner);
        Assert.Equal(4, s.List().Count);
        s.Delete(ids[1]);
        Assert.Null(s.Get(ids[1]));
        Assert.Equal(3, s.List().Count);
        var again = s.Get(ids[0])!; again.Name = "mutated"; // a caller's edit must not leak into the cache before Save
        Assert.Equal("t0", s.Get(ids[0])!.Name);
    }

    [Fact]
    public void Changes_made_by_another_instance_show_up()
    {
        var a = Seeded(2, out var ids);
        var b = new TargetStore(_dir);
        Assert.Equal(2, a.List().Count);
        var t = b.Get(ids[0])!; t.Threshold = 0.61; b.Save(t);
        using var img = RealButtons.Decode(RealButtons.Modifiers);
        var added = b.Add("new", img, 96, One.Virtual, 0.9);
        b.Delete(ids[1]);
        bool ok = false;
        for (int i = 0; i < 40 && !ok; i++)
        {
            ok = a.Get(ids[0])?.Threshold == 0.61 && a.Get(added.Id) != null && a.Get(ids[1]) == null && a.List().Count == 2;
            if (!ok) Thread.Sleep(100);
        }
        Assert.True(ok, "the first instance never saw the other one's edit, add and delete");
    }
}
