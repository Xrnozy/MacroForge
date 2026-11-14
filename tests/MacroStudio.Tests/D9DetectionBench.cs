using System.Diagnostics;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// D9 benchmark lab: synthetic-but-realistic scenes for measuring detection ACCURACY (precision/recall) and LATENCY.
/// Backgrounds are crops of the real desktop screenshot (<see cref="Desktop"/>); targets are drawn controls planted at
/// known positions under controlled variations (brightness, hue/tint, scale, occlusion bands, noise, resample blur,
/// look-alike distractors, edge positions, tiny/flat targets, a second monitor). Everything is deterministic: seeded
/// draws, fixed crop origins, no wall-clock dependence in the image data.
///
/// Semantics asserted by the matrix (a wrong click is worse than no click):
///   Expect.Hit  -> a detection within TolPx of the planted centre must exist; wrong-position detections count as FPs.
///   Expect.Miss -> any detection is a false positive.
///   Expect.Info -> measured and reported, not asserted (still FP-checked unless FpOk).
/// </summary>
public static class D9Lab
{
    static readonly Lazy<Mat> Desk = new(() => Desktop.Make());   // 3840x1080 "virtual desktop" (two 1920x1080 monitors)
    static readonly List<Mat> Pool = [];

    /// <summary>Register a Mat for disposal at <see cref="Reset"/> (template/variant images; frames are owned by their Case).</summary>
    public static T Keep<T>(T m) where T : Mat { Pool.Add(m); return m; }
    public static void Reset() { foreach (var m in Pool) m.Dispose(); Pool.Clear(); }

    /// <summary>A fresh 1280x720 window onto the desktop screenshot; different origins = different backgrounds.</summary>
    public static Mat Crop(int x, int y, int w = 1280, int h = 720) { using var v = new Mat(Desk.Value, new Rect(x, y, w, h)); return v.Clone(); }
    /// <summary>The whole 3840x1080 virtual desktop.</summary>
    public static Mat Whole() => Desk.Value.Clone();

    // ---------- targets ----------

    static Scalar Shade(Scalar c, double k) => new(c.Val0 * k, c.Val1 * k, c.Val2 * k);

    /// <summary>A drawn button: vertical gradient, darker border, centred label (the LookAlikeButtonTests recipe).</summary>
    public static Mat Button(string label, Scalar fill, int w = 120, int h = 36)
    {
        var b = new Mat(h, w, MatType.CV_8UC3);
        for (int y = 0; y < h; y++) Cv2.Line(b, new Point(0, y), new Point(w - 1, y), Shade(fill, 0.75 + 0.25 * y / h));
        Cv2.Rectangle(b, new Rect(0, 0, w, h), Shade(fill, 0.45), 2);
        var sz = Cv2.GetTextSize(label, HersheyFonts.HersheyDuplex, 0.62, 2, out _);
        Cv2.PutText(b, label, new Point(Math.Max(2, (w - sz.Width) / 2), (h + sz.Height) / 2), HersheyFonts.HersheyDuplex, 0.62, Scalar.All(245), 1, LineTypes.AntiAlias);
        return b;
    }

    /// <summary>A keypoint-rich app icon (seeded shapes on a light disc): textured enough for AKAZE, unlike the flat buttons.</summary>
    public static Mat Icon(int size, int seed)
    {
        var rng = new Random(seed);
        var m = new Mat(size, size, MatType.CV_8UC3, new Scalar(238, 232, 225));
        Cv2.Circle(m, new Point(size / 2, size / 2), size / 2 - 1, new Scalar(40, 40, 45), 2, LineTypes.AntiAlias);
        for (int i = 0; i < 5; i++)
        {
            var c = new Scalar(rng.Next(40, 220), rng.Next(40, 220), rng.Next(40, 220));
            switch (i % 3)
            {
                case 0: Cv2.Circle(m, new Point(size / 4 + rng.Next(size / 2), size / 4 + rng.Next(size / 2)), 3 + rng.Next(size / 9), c, -1, LineTypes.AntiAlias); break;
                case 1: Cv2.Rectangle(m, new Rect(size / 5 + rng.Next(size / 2), size / 5 + rng.Next(size / 2), 6 + rng.Next(size / 7), 6 + rng.Next(size / 7)), c, -1, LineTypes.AntiAlias); break;
                default: Cv2.Line(m, new Point(rng.Next(size), rng.Next(size)), new Point(rng.Next(size), rng.Next(size)), c, 2, LineTypes.AntiAlias); break;
            }
        }
        Cv2.Line(m, new Point(4, size - 6), new Point(size - 4, 6), new Scalar(60, 90, 200), 2, LineTypes.AntiAlias);
        return m;
    }

    /// <summary>A sub-16px glyph (checkbox-sized): border, diagonal, two dots. Below the feature stage's 32px floor and too small for part voting.</summary>
    public static Mat TinyIcon(int s, int seed)
    {
        var rng = new Random(seed);
        var m = new Mat(s, s, MatType.CV_8UC3, new Scalar(235, 235, 238));
        Cv2.Rectangle(m, new Rect(0, 0, s, s), new Scalar(60, 60, 70), 1);
        Cv2.Line(m, new Point(2, 2), new Point(s - 3, s - 3), new Scalar(200, 60, 40), 1, LineTypes.AntiAlias);
        Cv2.Circle(m, new Point(s / 2, s / 3), Math.Max(1, s / 8), new Scalar(40, 160, 80), -1);
        Cv2.Circle(m, new Point(s / 3, 2 * s / 3), Math.Max(1, s / 8), new Scalar(40, 80 + rng.Next(80), 200), -1);
        return m;
    }

    /// <summary>A flat-colour rectangle: no keypoints, almost no structure. The SqDiff path's job.</summary>
    public static Mat Swatch(int w, int h, Scalar c) => new(h, w, MatType.CV_8UC3, c);

    // ---------- variations ----------

    /// <summary>Brightness gain (hover highlight &gt;1, dimmed/disabled &lt;1).</summary>
    public static Mat Gain(Mat src, double k) { var d = new Mat(); src.ConvertTo(d, -1, k); return d; }
    /// <summary>Gaussian blur standing in for antialiasing/resample blur.</summary>
    public static Mat BlurIt(Mat src, double sigma) { var d = new Mat(); Cv2.GaussianBlur(src, d, new Size(0, 0), sigma); return d; }
    /// <summary>Resized copy (DPI/scale drift), same interpolation choices as the engine's Scaled.</summary>
    public static Mat ScaleIt(Mat src, double s)
    {
        var d = new Mat();
        Cv2.Resize(src, d, new Size(Math.Max(1, (int)Math.Round(src.Width * s)), Math.Max(1, (int)Math.Round(src.Height * s))), 0, 0,
            s < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return d;
    }
    /// <summary>Hue rotation in HSV: a recolour/hover-tint that keeps every stroke (chroma distance grows with |shift|).</summary>
    public static Mat Tint(Mat src, int hue)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
        int h = hsv.Height, w = hsv.Width;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var v = hsv.At<Vec3b>(y, x);
                v.Item0 = (byte)((v.Item0 + hue) % 180);
                hsv.Set(y, x, v);
            }
        var d = new Mat();
        Cv2.CvtColor(hsv, d, ColorConversionCodes.HSV2BGR);
        return d;
    }
    /// <summary>A "greyed out" disabled button: luminance kept, chroma gone (like real disabled controls), optionally darker.</summary>
    public static Mat Desat(Mat src, double bright = 0.85)
    {
        using var g = new Mat();
        Cv2.CvtColor(src, g, ColorConversionCodes.BGR2GRAY);
        var d = new Mat();
        Cv2.CvtColor(g, d, ColorConversionCodes.GRAY2BGR);
        if (Math.Abs(bright - 1) > 0.01) d.ConvertTo(d, -1, bright);
        return d;
    }
    /// <summary>Full inversion (light-on-dark vs dark-on-light: the shape stage claims to ignore polarity).</summary>
    public static Mat Inverted(Mat src) { var d = new Mat(); Cv2.BitwiseNot(src, d); return d; }
    /// <summary>Replicates the engine's edge image (Sobel magnitude of a blurred float gray) so probes can inspect the correlation map directly.</summary>
    public static Mat EdgeMag(Mat gray)
    {
        using var f32 = new Mat();
        gray.ConvertTo(f32, MatType.CV_32F);
        Cv2.GaussianBlur(f32, f32, new Size(0, 0), 0.8);
        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(f32, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(f32, gy, MatType.CV_32F, 0, 1, 3);
        var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        return mag;
    }

    /// <summary>Contrast reduced to <paramref name="keep"/> of the original (blend toward the image's mean colour).</summary>
    public static Mat LowContrast(Mat src, double keep = 0.55)
    {
        using var flat = new Mat(src.Size(), MatType.CV_8UC3, Cv2.Mean(src));
        var d = new Mat();
        Cv2.AddWeighted(src, keep, flat, 1 - keep, 0, d);
        return d;
    }
    /// <summary>Per-pixel additive noise (triangular distribution, sigma = equivalent stddev). Deterministic from <paramref name="seed"/>.</summary>
    public static Mat Noised(Mat frame, double sigma, int seed)
    {
        var rng = new Random(seed);
        double k = sigma * 2.449;                       // sum of two U(-0.5,0.5): stddev = 1/sqrt(6)
        short N() => (short)Math.Round(k * (rng.NextDouble() + rng.NextDouble() - 1));
        var vecs = new Vec3s[frame.Width * frame.Height];
        for (int i = 0; i < vecs.Length; i++) vecs[i] = new Vec3s(N(), N(), N());
        using var noise = new Mat(frame.Height, frame.Width, MatType.CV_16SC3);
        noise.SetArray(vecs);
        using var f16 = new Mat();
        frame.ConvertTo(f16, MatType.CV_16SC3);
        Cv2.Add(f16, noise, f16);
        var d = new Mat();
        f16.ConvertTo(d, MatType.CV_8UC3);              // saturates
        return d;
    }
    /// <summary>Rotated copy on a larger white canvas (only the feature stage claims to handle rotation).</summary>
    public static Mat Rotated(Mat src, double deg)
    {
        double s = 1.7;
        using var rot = Cv2.GetRotationMatrix2D(new Point2f(src.Width / 2f, src.Height / 2f), deg, 1);
        rot.Set(0, 2, rot.At<double>(0, 2) + src.Width * (s - 1) / 2);
        rot.Set(1, 2, rot.At<double>(1, 2) + src.Height * (s - 1) / 2);
        var d = new Mat();
        Cv2.WarpAffine(src, d, rot, new Size((int)(src.Width * s), (int)(src.Height * s)), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(205, 205, 205));
        return d;
    }

    /// <summary>Draw img into scene at (x,y), clipped to the frame. Returns the VISIBLE rect (ground truth for fully-visible plants).</summary>
    public static Rect Paste(Mat scene, Mat img, int x, int y)
    {
        var r = new Rect(x, y, img.Width, img.Height).Intersect(new Rect(0, 0, scene.Width, scene.Height));
        if (r.Width <= 0 || r.Height <= 0) return r;
        using var s = new Mat(img, new Rect(r.X - x, r.Y - y, r.Width, r.Height));
        using var d = new Mat(scene, r);
        s.CopyTo(d);
        return r;
    }

    /// <summary>An opaque "popup" band over the right fraction of r, slightly taller than the target.</summary>
    public static Rect CoverRight(Mat scene, Rect r, double frac, Scalar? fill = null)
    {
        int cw = Math.Max(1, (int)Math.Round(r.Width * frac));
        var b = new Rect(r.Right - cw, r.Y - 6, cw, r.Height + 12).Intersect(new Rect(0, 0, scene.Width, scene.Height));
        Cv2.Rectangle(scene, b, fill ?? new Scalar(25, 25, 30), -1);
        return b;
    }

    // ---------- case model ----------

    public enum Expect { Hit, Miss, Info }

    /// <param name="FpOk">Do not assert zero wrong-position detections (a by-design ambiguity, e.g. a recoloured twin).</param>
    /// <param name="TopMust">Assert the FIRST (best-scoring) detection is the target, not a look-alike that also passed.</param>
    public sealed record Case(string Name, Mat Frame, Mat Tpl, Rect? Truth, double Thr, Expect Want, int TolPx = 4, bool FpOk = false, bool TopMust = false, string Note = "");

    public sealed record Row(string Name, Expect Want, bool Hit, bool TopOk, int Fp, double Err, double Score, string Method, double MsCold, double MsWarm, string Verdict, string Note);

    static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Run Find twice (cold: template prep included; warm: caches hot) and score the answer against ground truth.</summary>
    public static Row Eval(Detector det, Case c)
    {
        var sw = Stopwatch.StartNew();
        var hits = det.Find(c.Frame, c.Tpl, c.Thr, 1, null, true, 0.45);
        double cold = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        hits = det.Find(c.Frame, c.Tpl, c.Thr, 1, null, true, 0.45);
        double warm = sw.Elapsed.TotalMilliseconds;

        Point gt = c.Truth is { } t ? new Point(t.X + t.Width / 2, t.Y + t.Height / 2) : default;
        Detection? best = null; double bd = double.MaxValue; int fp = 0;
        foreach (var h in hits)
        {
            double d = c.Truth == null ? double.MaxValue : Dist(h.Center, gt);
            if (d < bd || best is null) { bd = d; best = h; }   // for absent scenes: report the first false positive's score/method
            if (d > c.TolPx) fp++;                              // a reported detection that is not the planted target
        }
        bool hit = best is { } && bd <= c.TolPx;
        bool topOk = hits.Count > 0 && c.Truth != null && Dist(hits[0].Center, gt) <= c.TolPx;
        string verdict = c.Want switch
        {
            Expect.Hit => hit ? (fp > 0 ? "HIT+fp" : "HIT") : "MISS!",
            Expect.Miss => fp > 0 ? "FP!" : "ok-miss",
            _ => hit ? "hit" : fp > 0 ? "fp" : "none",
        };
        return new Row(c.Name, c.Want, hit, topOk, fp, hit ? bd : -1, best?.Score ?? -1, best?.Method ?? "-", cold, warm, verdict, c.Note);
    }

    /// <summary>
    /// The benchmark matrix. Thresholds are tuned the way the app does it (AutoThreshold on the capture-time scene);
    /// evaluation frames are different crops/positions, so a case is never trivially identical to its tuning scene.
    /// </summary>
    public static List<Case> Matrix(Detector det)
    {
        var gold = new Scalar(40, 140, 230);
        var play = Keep(Button("PLAY", gold));
        var save = Keep(Button("SAVE", new Scalar(60, 160, 60)));
        var stop = Keep(Button("STOP", new Scalar(60, 60, 200)));
        var plan = Keep(Button("PLAN", gold));                       // one letter off PLAY, identical frame and colour
        var playG = Keep(Button("PLAY", new Scalar(60, 160, 60)));   // PLAY's strokes in a different colour
        var icon = Keep(Icon(48, 7));
        var tiny = Keep(TinyIcon(14, 3));
        var swatch = Keep(Swatch(48, 32, new Scalar(30, 80, 235)));
        var flat = Keep(Swatch(120, 36, new Scalar(40, 140, 230)));

        double Thr(Mat tpl, int cx, int cy, int px, int py)
        {
            using var f = Crop(cx, cy);
            var r = Paste(f, tpl, px, py);
            return det.AutoThreshold(f, r);
        }
        double thrA = Thr(play, 0, 0, 520, 300);
        double thrI = Thr(icon, 300, 60, 700, 200);
        double thrT = Thr(tiny, 700, 100, 300, 600);
        double thrS = Thr(swatch, 1900, 60, 800, 500);
        double thrF = Thr(flat, 0, 0, 1050, 640);

        var cases = new List<Case>();
        void Add(string name, Mat frame, Mat tpl, Rect? truth, double thr, Expect want = Expect.Hit, int tol = 4,
                 bool fpOk = false, bool topMust = false, string note = "")
            => cases.Add(new Case(name, frame, tpl, truth, thr, want, tol, fpOk, topMust, note));

        // --- exact / brightness / tint ---
        { var f = Crop(0, 0); var r = Paste(f, play, 620, 380); Add("baseline", f, play, r, thrA, topMust: true); }
        { var f = Crop(0, 0); var r = Paste(f, Keep(Gain(play, 0.75)), 620, 380); Add("target-dim-25%", f, play, r, thrA, topMust: true); }
        { var f = Crop(0, 0); var r = Paste(f, Keep(Gain(play, 1.3)), 620, 380); Add("target-hover+30%", f, play, r, thrA, topMust: true); }
        { var f0 = Crop(0, 0); var r = Paste(f0, play, 620, 380); var f = Gain(f0, 1.18); f0.Dispose(); Add("screen+18%bright", f, play, r, thrA); }
        { var f0 = Crop(0, 0); var r = Paste(f0, play, 620, 380); var f = Gain(f0, 0.7); f0.Dispose(); Add("screen-30%bright", f, play, r, thrA); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Tint(play, 12)), 500, 300); Add("mild-hover-tint", f, play, r, thrA); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Tint(play, 30)), 500, 300); Add("tint+30", f, play, r, thrA, Expect.Info, note: "chroma boundary probe (~30 tolerance)"); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Tint(play, 45)), 500, 300); Add("tint+45", f, play, r, thrA, Expect.Info, note: "where does recolour stop matching?"); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Tint(play, 60)), 500, 300); Add("tint+60", f, play, r, thrA, Expect.Info); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Tint(play, 75)), 500, 300); Add("recoloured-target", f, play, r, thrA, note: "docs claim recolour still matches: strokes score 0.99 alone, ~0.63 in-frame (outer seam ring dominates the normed edge correlation)"); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Desat(play)), 500, 300); Add("greyed-out", f, play, r, thrA, note: "luminance-identical disabled look: 1.00 edge match alone, ~0.5 in-frame; the seam ring, not the strokes, decides"); }
        { var f = Crop(300, 60); var r = Paste(f, Keep(Inverted(play)), 500, 300); Add("light-on-dark", f, play, r, thrA, note: "inverted polarity: docs claim shape ignores it"); }

        // --- scale (0.85-1.15: sweep offers .8/.87/.93/1/1.07/1.14/1.25, features back it up) ---
        { var f = Crop(700, 100); var r = Paste(f, Keep(ScaleIt(icon, 0.85)), 700, 300); Add("scale-0.85-icon", f, icon, r, thrI, tol: 6); }
        { var f = Crop(700, 100); var r = Paste(f, Keep(ScaleIt(icon, 0.90)), 900, 300); Add("scale-0.90-icon", f, icon, r, thrI, tol: 6); }
        { var f = Crop(700, 100); var r = Paste(f, Keep(ScaleIt(icon, 1.15)), 400, 550); Add("scale-1.15-icon", f, icon, r, thrI, tol: 6); }
        { var f = Crop(700, 100); var r = Paste(f, Keep(ScaleIt(play, 1.10)), 900, 600); Add("scale-1.10-button", f, play, r, thrA, tol: 6); }

        // --- occlusion bands ---
        { var f = Crop(0, 0); var r = Paste(f, play, 250, 620); CoverRight(f, r, 0.15); Add("occluded-15%", f, play, r, thrA); }
        { var f = Crop(0, 0); var r = Paste(f, play, 700, 620); CoverRight(f, r, 0.30); Add("occluded-30%", f, play, r, thrA); }
        { var f = Crop(0, 0); var r = Paste(f, play, 1150, 620); CoverRight(f, r, 0.50); Add("occluded-50%", f, play, r, thrA, note: "half visible: at the minVisible edge"); }
        { var f = Crop(0, 0); var r = Paste(f, play, 1100, 520); CoverRight(f, r, 0.70); Add("occluded-70%", f, play, r, thrA, Expect.Info, note: "30% visible < minVisible: a miss is fine, a wrong click is not"); }

        // --- noise / blur / contrast ---
        { var f0 = Crop(200, 40); var r = Paste(f0, play, 600, 400); var f = Noised(f0, 8, 11); f0.Dispose(); Add("noise-sigma8", f, play, r, thrA); }
        { var f0 = Crop(200, 40); var r = Paste(f0, play, 900, 500); var f = Noised(f0, 16, 12); f0.Dispose(); Add("noise-sigma16", f, play, r, thrA, Expect.Info, note: "heavy static: measured, not asserted"); }
        { var f = Crop(200, 40); var r = Paste(f, Keep(BlurIt(play, 1.0)), 1050, 300); Add("resample-blur", f, play, r, thrA); }
        { var f = Crop(200, 40); var r = Paste(f, Keep(LowContrast(play, 0.55)), 1000, 560); Add("low-contrast-45%", f, play, r, thrA, note: "normalised correlation should be indifferent"); }

        // --- look-alike distractors ---
        { var f = Crop(300, 60); Paste(f, stop, 900, 300); Paste(f, Keep(Button("EXIT", new Scalar(140, 60, 140))), 1150, 480); var r = Paste(f, play, 500, 300);
          Add("diff-label-diff-colour", f, play, r, thrA, topMust: true, note: "STOP/EXIT nearby must not score"); }
        { var f = Crop(300, 60); Paste(f, plan, 820, 300); var r = Paste(f, play, 480, 300);
          Add("lookalike-label-1-letter", f, play, r, thrA, fpOk: true, topMust: true, note: "PLAN vs PLAY: an extra hit is tolerable, the BEST hit must be PLAY"); }
        { var f = Crop(300, 60); var r = Paste(f, play, 480, 300); Paste(f, playG, 820, 300);
          Add("recolour-twin-present", f, play, r, thrA, topMust: true, note: "green PLAY must lose at the colour check"); }

        // --- geometry / special targets ---
        { var f = Crop(0, 0); var r = Paste(f, play, 1280 - play.Width - 3, 6); Add("near-right-edge", f, play, r, thrA); }
        { var f = Crop(700, 100); var r = Paste(f, tiny, 300, 600); Add("tiny-14px", f, tiny, r, thrT); }
        { var f = Crop(2240, 0); var r = Paste(f, swatch, 620, 430); Paste(f, Keep(Swatch(48, 32, new Scalar(90, 60, 60))), 1050, 620);
          Add("flat-swatch+distract", f, swatch, r, thrS, topMust: true, note: "flat colour: few keypoints, SqDiff path"); }
        { var f = Crop(300, 60); var r = Paste(f, flat, 900, 640); Add("flat-120x36", f, flat, r, thrF); }
        { var f = Whole(); var r = Paste(f, play, 2500, 400); Add("monitor-2", f, play, r, thrA, note: "full 3840x1080 virtual desktop"); }
        { var f = Crop(0, 0); Paste(f, play, 1280 - 46, 200); var truth = new Rect(1280 - 46, 200, play.Width, play.Height);
          Add("half-off-right-edge", f, play, truth, thrA, Expect.Info, note: "only the left 46px show"); }
        { var f = Crop(900, 80); var r = Paste(f, Keep(Rotated(icon, 15)), 700, 400); Add("rotated-15deg-icon", f, icon, r, thrI, Expect.Info, tol: 12, note: "template match does not do rotation"); }

        // --- absent: any detection is a false positive (a click on nothing) ---
        { var f = Crop(0, 0); Add("absent-clean", f, play, null, thrA, Expect.Miss); }
        { var f = Crop(300, 60); Paste(f, stop, 600, 300); Paste(f, Keep(Button("EXIT", new Scalar(140, 60, 140))), 950, 500); Add("absent-distractors", f, play, null, thrA, Expect.Miss); }
        { var f = Crop(300, 60); Paste(f, plan, 600, 350); Add("absent-lookalike-label", f, play, null, thrA, Expect.Miss, note: "PLAN alone must not answer a PLAY search"); }
        { var f = Crop(0, 0); var r = Paste(f, play, 620, 380); CoverRight(f, r, 1.0); Add("absent-fully-covered", f, play, null, thrA, Expect.Miss); }
        { var f = Crop(300, 60); Paste(f, playG, 600, 350); Add("absent-recoloured-twin", f, play, null, thrA, Expect.Info, fpOk: true,
            note: "green PLAY is found by shape: the colour-ignored fallback is BY DESIGN (docs) but shows as an 'FP' here"); }
        { var f0 = Crop(200, 40); var f = Noised(f0, 12, 31); f0.Dispose(); Add("absent-noise", f, play, null, thrA, Expect.Miss); }
        return cases;
    }

    // ---------- reporting ----------

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Round(p * (sorted.Count - 1)))];

    /// <summary>Diffable BENCH-D9 table + a one-line machine-readable summary (BENCH-D9 SUMMARY ...).</summary>
    public static void Print(List<Row> rows)
    {
        Console.WriteLine("BENCH-D9 case                        want  verdict fp  errPx score  method    msCold msWarm note");
        foreach (var r in rows)
            Console.WriteLine(FormattableString.Invariant(
                $"BENCH-D9 {r.Name,-25} {r.Want,-4} {r.Verdict,-7} {r.Fp,2} {r.Err,6:0.0} {r.Score,6:0.00} {r.Method,-9} {r.MsCold,6:0} {r.MsWarm,6:0} {r.Note}"));

        var hitWanted = rows.Where(r => r.Want == Expect.Hit).ToList();
        var missWanted = rows.Where(r => r.Want == Expect.Miss).ToList();
        int okHits = hitWanted.Count(r => r.Hit);
        int cleanMiss = missWanted.Count(r => r.Fp == 0);
        int fpCases = rows.Count(r => r.Fp > 0);
        int fpTotal = rows.Sum(r => r.Fp);
        var errs = hitWanted.Where(r => r.Hit).Select(r => r.Err).OrderBy(x => x).ToList();
        var cold = rows.Select(r => r.MsCold).OrderBy(x => x).ToList();
        var warm = rows.Select(r => r.MsWarm).OrderBy(x => x).ToList();
        double recall = hitWanted.Count == 0 ? 0 : 100.0 * okHits / hitWanted.Count;
        FormattableString msg = $"BENCH-D9 SUMMARY cases={rows.Count} recall={okHits}/{hitWanted.Count} ({recall:0.#}%) cleanMiss={cleanMiss}/{missWanted.Count} fpCases={fpCases} fpTotal={fpTotal} errPx(med/p90)={Pct(errs, .5):0.0}/{Pct(errs, .9):0.0} msCold(med/p90/max)={Pct(cold, .5):0}/{Pct(cold, .9):0}/{cold.LastOrDefault():0} msWarm(med)={Pct(warm, .5):0}";
        Console.WriteLine(FormattableString.Invariant(msg));
    }
}

/// <summary>
/// D9: the objective accuracy+latency benchmark the detection engine lacked. Reports per-scenario verdict / false
/// positives / position error / score / milliseconds as BENCH-D9 lines, then a diffable summary. Assertions demand the
/// documented behaviour; where the current implementation cannot meet it, the failure stays visible (see Notes).
/// </summary>
public class D9DetectionBench
{
    [Fact]
    public void Case_matrix_precision_recall_and_latency()
    {
        var det = new Detector();
        var cases = D9Lab.Matrix(det);
        try
        {
            var rows = cases.Select(c => D9Lab.Eval(det, c)).ToList();
            D9Lab.Print(rows);
            var bad = new List<string>();
            foreach (var (c, r) in cases.Zip(rows))
            {
                if (c.Want == D9Lab.Expect.Hit && !r.Hit) bad.Add($"{c.Name}: target missed");
                if (c.Want == D9Lab.Expect.Miss && r.Fp > 0) bad.Add($"{c.Name}: {r.Fp} false positive(s)");
                if (r.Fp > 0 && !c.FpOk) bad.Add($"{c.Name}: {r.Fp} wrong-position detection(s)");
                if (c.TopMust && !r.TopOk) bad.Add($"{c.Name}: best-scoring detection is not the target");
            }
            Assert.True(bad.Count == 0, "D9 benchmark failures: " + string.Join(" | ", bad) + " (see the BENCH-D9 table in the test output)");
        }
        finally { foreach (var c in cases) c.Frame.Dispose(); D9Lab.Reset(); }
    }

    /// <summary>FindTracked: cold find, +-10px verify, appearance drift, partial cover, far jump, disappearance, re-find.</summary>
    [Fact]
    public void FindTracked_stays_accurate_and_cheap_through_a_session()
    {
        var det = new Detector();
        var play = D9Lab.Keep(D9Lab.Button("PLAY", new Scalar(40, 140, 230)));
        try
        {
            List<Detection> hits;
            var sw = Stopwatch.StartNew();
            void Report(string step, List<Detection> h, Rect? truth, double ms)
            {
                var b = h.Count > 0 ? h[0] : (Detection?)null;
                Console.WriteLine(FormattableString.Invariant(
                    $"BENCH-D9 TRACK {step,-28} {(h.Count > 0 ? "hit" : "miss"),-4} {(b is { } d ? $"{d.Method}@({d.Rect.X},{d.Rect.Y}) s={d.Score:0.00}" : "-"),-32} {ms,6:0.0} ms"));
            }

            using var f0 = D9Lab.Crop(0, 0);
            var r0 = D9Lab.Paste(f0, play, 500, 300);
            double thr = det.AutoThreshold(f0, r0);
            sw.Restart(); hits = det.FindTracked("t", f0, play, thr); double cold = sw.Elapsed.TotalMilliseconds;
            Report("cold-find", hits, r0, cold);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Rect.X - r0.X) <= 4);

            // +-6px move: the verify window covers it, cost should be a small fraction of a cold find
            using var f1 = D9Lab.Crop(0, 0);
            var r1 = D9Lab.Paste(f1, play, 506, 304);
            sw.Restart(); hits = det.FindTracked("t", f1, play, thr); double t1 = sw.Elapsed.TotalMilliseconds;
            Report("verify-after-6px-move", hits, r1, t1);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Rect.X - r1.X) <= 4, $"tracked position wrong: {hits.FirstOrDefault().Rect}");
            Assert.True(t1 < 20, $"verify should be nearly free, took {t1:0.0} ms (cold find {cold:0.0} ms)");

            // hover-brightened in place: tracked appearance drifts, verify's slack keeps it
            using var f2 = D9Lab.Crop(0, 0);
            var r2 = D9Lab.Paste(f2, D9Lab.Keep(D9Lab.Gain(play, 1.25)), 512, 308);
            sw.Restart(); hits = det.FindTracked("t", f2, play, thr); double t2 = sw.Elapsed.TotalMilliseconds;
            Report("verify-under-hover+25%", hits, r2, t2);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Rect.X - r2.X) <= 4, $"lost under brightness drift: {hits.FirstOrDefault().Rect}");

            // right third covered by a popup: the tracked check falls back to part voting at the same spot
            using var f3 = D9Lab.Crop(0, 0);
            var r3 = D9Lab.Paste(f3, play, 512, 308);
            D9Lab.CoverRight(f3, r3, 0.35);
            sw.Restart(); hits = det.FindTracked("t", f3, play, thr); double t3 = sw.Elapsed.TotalMilliseconds;
            Report("verify-35%-covered", hits, r3, t3);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Center.X - (r3.X + r3.Width / 2)) <= 6, $"covered target lost: {hits.FirstOrDefault().Rect}");

            // teleported far outside the tracking window: hint-window search, then whole-frame fallback
            using var f4 = D9Lab.Crop(0, 0);
            var r4 = D9Lab.Paste(f4, play, 1100, 600);
            sw.Restart(); hits = det.FindTracked("t", f4, play, thr); double t4 = sw.Elapsed.TotalMilliseconds;
            Report("jumped-far", hits, r4, t4);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Rect.X - r4.X) <= 4);

            // gone entirely: must answer empty (a stale hit would click empty space), and forget the position
            using var f5 = D9Lab.Crop(0, 0);
            sw.Restart(); hits = det.FindTracked("t", f5, play, thr); double t5 = sw.Elapsed.TotalMilliseconds;
            Report("disappeared", hits, null, t5);
            Assert.Empty(hits);

            // back somewhere else: next call is a cold search again
            using var f6 = D9Lab.Crop(0, 0);
            var r6 = D9Lab.Paste(f6, play, 200, 640);
            sw.Restart(); hits = det.FindTracked("t", f6, play, thr); double t6 = sw.Elapsed.TotalMilliseconds;
            Report("reappeared-elsewhere", hits, r6, t6);
            Assert.True(hits.Count > 0 && Math.Abs(hits[0].Rect.X - r6.X) <= 4);
        }
        finally { D9Lab.Reset(); }
    }

    /// <summary>
    /// Diagnostic: runs the staged pipeline explicitly (Light, then deep stages 0-3 = scale sweep / part voting / shape /
    /// features) on the variations the matrix reports as misses, and prints each stage's raw answer. Shows exactly WHERE
    /// the pipeline drops a case — a report, not an assertion.
    /// </summary>
    [Fact]
    public void Stage_by_stage_probe_for_cases_that_miss()
    {
        var det = new Detector();
        var play = D9Lab.Keep(D9Lab.Button("PLAY", new Scalar(40, 140, 230)));
        try
        {
            using var cap = D9Lab.Crop(0, 0);
            var rc = D9Lab.Paste(cap, play, 520, 300);
            double thr = det.AutoThreshold(cap, rc);
            Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE thr={thr:0.00} (stages: light, 0=scales, 1=patches, 2=shape, 3=features)"));
            var variants = new (string Name, Mat Img)[]
            {
                ("tint+75 (recoloured)", D9Lab.Keep(D9Lab.Tint(play, 75))),
                ("greyed-out (luminance)", D9Lab.Keep(D9Lab.Desat(play))),
                ("PLAN instead of PLAY", D9Lab.Keep(D9Lab.Button("PLAN", new Scalar(40, 140, 230)))),
            };
            foreach (var (name, img) in variants)
            {
                using var f = D9Lab.Crop(300, 60);
                var r = D9Lab.Paste(f, img, 500, 300);
                using var ctx = new FrameContext(f);
                var sw = Stopwatch.StartNew();
                var light = det.Light(ctx, play, thr, 1, null, out double bs);
                Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE {name,-26} light: {light.Count} hit(s), bestScore={bs:0.00} ({sw.ElapsedMilliseconds} ms)"));
                // raw edge-correlation peak (replicates ShapeMatching.EdgeOf + CCoeffNormed) at the planted spot vs the map's best
                using (var g = new Mat())
                {
                    Cv2.CvtColor(play, g, ColorConversionCodes.BGR2GRAY);
                    using var te = D9Lab.EdgeMag(g);
                    using var fg = new Mat();
                    Cv2.CvtColor(f, fg, ColorConversionCodes.BGR2GRAY);
                    using var fe = D9Lab.EdgeMag(fg);
                    using var map = new Mat();
                    Cv2.MatchTemplate(fe, te, map, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(map, out _, out double mx, out _, out Point mxloc);
                    double at = map.At<float>(300, 500);
                    Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE {name,-26} edgeMap: max={mx:0.00}@({mxloc.X},{mxloc.Y}) planted-spot={at:0.00} (planted at 500,300)"));
                    // standalone: edge-correlation of the template against the VARIANT ALONE (no frame, no background ring)
                    using (var vg = new Mat())
                    {
                        Cv2.CvtColor(img, vg, ColorConversionCodes.BGR2GRAY);
                        using var ve = D9Lab.EdgeMag(vg);
                        using var solo = new Mat();
                        Cv2.MatchTemplate(ve, te, solo, TemplateMatchModes.CCoeffNormed);
                        Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE {name,-26} edge-vs-variant-alone={(solo.Width == 1 && solo.Height == 1 ? solo.At<float>(0, 0) : float.NaN):0.00}"));
                    }
                    // control: the same variant planted on a FLAT patch (no desktop texture behind the seam)
                    using (var flat = new Mat(140, 220, MatType.CV_8UC3, new Scalar(228, 226, 224)))
                    {
                        D9Lab.Paste(flat, img, 50, 50);
                        using var cfg = new Mat();
                        Cv2.CvtColor(flat, cfg, ColorConversionCodes.BGR2GRAY);
                        using var cfe = D9Lab.EdgeMag(cfg);
                        using var cmap = new Mat();
                        Cv2.MatchTemplate(cfe, te, cmap, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(cmap, out _, out double cmx, out _, out _);
                        Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE {name,-26} edge-on-flat-bg={cmx:0.00}"));
                    }
                }
                foreach (var st in Enumerable.Range(0, Detector.DeepStages))
                {
                    sw.Restart();
                    var d = det.DeepStage(ctx, play, thr, 1, st, 0.45);
                    var first = d.Count > 0 ? $"{d[0].Method}@({d[0].Rect.X},{d[0].Rect.Y}) s={d[0].Score:0.00} v={d[0].Visible:P0}" : "-";
                    Console.WriteLine(FormattableString.Invariant($"BENCH-D9 PROBE {name,-26} stage{st}: {d.Count} hit(s) {first} ({sw.ElapsedMilliseconds} ms)"));
                }
            }
        }
        finally { D9Lab.Reset(); }
    }

    /// <summary>Two pixel-identical buttons are both returned; a same-strokes different-colour one is not (template-stage colour check).</summary>
    [Fact]
    public void Two_identical_buttons_are_both_found_but_a_recoloured_one_is_not_listed()
    {
        var det = new Detector();
        var play = D9Lab.Keep(D9Lab.Button("PLAY", new Scalar(40, 140, 230)));
        try
        {
            using var f = D9Lab.Crop(0, 0);
            using var cap = D9Lab.Crop(300, 60);
            var rc = D9Lab.Paste(cap, play, 400, 300);
            double thr = det.AutoThreshold(cap, rc);
            var a = D9Lab.Paste(f, play, 300, 300);
            var b = D9Lab.Paste(f, play, 1100, 650);
            D9Lab.Paste(f, D9Lab.Keep(D9Lab.Button("PLAY", new Scalar(60, 160, 60))), 750, 480);   // recoloured twin
            var sw = Stopwatch.StartNew();
            var hits = det.Find(f, play, thr);
            var list = string.Join("; ", hits.Select(h => $"{h.Rect.X},{h.Rect.Y} {h.Method} {h.Score:0.00}"));
            FormattableString msg = $"BENCH-D9 twins: {hits.Count} hit(s) in {sw.ElapsedMilliseconds} ms: {list}";
            Console.WriteLine(FormattableString.Invariant(msg));
            bool Near(Point c, Rect r) => Math.Abs(c.X - (r.X + r.Width / 2)) <= 4 && Math.Abs(c.Y - (r.Y + r.Height / 2)) <= 4;
            Assert.Contains(hits, h => Near(h.Center, a));
            Assert.Contains(hits, h => Near(h.Center, b));
            Assert.All(hits, h => Assert.True(Near(h.Center, a) || Near(h.Center, b), $"unexpected hit at {h.Rect} (the recoloured twin should not list)"));
        }
        finally { D9Lab.Reset(); }
    }
}
