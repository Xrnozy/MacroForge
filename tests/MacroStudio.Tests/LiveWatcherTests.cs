using System.Diagnostics;
using System.IO;
using MacroStudio.Native;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>Real GDI capture, real watcher thread, real CPU. Skipped when there is no usable desktop.</summary>
public class LiveWatcherTests
{
    [Fact]
    public void Real_screen_target_is_found_and_tracked_at_low_cpu()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return;
        using var frame = ScreenCapture.Capture();

        // a textured block that is unique on screen (striped / repeating areas match everywhere)
        using var gray = new Mat(); Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        var blocks = new List<(Rect R, double Sd)>();
        for (int y = 0; y + 56 < frame.Height; y += 56)
            for (int x = 0; x + 72 < frame.Width; x += 72)
            {
                using var b = new Mat(gray, new Rect(x, y, 72, 56));
                Cv2.MeanStdDev(b, out _, out var sd);
                if (sd.Val0 >= 12) blocks.Add((new Rect(x, y, 72, 56), sd.Val0));
            }
        var probe = new Detector();
        Rect pick = default; bool ok = false;
        foreach (var (r, _) in blocks.OrderByDescending(b => b.Sd).Take(40))
        {
            using var cand = new Mat(frame, r).Clone();
            if (probe.Find(frame, cand, 0.9, occlusion: false).Count == 1) { pick = r; ok = true; break; }
        }
        if (!ok) return;

        var dir = Path.Combine(Path.GetTempPath(), "ms-livew-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TargetStore(dir);
            using var tpl = new Mat(frame, pick).Clone();
            var t = store.Add("live", tpl, Screens.CurrentEnv().Dpi, desk.Virtual, Math.Round(probe.AutoThreshold(frame, pick), 2));
            using var w = new TargetWatcher(store);
            using var h = w.Watch([t]);

            var sw = Stopwatch.StartNew();
            WatchState? s = null;
            while (sw.ElapsedMilliseconds < 5000 && (s = w.Get(t.Id))?.Hit == null) Thread.Sleep(20);
            long foundMs = sw.ElapsedMilliseconds;
            Assert.NotNull(s?.Hit);
            Assert.True(Math.Abs(s!.Hit!.Value.Rect.X - desk.Virtual.X - pick.X) <= 2 && Math.Abs(s.Hit.Value.Rect.Y - desk.Virtual.Y - pick.Y) <= 2,
                $"found at {s.Hit.Value.Rect.X},{s.Hit.Value.Rect.Y}, expected {pick.X + desk.Virtual.X},{pick.Y + desk.Virtual.Y}");

            Thread.Sleep(500); // let it settle into tracking
            double baseline = 0; // (host idle is reported separately by the absent-target test)
            var p = Process.GetCurrentProcess();
            double cpu0 = p.TotalProcessorTime.TotalMilliseconds; var wall = Stopwatch.StartNew();
            Thread.Sleep(3000);
            p.Refresh();
            double cpuPct = Math.Max(0, (p.TotalProcessorTime.TotalMilliseconds - cpu0) / wall.Elapsed.TotalMilliseconds * 100 - baseline);
            Console.WriteLine($"LIVE watcher, 1 target on the real screen: found in {foundMs} ms, tracking costs {cpuPct:0.0}% of one core (host idle {baseline:0.0}% subtracted), {w.Stats.Captures} captures, {w.Stats.WorkMs:0.0} ms/tick, delay {w.Stats.DelayMs:0} ms");
            Assert.True(cpuPct < 25, $"{cpuPct:0.0}% of a core while tracking one target");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }


    /// <summary>Process CPU over a window, as a percentage of one core.</summary>
    static double CpuPercent(int ms)
    {
        var p = Process.GetCurrentProcess();
        double c0 = p.TotalProcessorTime.TotalMilliseconds; var w = Stopwatch.StartNew();
        Thread.Sleep(ms); p.Refresh();
        return (p.TotalProcessorTime.TotalMilliseconds - c0) / w.Elapsed.TotalMilliseconds * 100;
    }

    [Fact]
    public void A_target_that_is_not_on_screen_costs_little_when_the_screen_is_quiet()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return;
        var dir = Path.Combine(Path.GetTempPath(), "ms-livew-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var warm = ScreenCapture.Capture(new MacroStudio.Model.ScreenRect(0, 0, 200, 200))) { } // load native libs now, not inside the measurement
            Thread.Sleep(2500);
            double host = CpuPercent(2000);                                   // the test host with nothing running
            var store = new TargetStore(dir);
            using var noise = new Mat(56, 72, MatType.CV_8UC3); Cv2.Randu(noise, Scalar.All(0), Scalar.All(255));
            var t = store.Add("never-on-screen", noise, Screens.CurrentEnv().Dpi, desk.Virtual, 0.85);
            using var w = new TargetWatcher(store);
            using var h = w.Watch([t]);
            Thread.Sleep(4000);                                               // light scan, then the expensive stages run their cycle
            double busy = CpuPercent(3000);
            Console.WriteLine($"LIVE absent target: total {busy:0.0}% of one core, host alone {host:0.0}%, so watcher ~{Math.Max(0, busy - host):0.0}% (ticks {w.Stats.WorkMs:0} ms, delay {w.Stats.DelayMs:0} ms, {w.Stats.Samples} samples, {w.Stats.Captures} captures, {w.Stats.LightScans} light, {w.Stats.DeepScans} deep)");
            Assert.True(busy - host < 35, $"{busy - host:0.0}% of a core searching for an absent target");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
