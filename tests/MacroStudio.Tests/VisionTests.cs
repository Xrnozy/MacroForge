using System.Diagnostics;
using System.IO;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

public class VisionTests
{
    // ---- synthetic scenes ----

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

    static Mat Scaled(Mat m, double s)
    {
        var r = new Mat();
        Cv2.Resize(m, r, new Size((int)Math.Round(m.Width * s), (int)Math.Round(m.Height * s)), 0, 0, InterpolationFlags.Linear);
        return r;
    }

    [Fact]
    public void Finds_template_at_exact_pixel()
    {
        using var frame = Background(900, 600, 1); using var btn = Button();
        Paste(frame, btn, 300, 200);
        var r = new Detector().Find(frame, btn, 0.85);
        Assert.Single(r);
        Assert.Equal((300, 200), (r[0].Rect.X, r[0].Rect.Y));
        Assert.True(r[0].Score > 0.99);
    }

    [Fact]
    public void Finds_it_after_it_moves_to_a_new_place_and_background()
    {
        using var btn = Button();
        var det = new Detector();
        using var f1 = Background(900, 600, 1); Paste(f1, btn, 300, 200);
        Assert.Equal(300, det.FindTracked("t", f1, btn, 0.85)[0].Rect.X);
        using var f2 = Background(900, 600, 2); Paste(f2, btn, 780, 40);            // far outside the tracking window
        var r = det.FindTracked("t", f2, btn, 0.85);
        Assert.Equal((780, 40), (r[0].Rect.X, r[0].Rect.Y));
        using var f3 = Background(900, 600, 3); Paste(f3, btn, 790, 55);            // nearby: tracking fast path
        Assert.Equal((790, 55), (det.FindTracked("t", f3, btn, 0.85)[0].Rect.X, det.FindTracked("t", f3, btn, 0.85)[0].Rect.Y));
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(1.12)]
    [InlineData(0.8)]
    public void Finds_it_when_the_ui_is_scaled(double s)
    {
        using var btn = Button(); using var big = Scaled(btn, s);
        using var frame = Background(900, 600, 4);
        Paste(frame, big, 400, 300);
        var r = new Detector().Find(frame, btn, 0.8);
        Assert.NotEmpty(r);
        Assert.InRange(r[0].Center.X, 400 + big.Width / 2 - 3, 400 + big.Width / 2 + 3);
        Assert.InRange(r[0].Center.Y, 300 + big.Height / 2 - 3, 300 + big.Height / 2 + 3);
    }

    [Theory]
    [InlineData(1.15)]
    [InlineData(0.85)]
    public void Tolerates_brightness_change(double gain)
    {
        using var btn = Button(); using var frame = Background(900, 600, 5);
        Paste(frame, btn, 200, 150);
        using var lit = new Mat();
        frame.ConvertTo(lit, -1, gain);
        var r = new Detector().Find(lit, btn, 0.85);
        Assert.NotEmpty(r);
        Assert.Equal((200, 150), (r[0].Rect.X, r[0].Rect.Y));
    }

    [Fact]
    public void Returns_every_instance()
    {
        using var btn = Button(); using var frame = Background(900, 600, 6);
        Paste(frame, btn, 100, 100); Paste(frame, btn, 500, 400);
        var r = new Detector().Find(frame, btn, 0.9);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Absent_button_and_lookalike_text_are_not_detected()
    {
        using var btn = Button(); using var other = Button("STOP", new Scalar(60, 200, 60));
        using var frame = Background(900, 600, 7);
        Assert.Empty(new Detector().Find(frame, btn, 0.85));
        Paste(frame, other, 300, 300);
        Assert.Empty(new Detector().Find(frame, btn, 0.85));
    }

    [Fact]
    public void Flat_template_does_not_crash_and_is_found()
    {
        using var frame = Background(600, 400, 8);
        using var flat = new Mat(40, 40, MatType.CV_8UC3, new Scalar(30, 30, 220));
        Paste(frame, flat, 250, 120);
        var r = new Detector().Find(frame, flat, 0.9);
        Assert.NotEmpty(r);
        Assert.Equal((250, 120), (r[0].Rect.X, r[0].Rect.Y));
    }

    [Fact]
    public void Auto_threshold_sits_between_lookalike_and_perfect()
    {
        using var btn = Button(); using var twin = Button("PLAT");
        using var frame = Background(900, 600, 9);
        Paste(frame, btn, 300, 200); Paste(frame, twin, 600, 400);
        var det = new Detector();
        double t = det.AutoThreshold(frame, new Rect(300, 200, 80, 32));
        Assert.InRange(t, 0.70, 0.95);
        Assert.NotEmpty(det.Find(frame, btn, t));
    }

    [Fact]
    public void Full_hd_frame_search_is_fast()
    {
        using var btn = Button(); using var frame = Background(1920, 1080, 10);
        Paste(frame, btn, 1500, 800);
        var det = new Detector();
        det.Find(frame, btn, 0.85); // warm-up
        var sw = Stopwatch.StartNew();
        var r = det.Find(frame, btn, 0.85);
        sw.Stop();
        Console.WriteLine($"1080p full-frame find: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1500, r[0].Rect.X);
        Assert.True(sw.ElapsedMilliseconds < 150, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Target_store_round_trips_pixels_and_metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-targets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TargetStore(dir);
            using var btn = Button();
            var t = store.Add("Play button", btn, 144, new MacroStudio.Model.ScreenRect(0, 0, 3840, 1080), 0.82);
            var back = Assert.Single(store.List());
            Assert.Equal(("Play button", 144, 0.82), (back.Name, back.CaptureDpi, back.Threshold));
            using var diff = new Mat(); Cv2.Absdiff(store.Image(t.Id), btn, diff);
            Assert.Equal(0, Cv2.CountNonZero(diff.Reshape(1)));
            store.Delete(t.Id);
            Assert.Empty(store.List());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- occlusion ----

    static void Cover(Mat frame, Rect r, Scalar? c = null) => Cv2.Rectangle(frame, r, c ?? new Scalar(20, 20, 20), -1);

    [Theory]
    [InlineData(56, 0, 24, 32)]    // right 30% hidden
    [InlineData(28, 0, 22, 32)]    // a middle strip hidden
    [InlineData(0, 16, 80, 16)]    // bottom half hidden
    public void Partly_covered_button_is_found_by_part_voting_but_not_by_exact_match(int cx, int cy, int cw, int ch)
    {
        using var btn = Button(); using var frame = Background(900, 600, 11);
        Paste(frame, btn, 300, 200);
        Cover(frame, new Rect(300 + cx, 200 + cy, cw, ch));
        var det = new Detector();
        Assert.Empty(det.Find(frame, btn, 0.85, occlusion: false));
        var r = det.Find(frame, btn, 0.85);
        Assert.NotEmpty(r);
        Assert.Equal("patches", r[0].Method);
        Assert.InRange(r[0].Rect.X, 298, 302); Assert.InRange(r[0].Rect.Y, 198, 202);
        Assert.InRange(r[0].Visible, 0.4, 0.95);
    }

    [Fact]
    public void Almost_fully_covered_button_is_not_reported()
    {
        using var btn = Button(); using var frame = Background(900, 600, 12);
        Paste(frame, btn, 300, 200);
        Cover(frame, new Rect(300, 200, 80, 28));                       // only a 4 px sliver left
        var dd = new Detector().Find(frame, btn, 0.85); Console.WriteLine("SLIVER " + string.Join(";", dd.Select(x => $"{x.Method} {x.Rect.X},{x.Rect.Y} {x.Score:0.00} v{x.Visible:0.00}"))); Assert.Empty(dd);
    }

    [Fact]
    public void Occlusion_stages_do_not_invent_targets()
    {
        using var btn = Button(); using var other = Button("STOP", new Scalar(60, 200, 60));
        var det = new Detector();
        for (int seed = 20; seed < 26; seed++)
        {
            using var frame = Background(900, 600, seed);
            Assert.Empty(det.Find(frame, btn, 0.85));
            Paste(frame, other, 300, 300);
            Assert.Empty(det.Find(frame, btn, 0.85));                   // same shape, different colour and text
        }
    }

    [Fact]
    public void Absent_target_with_all_stages_still_answers_quickly_on_full_hd()
    {
        using var btn = Button(); using var frame = Background(1920, 1080, 30);
        var det = new Detector();
        det.Find(frame, btn, 0.85);
        var sw = Stopwatch.StartNew();
        var r = det.Find(frame, btn, 0.85);
        sw.Stop();
        Console.WriteLine($"1080p absent target, all stages: {sw.ElapsedMilliseconds} ms");
        Assert.Empty(r);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData(226, 104, 52, 52)]  // settings gear
    [InlineData(380, 628, 66, 66)]  // hotbar slot
    public void Real_ui_element_is_found_with_a_third_of_it_covered(int x, int y, int w, int h)
    {
        using var frame = RealFrame();
        if (frame == null) return;
        using var tpl = new Mat(frame, new Rect(x, y, w, h)).Clone();
        using var seen = frame.Clone();
        Cover(seen, new Rect(x + w * 2 / 3, y, w - w * 2 / 3, h), new Scalar(200, 30, 200)); // an obstacle over the right third
        var det = new Detector();
        Assert.Empty(det.Find(seen, tpl, 0.9, occlusion: false).Where(d => Math.Abs(d.Rect.X - x) <= 2 && Math.Abs(d.Rect.Y - y) <= 2));
        var r = det.Find(seen, tpl, 0.9);
        Assert.Contains(r, d => Math.Abs(d.Center.X - (x + w / 2)) <= 4 && Math.Abs(d.Center.Y - (y + h / 2)) <= 4);
    }

    [Fact]
    public void Rotated_resized_and_partly_covered_textured_icon_is_found_by_features()
    {
        using var frame = RealFrame();
        if (frame == null) return;
        var crop = new Rect(8, 455, 76, 76);                             // "Broken Drive" icon: colourful and detailed
        using var tpl = new Mat(frame, crop).Clone();
        using var seen = Background(1280, 729, 40);                      // unrelated scene
        using var rot = Cv2.GetRotationMatrix2D(new Point2f(crop.Width / 2f, crop.Height / 2f), 12, 1.2);
        rot.Set(0, 2, rot.At<double>(0, 2) + crop.Width * 0.4); rot.Set(1, 2, rot.At<double>(1, 2) + crop.Height * 0.4); // keep the whole rotated icon on the canvas
        using var warped = new Mat();
        Cv2.WarpAffine(tpl, warped, rot, new Size((int)(crop.Width * 1.8), (int)(crop.Height * 1.8)), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(255, 255, 255));
        Paste(seen, warped, 600, 300);
        Cover(seen, new Rect(600 + 98, 300 + 20, 38, 96), new Scalar(10, 10, 10)); // an obstacle over the icon's right edge
        var r = new Detector().Find(seen, tpl, 0.9);
        Assert.NotEmpty(r);
        Assert.Equal("features", r[0].Method);
        // the target's centre maps through the same rotation
        var c = rot.At<double>(0, 0) * crop.Width / 2 + rot.At<double>(0, 1) * crop.Height / 2 + rot.At<double>(0, 2);
        var d = rot.At<double>(1, 0) * crop.Width / 2 + rot.At<double>(1, 1) * crop.Height / 2 + rot.At<double>(1, 2);
        Assert.InRange(r[0].Center.X, 600 + c - 10, 600 + c + 10);
        Assert.InRange(r[0].Center.Y, 300 + d - 10, 300 + d + 10);
    }

    [Fact]
    public void Region_picker_bitmaps_for_every_monitor_slice_of_a_real_capture_do_not_crash()
    {
        // Regression: BitmapSource.Create over a sub-region Mat used step*height bytes and read past the end of the
        // capture for the right-most / bottom-most monitor (access violation, process crash).
        var desk = MacroStudio.Native.Screens.Current();
        if (desk.Virtual.Width == 0) return;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                using var frame = ScreenCapture.Capture();
                foreach (var m in desk.Monitors)
                {
                    using var sub = new Mat(frame, new Rect(m.X - desk.Virtual.X, m.Y - desk.Virtual.Y, m.Width, m.Height));
                    var bmp = MacroStudio.Views.Theme.ToBitmap(sub);
                    Assert.Equal((m.Width, m.Height), (bmp.PixelWidth, bmp.PixelHeight));
                }
                using var corner = new Mat(frame, new Rect(frame.Width - 50, frame.Height - 30, 50, 30)); // last rows of the buffer
                Assert.Equal(50, MacroStudio.Views.Theme.ToBitmap(corner).PixelWidth);
            }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw new Exception("bitmap conversion failed", err);
    }

    [Fact]
    public void Feature_stage_does_not_invent_a_textured_icon_in_unrelated_scenes()
    {
        using var frame = RealFrame();
        if (frame == null) return;
        using var tpl = new Mat(frame, new Rect(8, 455, 76, 76)).Clone();
        var det = new Detector();
        for (int seed = 50; seed < 56; seed++)
        {
            using var scene = Background(1280, 729, seed);
            Assert.Empty(det.Find(scene, tpl, 0.9));
        }
    }

    [Fact]
    public void Live_screen_capture_is_real_and_a_piece_of_it_is_found_again()
    {
        var desk = MacroStudio.Native.Screens.Current();
        if (desk.Virtual.Width == 0) return; // no interactive desktop
        using var a = ScreenCapture.Capture();
        Assert.Equal((desk.Virtual.Width, desk.Virtual.Height), (a.Width, a.Height));
        // most textured 64x48 blocks first, but only one that is unique on screen (striped or repeating areas match everywhere)
        using var gray = new Mat(); Cv2.CvtColor(a, gray, ColorConversionCodes.BGR2GRAY);
        var blocks = new List<(Rect R, double Sd)>();
        for (int y = 0; y + 48 < a.Height; y += 48)
            for (int x = 0; x + 64 < a.Width; x += 64)
            {
                using var blk = new Mat(gray, new Rect(x, y, 64, 48));
                Cv2.MeanStdDev(blk, out _, out var sd);
                if (sd.Val0 >= 10) blocks.Add((new Rect(x, y, 64, 48), sd.Val0));
            }
        var probe = new Detector();
        Rect best = default; bool picked = false;
        foreach (var (r, _) in blocks.OrderByDescending(b => b.Sd).Take(40))
        {
            using var cand = new Mat(a, r).Clone();
            if (probe.Find(a, cand, 0.85, occlusion: false).Count == 1) { best = r; picked = true; break; }
        }
        if (!picked) return; // a blank or fully repetitive screen proves nothing
        using var tpl = new Mat(a, best).Clone();
        bool ok = false;
        for (int attempt = 0; attempt < 3 && !ok; attempt++) // the screen can animate between grabs
        {
            using var b = ScreenCapture.Capture();
            ok = new Detector().Find(b, tpl, 0.85).Any(d => Math.Abs(d.Rect.X - best.X) <= 1 && Math.Abs(d.Rect.Y - best.Y) <= 1);
        }
        Assert.True(ok);
    }

    // ---- real game screenshot from the Revolution folder ----

    static Mat? RealFrame()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Revolution", "logs", "debug_images", "inventory_detect.png");
            if (File.Exists(p)) return Cv2.ImDecode(File.ReadAllBytes(p), ImreadModes.Color);
        }
        return null; // sample not present on this machine
    }

    public static IEnumerable<object[]> RealCrops => new[]
    {
        new object[] { 226, 104, 52, 52 },   // settings gear
        new object[] { 380, 628, 66, 66 },   // hotbar slot 1
        new object[] { 8, 372, 64, 64 },     // Ant Pass icon
    };

    [Theory, MemberData(nameof(RealCrops))]
    public void Real_game_ui_element_is_found_where_it_was_cut_moved_and_scaled(int x, int y, int w, int h)
    {
        using var frame = RealFrame();
        if (frame == null) return;
        var crop = new Rect(x, y, w, h);
        using var tpl = new Mat(frame, crop).Clone();
        var det = new Detector();

        var same = det.Find(frame, tpl, 0.9);
        Assert.Contains(same, d => Math.Abs(d.Rect.X - x) <= 1 && Math.Abs(d.Rect.Y - y) <= 1);

        using var moved = frame.Clone();
        Paste(moved, tpl, 700, 300);
        Assert.Contains(det.Find(moved, tpl, 0.9), d => Math.Abs(d.Rect.X - 700) <= 1 && Math.Abs(d.Rect.Y - 300) <= 1);

        using var small = Scaled(frame, 0.85);
        var found = det.Find(small, tpl, 0.75);
        Assert.Contains(found, d => Math.Abs(d.Center.X - (x + w / 2) * 0.85) <= 4 && Math.Abs(d.Center.Y - (y + h / 2) * 0.85) <= 4);
    }
}
