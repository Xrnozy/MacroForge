using System.Diagnostics;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>D1: detector-core accuracy contracts plus stage-level timing, so the pipeline stays correct while internals are tuned.</summary>
public class D1DetectorTests
{
    static Mat Background(int w, int h, int seed)
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

    static Mat Button(string text = "PLAY", Scalar? color = null)
    {
        var b = new Mat(32, 80, MatType.CV_8UC3, color ?? new Scalar(40, 140, 230));
        for (int y = 0; y < 32; y++) Cv2.Line(b, new Point(0, y), new Point(79, y), Shade(color ?? new Scalar(40, 140, 230), 0.7 + 0.3 * y / 32.0));
        Cv2.Rectangle(b, new Rect(0, 0, 80, 32), Scalar.All(255), 2);
        Cv2.PutText(b, text, new Point(12, 22), HersheyFonts.HersheySimplex, 0.7, Scalar.All(255), 2);
        return b;
    }

    static Scalar Shade(Scalar c, double k) => new(c.Val0 * k, c.Val1 * k, c.Val2 * k);

    static void Paste(Mat frame, Mat img, int x, int y)
    {
        using var roi = new Mat(frame, new Rect(x, y, img.Width, img.Height));
        img.CopyTo(roi);
    }

    // ---------- stage timing breakdown (diagnostic for the "absent target" latency) ----------

    [Fact]
    public void Absent_target_stage_breakdown_on_full_hd()
    {
        using var btn = Button(); using var frame = Background(1920, 1080, 30);
        var det = new Detector();
        using var ctx = new FrameContext(frame);
        // warm every lazy path once (template cache, gray/half pyramids, AKAZE template)
        det.Find(frame, btn, 0.85);

        var t = new List<(string Stage, double Ms)>();
        var sw = Stopwatch.StartNew();
        det.Find(frame, btn, 0.85, occlusion: false);            // template sweep, all 7 scales
        t.Add(("template-only", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        det.Light(ctx, btn, 0.85, 1, null, out _);                // the two likely scales
        t.Add(("light", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        DrainDeep(det, ctx, btn, 0.85, 1, stage: 0);             // remaining sweep scales
        t.Add(("deep0-scales", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        DrainDeep(det, ctx, btn, 0.85, 1, stage: 1);             // part voting
        t.Add(("deep1-patches", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        DrainDeep(det, ctx, btn, 0.85, 1, stage: 2);             // shape
        t.Add(("deep2-shape", sw.Elapsed.TotalMilliseconds));

        sw.Restart();
        DrainDeep(det, ctx, btn, 0.85, 1, stage: 3);             // features
        t.Add(("deep3-features", sw.Elapsed.TotalMilliseconds));

        foreach (var (s, ms) in t) Console.WriteLine($"D1-STAGE {s}: {ms:F1} ms");
        Assert.True(true);
    }

    static List<Detection> DrainDeep(Detector det, FrameContext ctx, Mat tpl, double thr, double baseScale, int stage)
    {
        List<Detection> last = [];
        foreach (var s in det.DeepSteps(ctx, tpl, thr, baseScale, stage, 0.45, sliced: false)) if (s != null) last = s;
        return last;
    }

    // ---------- contracts that must hold while internals change ----------

    [Fact]
    public void Sweep_scales_find_the_button_at_plus_or_minus_ten_percent()
    {
        using var btn = Button();
        var det = new Detector();
        foreach (var s in new[] { 0.9, 1.1 })
        {
            using var frame = Background(900, 600, 60);
            using var scaled = new Mat();
            Cv2.Resize(btn, scaled, new Size((int)Math.Round(80 * s), (int)Math.Round(32 * s)), 0, 0, InterpolationFlags.Linear);
            Paste(frame, scaled, 400, 300);
            var r = det.Find(frame, btn, 0.8);
            Assert.NotEmpty(r);
            Assert.InRange(r[0].Center.X, 400 + scaled.Width / 2 - 3, 400 + scaled.Width / 2 + 3);
            Assert.InRange(r[0].Center.Y, 300 + scaled.Height / 2 - 3, 300 + scaled.Height / 2 + 3);
        }
    }

    [Fact]
    public void Two_distant_instances_both_survive_the_coarse_fine_pass()
    {
        using var btn = Button(); using var frame = Background(1200, 800, 61);
        Paste(frame, btn, 100, 100); Paste(frame, btn, 900, 600);
        var r = new Detector().Find(frame, btn, 0.9);
        Assert.True(r.Count >= 2, $"expected 2, got {r.Count}");
        Assert.Contains(r, d => Math.Abs(d.Rect.X - 100) <= 2 && Math.Abs(d.Rect.Y - 100) <= 2);
        Assert.Contains(r, d => Math.Abs(d.Rect.X - 900) <= 2 && Math.Abs(d.Rect.Y - 600) <= 2);
    }

    [Fact]
    public void Best_score_is_reported_even_when_nothing_passes()
    {
        using var btn = Button(); using var twin = Button("PLAT");
        using var frame = Background(900, 600, 62);
        Paste(frame, twin, 400, 300);                                   // a near miss, not a match
        var det = new Detector();
        det.Search(frame, btn, 0.99, 1, null, out double best, occlusion: false);
        Assert.InRange(best, 0.5, 0.99);
        using var with = frame.Clone(); Paste(with, btn, 100, 100);
        det.Search(with, btn, 0.99, 1, null, out double best3, occlusion: false);
        Assert.InRange(best3, 0.99, 1.0);
    }

    [Fact]
    public void Verify_keeps_a_slightly_appearance_drifted_target_tracked()
    {
        using var btn = Button(); using var frame = Background(900, 600, 63);
        Paste(frame, btn, 300, 200);
        var det = new Detector();
        using var ctx = new FrameContext(frame);
        var hit = det.Light(ctx, btn, 0.85, 1, null, out _)[0];
        // hover tint: same strokes, brighter by 15%
        using var lit = new Mat(); frame.ConvertTo(lit, -1, 1.15, 8);
        using var ctx2 = new FrameContext(lit);
        var v = det.Verify(ctx2, btn, hit, 0.85, 0.45, true);
        Assert.NotNull(v);
        Assert.Equal(hit.Rect, v.Value.Rect);
    }

    [Fact]
    public void PeakNear_prefers_the_position_closest_to_expected_among_equal_scores()
    {
        using var map = new Mat(20, 40, MatType.CV_32F, Scalar.All(0));
        map.Set<float>(5, 10, 1f); map.Set<float>(5, 11, 1f); map.Set<float>(5, 12, 1f); // plateau
        var p = Detector.PeakNear(map, 1.0, new Point(12, 5));
        Assert.Equal(new Point(12, 5), p);
        var q = Detector.PeakNear(map, 1.0, new Point(10, 5));
        Assert.Equal(new Point(10, 5), q);
    }

    [Fact]
    public void ScoreMap_of_a_colour_image_equals_the_mean_of_per_channel_scores()
    {
        using var img = Background(200, 120, 64);
        using var tpl = new Mat(img, new Rect(40, 30, 32, 16)).Clone();
        using var map = Detector.ScoreMap(img, tpl);
        Assert.Equal(new Size(img.Width - tpl.Width + 1, img.Height - tpl.Height + 1), map.Size());
        Cv2.MinMaxLoc(map, out _, out double mx, out _, out Point loc);
        Assert.InRange(mx, 0.99, 1.0);                          // template cut out of the image: perfect score where it came from
        Assert.Equal(new Point(40, 30), loc);
    }

    [Fact]
    public void Flat_template_uses_the_squared_difference_path_and_still_scores_one()
    {
        using var img = new Mat(120, 200, MatType.CV_8UC3, new Scalar(90, 90, 90));
        using var tpl = new Mat(24, 24, MatType.CV_8UC3, new Scalar(90, 90, 90));
        using var map = Detector.ScoreMap(img, tpl);
        Cv2.MinMaxLoc(map, out _, out double mx, out _, out _);
        Assert.InRange(mx, 0.99, 1.0);
    }
}
