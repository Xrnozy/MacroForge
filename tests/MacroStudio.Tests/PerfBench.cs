using System.Diagnostics;
using System.IO;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>Shared synthetic multi-target desktop used by the efficiency tests and benchmarks.</summary>
public static class Desktop
{
    public const int W = 3840, H = 1080;

    /// <summary>A detailed 3840x1080 "desktop" made from the real game screenshot (or noise when it is not available).</summary>
    public static Mat Make()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Revolution", "logs", "debug_images", "inventory_detect.png");
            if (!File.Exists(p)) continue;
            using var real = Cv2.ImDecode(File.ReadAllBytes(p), ImreadModes.Color);
            var m = new Mat();
            Cv2.Resize(real, m, new Size(W, H));
            return m;
        }
        var n = new Mat(H / 8, W / 8, MatType.CV_8UC3);
        Cv2.Randu(n, Scalar.All(30), Scalar.All(200));
        var big = new Mat(); Cv2.Resize(n, big, new Size(W, H), 0, 0, InterpolationFlags.Cubic);
        return big;
    }

    /// <summary>Four crops that are on the desktop and four that are not (cut from a different noise image).</summary>
    public static (List<Mat> Present, List<Rect> Where, List<Mat> Absent) Targets(Mat desktop)
    {
        // four distinctive, unique, well-separated blocks (flat or repeating patches would match in many places)
        using var gray = new Mat(); Cv2.CvtColor(desktop, gray, ColorConversionCodes.BGR2GRAY);
        var blocks = new List<(Rect R, double Sd)>();
        for (int y = 40; y + 56 < desktop.Height - 40; y += 28)
            for (int x = 40; x + 64 < desktop.Width - 40; x += 32)
            {
                using var b = new Mat(gray, new Rect(x, y, 64, 56));
                Cv2.MeanStdDev(b, out _, out var sd);
                blocks.Add((new Rect(x, y, 64, 56), sd.Val0));
            }
        var probe = new Detector();
        var where = new List<Rect>();
        foreach (var (r, _) in blocks.OrderByDescending(b => b.Sd))
        {
            if (where.Count == 4) break;
            if (where.Any(w => Math.Abs(w.X - r.X) < 600 && Math.Abs(w.Y - r.Y) < 500)) continue;
            using var cand = new Mat(desktop, r).Clone();
            if (probe.Find(desktop, cand, 0.9, occlusion: false).Count == 1) where.Add(r);
        }
        var present = where.Select(r => new Mat(desktop, r).Clone()).ToList();
        using var other = new Mat(270, 480, MatType.CV_8UC3);
        Cv2.Randu(other, Scalar.All(0), Scalar.All(255));
        using var otherBig = new Mat(); Cv2.Resize(other, otherBig, new Size(1920, 1080), 0, 0, InterpolationFlags.Cubic);
        var absent = Enumerable.Range(0, 4).Select(i => new Mat(otherBig, new Rect(100 + i * 300, 200 + i * 90, 72, 56)).Clone()).ToList();
        return (present, where, absent);
    }
}

public class PerfBench
{
    static double CpuMs() => Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;

    /// <summary>Baseline of the per-frame API: every target is searched independently on every frame.</summary>
    [Fact]
    public void Baseline_per_frame_cost_with_eight_targets()
    {
        using var desk = Desktop.Make();
        var (present, where, absent) = Desktop.Targets(desk);
        var det = new Detector();
        var all = present.Concat(absent).ToList();
        string Run(string label, int ticks)
        {
            double c0 = CpuMs(); var sw = Stopwatch.StartNew();
            for (int t = 0; t < ticks; t++)
                for (int i = 0; i < all.Count; i++) det.FindTracked("t" + i, desk, all[i], 0.85);
            return $"{label}: {sw.ElapsedMilliseconds / (double)ticks:0} ms wall, {(CpuMs() - c0) / ticks:0} ms cpu per tick";
        }
        Console.WriteLine("BENCH " + Run("first tick (nothing known)", 1));
        Console.WriteLine("BENCH " + Run("steady tick (4 tracked, 4 absent)", 3));
        for (int i = 0; i < present.Count; i++)
            Assert.NotEmpty(det.FindTracked("t" + i, desk, present[i], 0.85));
    }
}
