using MacroStudio.Vision;

namespace MacroStudio.Tests;

/// <summary>Not a unit test: runs the real detector on the real screen against the saved targets. Only with MACRO_LIVE=1.</summary>
public class LiveScreenProbe
{
    [Fact]
    public void Where_does_the_detector_find_each_saved_target_on_this_screen_right_now()
    {
        if (Environment.GetEnvironmentVariable("MACRO_LIVE") != "1") return;
        var store = new TargetStore(); // the app's own library
        using var watcher = new TargetWatcher(store, threaded: false);
        var all = store.List();
        using var _ = watcher.Watch(all);
        foreach (var t in all) watcher.Prioritise(t.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 25000) { watcher.Tick(); Thread.Sleep(30); }
        foreach (var t in all)
        {
            var s = watcher.Get(t.Id);
            string cap = t.CaptureRect.Width > 0 ? $"captured at ({t.CaptureRect.X},{t.CaptureRect.Y}) {t.CaptureRect.Width}x{t.CaptureRect.Height}" : "no capture position";
            Console.WriteLine(s?.Hit is { } h
                ? $"LIVE {t.Name}: FOUND ({h.Rect.X},{h.Rect.Y}) {h.Rect.Width}x{h.Rect.Height} score {h.Score:0.00} thr {t.Threshold:0.00} {h.Method}{(s.Ambiguous ? " AMBIGUOUS" : "")} | {cap}"
                : $"LIVE {t.Name}: NOT FOUND thr {t.Threshold:0.00} occl={t.Occlusion} colTol={t.ColorTolerance} | {s?.Note} | {cap}");
        }
    }

    [Fact]
    public void Best_raw_match_of_every_target_on_each_monitor()
    {
        if (Environment.GetEnvironmentVariable("MACRO_LIVE") != "1") return;
        var store = new TargetStore();
        var desk = MacroStudio.Native.Screens.Current();
        for (int m = 0; m < desk.Monitors.Count; m++)
        {
            using var frame = ScreenCapture.Capture(desk.Monitors[m]);
            using var gray = new OpenCvSharp.Mat(); OpenCvSharp.Cv2.CvtColor(frame, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
            foreach (var t in store.List())
            {
                using var img = store.Image(t.Id);
                using var tg = new OpenCvSharp.Mat(); OpenCvSharp.Cv2.CvtColor(img, tg, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                if (gray.Width < tg.Width || gray.Height < tg.Height) continue;
                using var map = Detector.ScoreMap(gray, tg);
                OpenCvSharp.Cv2.MinMaxLoc(map, out _, out double mx, out _, out OpenCvSharp.Point loc);
                Console.WriteLine($"LIVE2 monitor {m + 1}: {t.Name} best {mx:0.00} (thr {t.Threshold:0.00}) at ({loc.X + desk.Monitors[m].X},{loc.Y + desk.Monitors[m].Y}) size {img.Width}x{img.Height}");
            }
        }
    }
}
