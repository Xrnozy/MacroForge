using System.IO;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// How well a registered target survives being moved and changing appearance a little (hover colour, tint, antialiasing).
/// Uses real UI crops from the Revolution screenshot and the threshold the app would pick for each.
/// </summary>
public class RobustnessTests
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

    public static readonly (string Name, Rect R)[] Crops =
    [
        ("gear", new Rect(226, 104, 52, 52)),
        ("hotbar slot", new Rect(380, 628, 66, 66)),
        ("item icon", new Rect(8, 455, 76, 76)),
        ("honey label", new Rect(345, 26, 70, 30)),
    ];

    static Mat Hue(Mat m, int dh)
    {
        using var hsv = new Mat(); Cv2.CvtColor(m, hsv, ColorConversionCodes.BGR2HSV);
        var ch = hsv.Split();
        using var h = new Mat(); ch[0].ConvertTo(h, MatType.CV_16S); Cv2.Add(h, Scalar.All(dh + 180), h);
        for (int y = 0; y < h.Rows; y++) for (int x = 0; x < h.Cols; x++) ch[0].Set<byte>(y, x, (byte)(h.At<short>(y, x) % 180));
        using var merged = new Mat(); Cv2.Merge(ch, merged);
        foreach (var c in ch) c.Dispose();
        var bgr = new Mat(); Cv2.CvtColor(merged, bgr, ColorConversionCodes.HSV2BGR);
        return bgr;
    }

    static Mat Sat(Mat m, double k)
    {
        using var hsv = new Mat(); Cv2.CvtColor(m, hsv, ColorConversionCodes.BGR2HSV);
        var ch = hsv.Split();
        ch[1].ConvertTo(ch[1], -1, k);
        using var merged = new Mat(); Cv2.Merge(ch, merged);
        foreach (var c in ch) c.Dispose();
        var bgr = new Mat(); Cv2.CvtColor(merged, bgr, ColorConversionCodes.HSV2BGR);
        return bgr;
    }

    static Mat Tint(Mat m, Scalar add) { var r = new Mat(); Cv2.Add(m, add, r); return r; }

    static Mat SubPixel(Mat m)
    {
        using var shifted = new Mat();
        using var t = Mat.FromArray(new double[,] { { 1, 0, 0.5 }, { 0, 1, 0.5 } });
        Cv2.WarpAffine(m, shifted, t, m.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        var r = new Mat(); Cv2.GaussianBlur(shifted, r, new Size(0, 0), 0.7);
        return r;
    }

    public static IEnumerable<(string Name, Func<Mat, Mat> Make)> Variants()
    {
        yield return ("identical", m => m.Clone());
        yield return ("hue +6 deg", m => Hue(m, 3));
        yield return ("hue +15 deg", m => Hue(m, 8));
        yield return ("hue +30 deg", m => Hue(m, 15));
        yield return ("saturation x0.8", m => Sat(m, 0.8));
        yield return ("saturation x1.25", m => Sat(m, 1.25));
        yield return ("brightness x0.88", m => { var r = new Mat(); m.ConvertTo(r, -1, 0.88); return r; });
        yield return ("brightness x1.12", m => { var r = new Mat(); m.ConvertTo(r, -1, 1.12); return r; });
        yield return ("warm tint", m => Tint(m, new Scalar(-14, 4, 16)));
        yield return ("cool tint", m => Tint(m, new Scalar(16, 4, -14)));
        yield return ("sub-pixel shift + blur", SubPixel);
    }

    [Fact]
    public void Report_scores_of_a_moved_target_that_changed_a_little()
    {
        using var frame = RealFrame();
        if (frame == null) return;
        var det = new Detector();
        int failed = 0, total = 0;
        foreach (var (name, r) in Crops)
        {
            using var tpl = new Mat(frame, r).Clone();
            double thr = det.AutoThreshold(frame, r);
            var line = $"ROBUST {name,-12} thr {thr:0.00} |";
            foreach (var (vn, make) in Variants())
            {
                using var variant = make(tpl);
                using var scene = frame.Clone();
                using (var blank = new Mat(scene, r)) blank.SetTo(Cv2.Mean(frame));               // it is no longer where it was
                using (var dst = new Mat(scene, new Rect(700, 250, r.Width, r.Height))) variant.CopyTo(dst);   // ...it moved here
                var hits = det.Search(scene, tpl, thr, 1, null, out double best, occlusion: false);
                bool ok = hits.Any(h => Math.Abs(h.Rect.X - 700) <= 2 && Math.Abs(h.Rect.Y - 250) <= 2);
                total++; if (!ok) failed++;
                line += $" {(ok ? "ok" : "MISS")}({best:0.00})";
            }
            Console.WriteLine(line);
        }
        Console.WriteLine($"ROBUST legend: identical, hue6, hue15, hue30, sat0.8, sat1.25, bri0.88, bri1.12, warm, cool, subpixel;  {failed}/{total} missed");
    }

    // ---- flat UI buttons: solid / gradient fill with text, the usual game and app button ----

    static Mat FlatButton(Scalar fill, string text = "PLAY", Scalar? textColor = null)
    {
        var b = new Mat(40, 110, MatType.CV_8UC3, fill);
        for (int y = 0; y < 40; y++) Cv2.Line(b, new Point(0, y), new Point(109, y), new Scalar(fill.Val0 * (0.8 + 0.2 * y / 40.0), fill.Val1 * (0.8 + 0.2 * y / 40.0), fill.Val2 * (0.8 + 0.2 * y / 40.0)));
        Cv2.Rectangle(b, new Rect(0, 0, 110, 40), new Scalar(fill.Val0 * 0.5, fill.Val1 * 0.5, fill.Val2 * 0.5), 2);
        Cv2.PutText(b, text, new Point(26, 27), HersheyFonts.HersheySimplex, 0.8, textColor ?? Scalar.All(255), 2);
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

    [Fact]
    public void Report_flat_button_that_changes_colour()
    {
        var orange = new Scalar(40, 140, 230);
        using var original = FlatButton(orange);
        var variants = new (string, Func<Mat>)[]
        {
            ("identical", () => original.Clone()),
            ("hover lighter", () => { var r = new Mat(); original.ConvertTo(r, -1, 1.0, 28); return r; }),
            ("pressed darker", () => { var r = new Mat(); original.ConvertTo(r, -1, 0.82); return r; }),
            ("hue +8 deg", () => Hue(original, 4)),
            ("hue +20 deg", () => Hue(original, 10)),
            ("hue +45 deg", () => Hue(original, 22)),
            ("recoloured green", () => FlatButton(new Scalar(70, 190, 70))),
            ("recoloured red", () => FlatButton(new Scalar(50, 50, 215))),
            ("grey (disabled)", () => FlatButton(new Scalar(150, 150, 150), "PLAY", new Scalar(215, 215, 215))),
        };
        var det = new Detector();
        using var reg = Noise(900, 600, 1); using (var d = new Mat(reg, new Rect(300, 200, 110, 40))) original.CopyTo(d);
        double thr = det.AutoThreshold(reg, new Rect(300, 200, 110, 40));
        string line = $"ROBUST flat button thr {thr:0.00} |";
        int missed = 0;
        foreach (var (name, make) in variants)
        {
            using var v = make(); using var scene = Noise(900, 600, 2);
            using (var d = new Mat(scene, new Rect(560, 380, 110, 40))) v.CopyTo(d);
            var hits = det.Search(scene, original, thr, 1, null, out double best, occlusion: false);
            bool ok = hits.Any(h => Math.Abs(h.Rect.X - 560) <= 2 && Math.Abs(h.Rect.Y - 380) <= 2);
            if (!ok) missed++;
            line += $" {name}={(ok ? "ok" : "MISS")}({best:0.00})";
        }
        Console.WriteLine(line);
        Console.WriteLine($"ROBUST flat button: {missed}/{variants.Length} missed");
    }

    // ---- the whole pipeline: watcher, thresholds as the app picks them, decoy buttons on screen, target moving and recolouring ----

    static readonly DesktopInfo Info = new(new MacroStudio.Model.ScreenRect(0, 0, 3840, 1080),
        [new MacroStudio.Model.ScreenRect(0, 0, 1920, 1080), new MacroStudio.Model.ScreenRect(1920, 0, 1920, 1080)]);

    sealed class Rig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "ms-rob-" + Guid.NewGuid().ToString("N"));
        public readonly Mat Desk = Noise(3840, 1080, 5);
        public readonly TargetStore Store;
        public readonly Target T;
        public readonly Rect Home = new(300, 200, 110, 40);
        public readonly Mat Original = FlatButton(new Scalar(40, 140, 230));
        public DateTime Clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public Rig(bool decoys, bool twin = false)
        {
            Store = new TargetStore(Dir);
            Paste(Original, Home.X, Home.Y);
            if (decoys)
            {
                using var b1 = FlatButton(new Scalar(40, 140, 230), "PLAT"); Paste(b1, 700, 200);          // same colour, one letter different
                using var b2 = FlatButton(new Scalar(40, 140, 230), "PLAN"); Paste(b2, 300, 600);
                using var b3 = FlatButton(new Scalar(70, 190, 70), "PLAY"); Paste(b3, 1300, 700);          // same text, other colour
                if (twin) { using var b4 = FlatButton(new Scalar(60, 160, 235), "PLAY"); Paste(b4, 2300, 300); }   // same text, nearly the same colour: a near twin
            }
            double thr = Math.Round(new Detector().AutoThreshold(Desk, Home), 2);
            T = Store.Add("Play", Original, 96, Info.Virtual, thr, new MacroStudio.Model.ScreenRect(Home.X, Home.Y, Home.Width, Home.Height));
        }

        public void Paste(Mat img, int x, int y) { using var d = new Mat(Desk, new Rect(x, y, img.Width, img.Height)); img.CopyTo(d); }
        public void Wipe(Rect r) => Cv2.Rectangle(Desk, r, new Scalar(90, 120, 60), -1);
        public TargetWatcher Watcher() => new(Store, r => new Mat(Desk, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => Clock);
        public void Dispose() { Desk.Dispose(); Original.Dispose(); if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
    }

    /// <summary>Run the scheduler in simulated time until the target is reported at <paramref name="where"/>; returns simulated ms, or -1 if it never was within the limit.</summary>
    static double TimeToFind(Rig rig, TargetWatcher w, Rect where, double limitMs = 12000, int tol = 3)
    {
        double t = 0;
        while (t < limitMs)
        {
            double d = w.Tick(); t += d; rig.Clock = rig.Clock.AddMilliseconds(d);
            if (w.Get(rig.T.Id)?.Hit is { } h && Math.Abs(h.Rect.X - where.X) <= tol && Math.Abs(h.Rect.Y - where.Y) <= tol) return t;
        }
        return -1;
    }

    [Fact]
    public void Report_watcher_follows_a_moved_recoloured_button_among_decoys()
    {
        var variants = new (string, Func<Mat, Mat>)[]
        {
            ("same", m => m.Clone()),
            ("hover lighter", m => { var r = new Mat(); m.ConvertTo(r, -1, 1.0, 28); return r; }),
            ("pressed darker", m => { var r = new Mat(); m.ConvertTo(r, -1, 0.82); return r; }),
            ("hue +8", m => Hue(m, 4)),
            ("hue +20", m => Hue(m, 10)),
            ("warm tint", m => Tint(m, new Scalar(-14, 4, 16))),
        };
        foreach (bool decoys in new[] { false, true })
        {
            using var probe = new Rig(decoys);
            var line = $"ROBUST watcher {(decoys ? "with decoys" : "no decoys  ")} thr {probe.T.Threshold:0.00} |";
            int missed = 0;
            foreach (var (name, make) in variants)
            {
                using var rig = new Rig(decoys);
                using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
                if (TimeToFind(rig, w, rig.Home) < 0) { line += $" {name}=NOT-FOUND-AT-HOME"; missed++; continue; }
                rig.Wipe(rig.Home);
                var to = new Rect(2600, 700, 110, 40);
                using var v = make(rig.Original); rig.Paste(v, to.X, to.Y);                                // moved to the other monitor, and changed
                double ms = TimeToFind(rig, w, to);
                if (ms < 0) { missed++; var got = w.Get(rig.T.Id)?.Hit; line += $" [{name} reported at {(got is { } g ? g.Rect.X + "," + g.Rect.Y + " score " + g.Score.ToString("0.000") : "nothing")}]"; }
                line += $" {name}={(ms < 0 ? "MISS" : ms.ToString("0") + "ms")}";
            }
            Console.WriteLine(line);
            Console.WriteLine($"ROBUST watcher {(decoys ? "with decoys" : "no decoys")}: {missed}/{variants.Length} not re-found within 12 s");
        }
    }

    [Fact]
    public void Report_watcher_follows_a_button_that_is_dragged_across_the_screen()
    {
        foreach (int step in new[] { 40, 120, 260, 600 })
        {
            using var rig = new Rig(decoys: true);
            using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
            TimeToFind(rig, w, rig.Home);
            var pos = rig.Home; int lostTicks = 0, ticks = 0; bool everLost = false;
            for (int i = 0; i < 30 && pos.X + step < 3600; i++)
            {
                rig.Wipe(pos);
                pos = new Rect(pos.X + step, pos.Y + (i % 2 == 0 ? 6 : -6), pos.Width, pos.Height);
                rig.Paste(rig.Original, pos.X, pos.Y);                                                       // one drag step per scheduler tick
                double d = w.Tick(); rig.Clock = rig.Clock.AddMilliseconds(d); ticks++;
                var hit = w.Get(rig.T.Id)?.Hit;
                if (hit is null) { lostTicks++; everLost = true; }
            }
            double ms = TimeToFind(rig, w, pos);
            Console.WriteLine($"ROBUST drag {step,3}px per tick: lost on {lostTicks}/{ticks} ticks, re-found at the end after {(ms < 0 ? "NEVER" : ms.ToString("0") + " ms")}{(everLost ? "" : " (never lost)")}");
        }
    }

    [Fact]
    public void Report_colour_distances_of_the_variants()
    {
        var orange = new Scalar(40, 140, 230);
        using var original = FlatButton(orange);
        var mean0 = Cv2.Mean(original);
        var variants = new (string, Func<Mat>)[]
        {
            ("hover lighter", () => { var r = new Mat(); original.ConvertTo(r, -1, 1.0, 28); return r; }),
            ("pressed darker", () => { var r = new Mat(); original.ConvertTo(r, -1, 0.82); return r; }),
            ("hue +8", () => Hue(original, 4)),
            ("hue +20", () => Hue(original, 10)),
            ("hue +45", () => Hue(original, 22)),
            ("warm tint", () => Tint(original, new Scalar(-14, 4, 16))),
            ("cool tint", () => Tint(original, new Scalar(16, 4, -14))),
            ("sat x0.8", () => Sat(original, 0.8)),
            ("recoloured green", () => FlatButton(new Scalar(70, 190, 70))),
            ("recoloured red", () => FlatButton(new Scalar(50, 50, 215))),
            ("grey", () => FlatButton(new Scalar(150, 150, 150), "PLAY", new Scalar(215, 215, 215))),
            ("near-orange decoy", () => FlatButton(new Scalar(60, 160, 235))),
        };
        var line = "COLOR distance from the registered orange |";
        foreach (var (n, make) in variants) { using var v = make(); line += $" {n}={ColorMath.Distance(mean0, Cv2.Mean(v)):0}"; }
        Console.WriteLine(line);
    }

    // ---- assertions (these must keep passing) ----

    [Fact]
    public void A_moved_button_that_changed_colour_a_little_is_found_again_even_with_similar_buttons_on_screen()
    {
        var slight = new (string, Func<Mat, Mat>)[]
        {
            ("hover lighter", m => { var r = new Mat(); m.ConvertTo(r, -1, 1.0, 28); return r; }),
            ("pressed darker", m => { var r = new Mat(); m.ConvertTo(r, -1, 0.82); return r; }),
            ("hue +8 deg", m => Hue(m, 4)),
            ("hue +20 deg", m => Hue(m, 10)),
            ("warm tint", m => Tint(m, new Scalar(-14, 4, 16))),
            ("cool tint", m => Tint(m, new Scalar(16, 4, -14))),
            ("saturation x0.8", m => Sat(m, 0.8)),
        };
        foreach (var (name, make) in slight)
        {
            using var rig = new Rig(decoys: true);                       // letter-different and other-colour look-alikes on screen
            using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
            Assert.True(TimeToFind(rig, w, rig.Home) >= 0, "not found at its starting place");
            rig.Wipe(rig.Home);
            var to = new Rect(2600, 700, 110, 40);
            using var v = make(rig.Original); rig.Paste(v, to.X, to.Y);
            double ms = TimeToFind(rig, w, to);
            Assert.True(ms >= 0 && ms < 3000, $"{name}: re-found after {ms} ms");
        }
    }

    [Fact]
    public void A_clearly_different_colour_is_not_the_same_target_and_the_tolerance_can_be_widened()
    {
        using var rig = new Rig(decoys: false);
        using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
        Assert.True(TimeToFind(rig, w, rig.Home) >= 0);
        rig.Wipe(rig.Home);
        var to = new Rect(2600, 700, 110, 40);
        using var green = FlatButton(new Scalar(70, 190, 70)); rig.Paste(green, to.X, to.Y);     // the same shape, now green
        Assert.True(TimeToFind(rig, w, to, 4000) < 0, "a green button must not be taken for the orange one with the default tolerance");

        rig.T.ColorTolerance = 250;                                                              // user: "ignore colour for this target"
        using var h2 = w.Watch([rig.T]);
        Assert.True(TimeToFind(rig, w, to, 8000) >= 0, "with colour ignored the recoloured button should match");
    }

    [Fact]
    public void A_button_dragged_past_a_look_alike_is_not_swapped_for_it()
    {
        foreach (int step in new[] { 40, 120, 260, 600 })
        {
            using var rig = new Rig(decoys: true);
            using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
            Assert.True(TimeToFind(rig, w, rig.Home) >= 0);
            var pos = rig.Home;
            for (int i = 0; i < 30 && pos.X + step < 3600; i++)
            {
                rig.Wipe(pos);
                pos = new Rect(pos.X + step, pos.Y + (i % 2 == 0 ? 6 : -6), pos.Width, pos.Height);
                rig.Paste(rig.Original, pos.X, pos.Y);
                double d = w.Tick(); rig.Clock = rig.Clock.AddMilliseconds(d);
            }
            Assert.True(TimeToFind(rig, w, pos) >= 0, $"{step}px per tick: the target was not found at its final place");
        }
    }

    [Fact]
    public void A_near_twin_is_reported_as_ambiguous_and_the_saved_surroundings_settle_it()
    {
        // two near-identical buttons: without surroundings nothing can tell them apart, so the engine must say so
        using (var rig = new Rig(decoys: true, twin: true))
        {
            using var w = rig.Watcher(); using var h = w.Watch([rig.T]);
            TimeToFind(rig, w, rig.Home);
            Assert.True(w.Get(rig.T.Id)?.Ambiguous, "two matches that cannot be told apart must be flagged");
        }

        // with the surroundings saved at registration the right one is chosen and nothing is ambiguous
        using (var rig = new Rig(decoys: true, twin: true))
        {
            var ring = new Rect(rig.Home.X - 24, rig.Home.Y - 24, rig.Home.Width + 48, rig.Home.Height + 48);
            using var context = new Mat(rig.Desk, ring).Clone();
            var t = rig.Store.Add("Play+ring", rig.Original, 96, Info.Virtual, rig.T.Threshold, new ScreenRect(rig.Home.X, rig.Home.Y, rig.Home.Width, rig.Home.Height), context, 24);
            using var w = rig.Watcher(); using var h = w.Watch([t]);
            double ms = 0;
            while (ms < 6000) { double d = w.Tick(); ms += d; rig.Clock = rig.Clock.AddMilliseconds(d); if (w.Get(t.Id)?.Hit is not null) break; }
            var hit = w.Get(t.Id)!;
            Assert.NotNull(hit.Hit);
            Assert.Equal((rig.Home.X, rig.Home.Y), (hit.Hit!.Value.Rect.X, hit.Hit.Value.Rect.Y));
            Assert.False(hit.Ambiguous);
        }
    }
}
