using System.IO;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// A grid of partial occlusions: how much of the target is covered, where, with what, for several kinds of target
/// (textured icons, flat buttons with text, small icons). Prints what fails; the assertions below pin what must work.
/// </summary>
public class OcclusionGridTests
{
    static Mat? RealFrame()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Revolution", "logs", "debug_images", "inventory_detect.png");
            if (File.Exists(p)) return Cv2.ImDecode(File.ReadAllBytes(p), ImreadModes.Color);
        }
        return null;
    }

    // ---- targets ----

    public sealed record TargetCase(string Name, Mat Frame, Rect Where, Mat Tpl);

    static Mat FlatButton(Scalar fill, string text, int w = 110, int h = 40)
    {
        var b = new Mat(h, w, MatType.CV_8UC3, fill);
        for (int y = 0; y < h; y++) Cv2.Line(b, new Point(0, y), new Point(w - 1, y), new Scalar(fill.Val0 * (0.8 + 0.2 * y / h), fill.Val1 * (0.8 + 0.2 * y / h), fill.Val2 * (0.8 + 0.2 * y / h)));
        Cv2.Rectangle(b, new Rect(0, 0, w, h), new Scalar(fill.Val0 * 0.5, fill.Val1 * 0.5, fill.Val2 * 0.5), 2);
        Cv2.PutText(b, text, new Point(Math.Max(4, w / 2 - text.Length * 11), h / 2 + 8), HersheyFonts.HersheySimplex, h / 50.0, Scalar.All(255), 2);
        return b;
    }

    static Mat Noise(int w, int h, int seed)
    {
        using var small = new Mat(h / 8, w / 8, MatType.CV_8UC3);
        var rng = new Random(seed);
        for (int y = 0; y < small.Rows; y++) for (int x = 0; x < small.Cols; x++) small.Set(y, x, new Vec3b((byte)rng.Next(40, 210), (byte)rng.Next(40, 210), (byte)rng.Next(40, 210)));
        var big = new Mat(); Cv2.Resize(small, big, new Size(w, h), 0, 0, InterpolationFlags.Cubic);
        return big;
    }

    public static List<TargetCase> Cases()
    {
        var l = new List<TargetCase>();
        using var real = RealFrame();
        if (real != null)
        {
            foreach (var (n, r) in new[] { ("textured icon 76px", new Rect(8, 455, 76, 76)), ("gear 52px", new Rect(226, 104, 52, 52)), ("hotbar slot 66px", new Rect(380, 628, 66, 66)) })
                l.Add(new TargetCase(n, real.Clone(), r, new Mat(real, r).Clone()));
        }
        var scene = Noise(900, 600, 3);
        var play = FlatButton(new Scalar(40, 140, 230), "PLAY");
        using (var d = new Mat(scene, new Rect(300, 200, 110, 40))) play.CopyTo(d);
        l.Add(new TargetCase("flat button 110x40", scene.Clone(), new Rect(300, 200, 110, 40), play));
        var small = FlatButton(new Scalar(60, 160, 60), "OK", 56, 28);
        using (var d = new Mat(scene, new Rect(500, 400, 56, 28))) small.CopyTo(d);
        l.Add(new TargetCase("small button 56x28", scene.Clone(), new Rect(500, 400, 56, 28), small));
        var icon = new Mat(22, 22, MatType.CV_8UC3, new Scalar(230, 230, 230));
        Cv2.Circle(icon, new Point(11, 11), 8, new Scalar(30, 30, 200), -1); Cv2.Line(icon, new Point(5, 5), new Point(17, 17), Scalar.All(255), 2);
        using (var d = new Mat(scene, new Rect(700, 100, 22, 22))) icon.CopyTo(d);
        l.Add(new TargetCase("tiny icon 22px", scene.Clone(), new Rect(700, 100, 22, 22), icon));
        return l;
    }

    // ---- occluders ----

    public enum Where { Left, Right, Top, Bottom, Center, Corner }

    static Rect Cover(Rect r, Where w, double frac)
    {
        int cw = (int)Math.Round(r.Width * frac), ch = (int)Math.Round(r.Height * frac);
        switch (w)
        {
            case Where.Left: return new Rect(r.X, r.Y, Math.Max(1, cw), r.Height);
            case Where.Right: return new Rect(r.Right - Math.Max(1, cw), r.Y, Math.Max(1, cw), r.Height);
            case Where.Top: return new Rect(r.X, r.Y, r.Width, Math.Max(1, ch));
            case Where.Bottom: return new Rect(r.X, r.Bottom - Math.Max(1, ch), r.Width, Math.Max(1, ch));
            case Where.Corner: { int s = Math.Max(1, (int)Math.Round(Math.Sqrt(frac) * Math.Min(r.Width, r.Height))); return new Rect(r.Right - s, r.Bottom - s, s, s); }
            default: { int s = (int)Math.Round(Math.Sqrt(frac)); double k = Math.Sqrt(frac); int w2 = Math.Max(1, (int)(r.Width * k)), h2 = Math.Max(1, (int)(r.Height * k)); return new Rect(r.X + (r.Width - w2) / 2, r.Y + (r.Height - h2) / 2, w2, h2); }
        }
    }

    public static readonly double[] Fractions = [0.10, 0.25, 0.50, 0.65];

    public static IEnumerable<(string Name, Scalar? Fill)> Occluders()
    {
        yield return ("dark", new Scalar(25, 25, 25));
        yield return ("white", Scalar.All(250));
        yield return ("blue", new Scalar(200, 60, 20));
        yield return ("noise", null);
    }

    /// <summary>A scene where the target sits at a new place with part of it covered.</summary>
    static (Mat Scene, Rect At) Occlude(TargetCase c, Where w, double frac, Scalar? fill, Mat? background = null)
    {
        var scene = c.Frame.Clone();
        using (var blank = new Mat(scene, c.Where)) blank.SetTo(Cv2.Mean(c.Frame));                 // gone from where it was
        var at = new Rect(scene.Width - c.Where.Width - 40 > 300 ? 560 : 40, scene.Height - c.Where.Height - 30 > 100 ? 90 : 20, c.Where.Width, c.Where.Height);
        if (at.Right > scene.Width) at = new Rect(scene.Width - c.Where.Width - 5, at.Y, at.Width, at.Height);
        using (var dst = new Mat(scene, at)) c.Tpl.CopyTo(dst);
        var r = Cover(at, w, frac);
        if (fill is { } f) Cv2.Rectangle(scene, r, f, -1);
        else { using var n = Noise(Math.Max(16, r.Width), Math.Max(16, r.Height), 99); using var roi = new Mat(scene, r); using var nn = new Mat(n, new Rect(0, 0, r.Width, r.Height)); nn.CopyTo(roi); }
        return (scene, at);
    }

    [Fact]
    public void Report_which_occlusions_are_still_found()
    {
        var det = new Detector();
        int total = 0, missed = 0;
        foreach (var c in Cases())
        {
            double thr = det.AutoThreshold(c.Frame, c.Where);
            var byFrac = new Dictionary<double, (int Ok, int N)>();
            var misses = new List<string>();
            foreach (double f in Fractions)
                foreach (Where w in Enum.GetValues<Where>())
                    foreach (var (on, fill) in Occluders())
                    {
                        var (scene, at) = Occlude(c, w, f, fill);
                        using (scene)
                        {
                            var hits = det.Find(scene, c.Tpl, thr, 1, null, occlusion: true, minVisible: 0.25);
                            bool ok = hits.Any(h => Math.Abs(h.Rect.X - at.X) <= 3 && Math.Abs(h.Rect.Y - at.Y) <= 3);
                            var (o, n) = byFrac.GetValueOrDefault(f);
                            byFrac[f] = (o + (ok ? 1 : 0), n + 1);
                            total++; if (!ok) { missed++; misses.Add($"{w}/{on}@{f:P0}"); }
                        }
                    }
            Console.WriteLine($"OCCL {c.Name,-22} thr {thr:0.00} | " + string.Join("  ", byFrac.OrderBy(k => k.Key).Select(k => $"{k.Key:P0}: {k.Value.Ok}/{k.Value.N}")));
            if (misses.Count > 0) Console.WriteLine($"OCCL    missed: {string.Join(", ", misses.Take(14))}{(misses.Count > 14 ? $" (+{misses.Count - 14})" : "")}");
        }
        Console.WriteLine($"OCCL total missed {missed}/{total}");
    }

    [Fact]
    public void Targets_are_found_with_10_and_25_percent_covered_and_a_half_hidden_from_one_side()
    {
        var det = new Detector();
        var bad = new List<string>();
        foreach (var c in Cases().Where(c => !c.Name.StartsWith("tiny")))
        {
            double thr = det.AutoThreshold(c.Frame, c.Where);
            foreach (var (f, sides) in new[] { (0.10, Enum.GetValues<Where>()), (0.25, [Where.Left, Where.Right, Where.Bottom, Where.Corner]), (0.50, new[] { Where.Left, Where.Right }) })
                foreach (var w in sides)
                    foreach (var (on, fill) in Occluders())
                    {
                        var (scene, at) = Occlude(c, w, f, fill);
                        using (scene)
                            if (!det.Find(scene, c.Tpl, thr, 1, null, true, 0.25).Any(h => Math.Abs(h.Rect.X - at.X) <= 3 && Math.Abs(h.Rect.Y - at.Y) <= 3)) bad.Add($"{c.Name}:{w}/{on}@{f:P0}");
                    }
        }
        Assert.True(bad.Count <= 3, string.Join(", ", bad));
    }

    [Fact]
    public void A_covered_target_is_not_invented_where_it_is_absent()
    {
        var det = new Detector();
        foreach (var c in Cases())
        {
            var scene = c.Frame.Clone();
            using (var blank = new Mat(scene, c.Where)) blank.SetTo(Cv2.Mean(c.Frame));
            using (scene) Assert.Empty(det.Find(scene, c.Tpl, det.AutoThreshold(c.Frame, c.Where), 1, null, true, 0.25));
        }
    }
}
