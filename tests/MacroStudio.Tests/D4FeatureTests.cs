using System.Diagnostics;
using System.IO;
using MacroStudio.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>
/// D4: locks and probes for the feature-matching stage (AKAZE keypoints + RANSAC). The correctness tests pin the
/// behaviours the cost work must not break (rotation/scale invariance, banded == one-shot results, cheap early exits,
/// per-frame keypoint reuse); the perf probes print the stage's share of a 1080p search.
/// </summary>
public class D4FeatureTests
{
    readonly ITestOutputHelper _o;
    public D4FeatureTests(ITestOutputHelper o) => _o = o;

    // ---------- fixtures ----------

    /// <summary>A colourful, detailed icon: the kind of textured target feature matching is for. Rich enough that AKAZE finds ~100+ keypoints.</summary>
    static Mat Icon(int w = 76, int h = 76, int seed = 7)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, new Scalar(35, 30, 30));
        var rng = new Random(seed);
        for (int i = 0; i < 60; i++)
            Cv2.Circle(m, new Point(rng.Next(w), rng.Next(h)), rng.Next(2, 9),
                new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), -1);
        for (int i = 0; i < 40; i++)
            Cv2.Line(m, new Point(rng.Next(w), rng.Next(h)), new Point(rng.Next(w), rng.Next(h)),
                new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), rng.Next(1, 3));
        for (int i = 0; i < 18; i++)   // little high-contrast squares: crisp corners are what AKAZE loves
            Cv2.Rectangle(m, new Rect(rng.Next(w - 8), rng.Next(h - 8), rng.Next(3, 9), rng.Next(3, 9)),
                new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), -1);
        Cv2.Rectangle(m, new Rect(0, 0, w - 1, h - 1), Scalar.All(255), 2);
        return m;
    }

    /// <summary>The unrelated, detailed scene the icon is hidden in (same recipe as VisionTests).</summary>
    static Mat Scene(int w, int h, int seed)
    {
        var rng = new Random(seed);
        using var small = new Mat(h / 8, w / 8, MatType.CV_8UC3);
        Cv2.Randu(small, Scalar.All(30), Scalar.All(200));
        var bg = new Mat();
        Cv2.Resize(small, bg, new Size(w, h), 0, 0, InterpolationFlags.Cubic);
        for (int i = 0; i < 60; i++)
            Cv2.Line(bg, new Point(rng.Next(w), rng.Next(h)), new Point(rng.Next(w), rng.Next(h)), new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), rng.Next(1, 4));
        for (int i = 0; i < 25; i++)
            Cv2.Circle(bg, new Point(rng.Next(w), rng.Next(h)), rng.Next(5, 30), new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), rng.Next(1, 3));
        return bg;
    }

    static void Paste(Mat frame, Mat img, int x, int y) { using var roi = new Mat(frame, new Rect(x, y, img.Width, img.Height)); img.CopyTo(roi); }

    static List<Detection> Drain(IEnumerable<List<Detection>?> steps)
    {
        List<Detection> last = [];
        foreach (var s in steps) if (s != null) last = s;
        return last;
    }

    static double MedianMs(Action a, int n = 5)
    {
        var l = new List<double>();
        for (int i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); a(); l.Add(sw.Elapsed.TotalMilliseconds); }
        l.Sort(); return l[n / 2];
    }

    /// <summary>Where the icon lands after rotate-by-<paramref name="deg"/>/scale-<paramref name="k"/> about its centre, translated so it stays on the canvas.</summary>
    static (Mat Warped, Point2d Center) Warp(Mat icon, double deg, double k)
    {
        using var rot = Cv2.GetRotationMatrix2D(new Point2f(icon.Width / 2f, icon.Height / 2f), deg, k);
        rot.Set(0, 2, rot.At<double>(0, 2) + icon.Width * 0.4);
        rot.Set(1, 2, rot.At<double>(1, 2) + icon.Height * 0.4);
        var warped = new Mat();
        Cv2.WarpAffine(icon, warped, rot, new Size((int)(icon.Width * 1.8), (int)(icon.Height * 1.8)), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255));
        var c = new Point2d(rot.At<double>(0, 0) * icon.Width / 2 + rot.At<double>(0, 1) * icon.Height / 2 + rot.At<double>(0, 2),
                            rot.At<double>(1, 0) * icon.Width / 2 + rot.At<double>(1, 1) * icon.Height / 2 + rot.At<double>(1, 2));
        return (warped, c);
    }

    // ---------- correctness: rotation/scale invariance (the stage's reason to exist) ----------

    [Theory]
    [InlineData(20, 1.0)]
    [InlineData(12, 1.2)]   // the rotation case in VisionTests uses these parameters on the game icon
    [InlineData(-35, 0.8)]
    [InlineData(0, 1.4)]
    public void Finds_rotated_and_scaled_icon(double deg, double k)
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 729, 40);
        var (warped, c) = Warp(tpl, deg, k);
        using (warped) Paste(frame, warped, 600, 300);
        using var ctx = new FrameContext(frame);
        var d = FeatureMatching.Find(ctx, tpl, null);
        Assert.NotNull(d);
        Assert.Equal("features", d.Value.Method);
        Assert.InRange(d.Value.Center.X, 600 + c.X - 10, 600 + c.X + 10);
        Assert.InRange(d.Value.Center.Y, 300 + c.Y - 10, 300 + c.Y + 10);
    }

    [Fact]
    public void Partly_covered_icon_is_still_found()
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 729, 41);
        var (warped, c) = Warp(tpl, 0, 1.0);
        using (warped) Paste(frame, warped, 400, 250);
        Cv2.Rectangle(frame, new Rect(400 + 98, 250 + 20, 38, 96), new Scalar(10, 10, 10), -1);   // right edge covered
        using var ctx = new FrameContext(frame);
        var d = FeatureMatching.Find(ctx, tpl, null);
        Assert.NotNull(d);
        Assert.InRange(d.Value.Center.X, 400 + c.X - 10, 400 + c.X + 10);
    }

    /// <summary>The banded slicing the watcher uses must reach the same answer as the one-shot pass.</summary>
    [Fact]
    public void Banded_and_one_shot_agree()
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 900, 42);   // taller than a few bands
        var (warped, c) = Warp(tpl, 15, 1.1);
        using (warped) Paste(frame, warped, 500, 500);
        using var ctxA = new FrameContext(frame);
        using var ctxB = new FrameContext(frame);
        var a = FeatureMatching.Find(ctxA, tpl, null);                                 // one shot inside
        var b = Drain(FeatureMatching.FindSteps(ctxB, tpl, null, sliced: true));       // bands
        Assert.NotNull(a); Assert.NotEmpty(b);
        _o.WriteLine($"one-shot kps {(ctxA.FeatureKp?.Length ?? -1)} vs banded {ctxB.FeatureKp?.Length ?? -1}; centres {a.Value.Center} vs {b[0].Center}");
        Assert.True(Math.Abs(a.Value.Center.X - b[0].Center.X) <= 8 && Math.Abs(a.Value.Center.Y - b[0].Center.Y) <= 8,
            $"one-shot {a.Value.Center} vs banded {b[0].Center}");
    }

    // ---------- correctness: cheap early exits ----------

    /// <summary>A featureless template never reaches the frame pass: no frame keypoints may be computed at all.</summary>
    [Fact]
    public void Flat_template_exits_before_the_frame_pass()
    {
        using var flat = new Mat(48, 64, MatType.CV_8UC3, new Scalar(200, 200, 200));
        using var frame = Scene(1280, 729, 43);
        using var ctx = new FrameContext(frame);
        Assert.Null(FeatureMatching.Find(ctx, flat, null));
        Assert.Null(ctx.FeatureKp);                    // the expensive frame pass was skipped
        Assert.Null(ctx.FeatureDesc);
    }

    /// <summary>A frame without enough keypoints answers "not found" without running the matcher.</summary>
    [Fact]
    public void Flat_frame_exits_before_matching()
    {
        using var tpl = Icon();
        using var frame = new Mat(700, 1200, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using var ctx = new FrameContext(frame);
        var sw = Stopwatch.StartNew();
        Assert.Null(FeatureMatching.Find(ctx, tpl, null));
        _o.WriteLine($"flat frame: {sw.ElapsedMilliseconds} ms, {ctx.FeatureKp?.Length ?? -1} kps");
        Assert.True(ctx.FeatureKp == null || ctx.FeatureKp.Length < 6);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");   // AKAZE still runs; the matcher must not
    }

    /// <summary>No textured icon is invented in unrelated detailed scenes (false-positive guard).</summary>
    [Fact]
    public void Absent_icon_is_not_invented()
    {
        using var tpl = Icon(seed: 9);
        for (int seed = 50; seed < 54; seed++)
        {
            using var frame = Scene(1280, 729, seed);
            using var ctx = new FrameContext(frame);
            Assert.Null(FeatureMatching.Find(ctx, tpl, null));
        }
    }

    // ---------- per-frame keypoint cache ----------

    /// <summary>
    /// After a whole-frame pass, a windowed search over a sub-area must reuse the already computed keypoints:
    /// the cached area stays the whole frame (no second AKAZE run replaced it) and the target is still found.
    /// </summary>
    [Fact]
    public void Windowed_search_reuses_the_whole_frame_keypoints()
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 729, 44);
        var (warped, c) = Warp(tpl, 18, 1.15);
        using (warped) Paste(frame, warped, 300, 400);
        using var ctx = new FrameContext(frame);
        var full = new Rect(0, 0, ctx.Width, ctx.Height);
        Assert.NotNull(FeatureMatching.Find(ctx, tpl, null));
        Assert.Equal(full, ctx.FeatureArea);
        int kps = ctx.FeatureKp!.Length;

        var hint = new Rect(250, 350, 500, 500);
        var sw = Stopwatch.StartNew();
        var d = FeatureMatching.Find(ctx, tpl, hint);
        _o.WriteLine($"hinted find after whole-frame pass: {sw.ElapsedMilliseconds} ms (frame had {kps} kps)");
        Assert.NotNull(d);
        Assert.InRange(d.Value.Center.X, 300 + c.X - 10, 300 + c.X + 10);
        Assert.Equal(full, ctx.FeatureArea);            // the hint reused the cached keypoints instead of re-running AKAZE on the window
    }

    /// <summary>The reverse order still works and caches only what it computed.</summary>
    [Fact]
    public void Small_area_then_whole_frame_recomputes_once()
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 729, 45);
        var (warped, c) = Warp(tpl, 0, 1.0);
        using (warped) Paste(frame, warped, 800, 200);
        using var ctx = new FrameContext(frame);
        var hint = new Rect(700, 100, 400, 300);
        Assert.NotNull(FeatureMatching.Find(ctx, tpl, hint));
        Assert.Equal(hint, ctx.FeatureArea);
        Assert.NotNull(FeatureMatching.Find(ctx, tpl, null));     // a bigger area cannot be served from the small cache
        Assert.Equal(new Rect(0, 0, ctx.Width, ctx.Height), ctx.FeatureArea);
    }

    /// <summary>A repeated identical search is a cache hit (no new AKAZE pass) and still answers.</summary>
    [Fact]
    public void Same_area_twice_is_a_cache_hit()
    {
        using var tpl = Icon();
        using var frame = Scene(1280, 729, 46);
        var (warped, c) = Warp(tpl, 10, 1.0);
        using (warped) Paste(frame, warped, 500, 200);
        using var ctx = new FrameContext(frame);
        Assert.NotNull(FeatureMatching.Find(ctx, tpl, null));
        var kp = ctx.FeatureKp; var desc = ctx.FeatureDesc;
        double ms = MedianMs(() => Assert.NotNull(FeatureMatching.Find(ctx, tpl, null)), 3);
        _o.WriteLine($"cache-hit find: {ms:F1} ms");
        Assert.Same(kp, ctx.FeatureKp);                 // same arrays: no recomputation happened
        Assert.Same(desc, ctx.FeatureDesc);
    }

    // ---------- probes (print the stage cost; loose ceilings) ----------

    static Mat? RealShot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Revolution", "logs", "debug_images", "inventory_detect.png");
            if (File.Exists(p)) return Cv2.ImDecode(File.ReadAllBytes(p), ImreadModes.Color);
        }
        return null;
    }

    /// <summary>Cost of the whole-frame AKAZE pass + match on a real 1080p UI shot with an absent textured target.</summary>
    [Fact]
    public void Probe_whole_frame_pass_absent_real_shot()
    {
        using var frame = RealShot();
        if (frame == null) return;
        using var tpl = Icon();
        int kps;
        using (var warm = new FrameContext(frame)) { FeatureMatching.Find(warm, tpl, null); kps = warm.FeatureKp?.Length ?? -1; }
        double ms = MedianMs(() => { using var c = new FrameContext(frame); Assert.Null(FeatureMatching.Find(c, tpl, null)); });
        _o.WriteLine($"whole-frame feature pass, absent target, real shot: {ms:F1} ms, kps {kps}");
        Assert.True(ms < 4000, $"{ms:F1} ms");
    }

    /// <summary>Cost on the synthetic noisy desktop (the worst case for keypoint density).</summary>
    [Fact]
    public void Probe_whole_frame_pass_absent_noise()
    {
        using var frame = Scene(1920, 1080, 30);
        using var tpl = Icon();
        using var warm = new FrameContext(frame); FeatureMatching.Find(warm, tpl, null);
        int kps = -1;
        double ms = MedianMs(() => { using var c = new FrameContext(frame); Assert.Null(FeatureMatching.Find(c, tpl, null)); }, 3);
        using (var c = new FrameContext(frame)) { FeatureMatching.Find(c, tpl, null); kps = c.FeatureKp?.Length ?? -1; }
        _o.WriteLine($"whole-frame feature pass, absent target, noise 1080p: {ms:F1} ms, kps {kps}");
        Assert.True(ms < 6000, $"{ms:F1} ms");
    }

    /// <summary>Keypoint statistics: how many keypoints the pass finds and how strong they are (informs thresholds).</summary>
    [Fact]
    public void Probe_keypoint_response_distribution()
    {
        using var akaze = AKAZE.Create(threshold: 0.0001f);
        foreach (var (name, img) in new (string, Mat?)[] { ("real", RealShot()), ("noise", Scene(1920, 1080, 31)), ("icon", Icon()) })
        {
            using var m = img;
            if (m == null) continue;
            using var gray = new Mat(); Cv2.CvtColor(m, gray, ColorConversionCodes.BGR2GRAY);
            var kp = akaze.Detect(gray, null);
            if (kp.Length == 0) { _o.WriteLine($"{name}: 0 keypoints"); continue; }
            var r = kp.Select(k => (double)k.Response).OrderBy(x => x).ToArray();
            _o.WriteLine($"{name}: {kp.Length} kps; response p50={r[r.Length / 2]:g3} p90={r[(int)(r.Length * .9)]:g3} p99={r[(int)(r.Length * .99)]:g3} max={r[^1]:g3}");
        }
    }

    /// <summary>How much of a hinted search is the window pass once the whole frame was scanned (per-frame reuse).</summary>
    [Fact]
    public void Probe_hinted_after_full_is_cheap()
    {
        using var frame = Scene(1280, 729, 47);
        using var tpl = Icon();
        var (warped, c) = Warp(tpl, 25, 1.0);
        using (warped) Paste(frame, warped, 300, 300);
        using var ctx = new FrameContext(frame);
        Assert.NotNull(FeatureMatching.Find(ctx, tpl, null));
        double hinted = MedianMs(() => FeatureMatching.Find(ctx, tpl, new Rect(200, 200, 500, 500)));
        double full = MedianMs(() => { using var c2 = new FrameContext(frame); FeatureMatching.Find(c2, tpl, null); }, 3);
        _o.WriteLine($"hinted-after-full {hinted:F1} ms vs cold full {full:F1} ms");
        Assert.True(hinted < Math.Max(full * 0.5, 30), $"hinted {hinted:F1} vs full {full:F1}");
    }
}
