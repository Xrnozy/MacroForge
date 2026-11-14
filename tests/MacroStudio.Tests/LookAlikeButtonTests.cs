using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>Buttons that share the same shape, colour and border and differ only in their text (RETRY vs MODIFIERS): the text is what tells them apart.</summary>
public class LookAlikeButtonTests
{
    static Mat Button(string text, bool gold = true, int w = 190, int h = 48)
    {
        var b = new Mat(h, w, MatType.CV_8UC3);
        for (int y = 0; y < h; y++)
        {
            double f = y / (double)(h - 1);
            var c = gold ? new Scalar(110 - 60 * f, 215 - 70 * f, 255 - 25 * f) : new Scalar(105 - 25 * f, 105 - 25 * f, 110 - 25 * f);
            Cv2.Line(b, new Point(0, y), new Point(w, y), c);
        }
        Cv2.Rectangle(b, new Rect(0, 0, w, h), new Scalar(30, 60, 90), 3);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheyDuplex, 0.8, 2, out _);
        Cv2.PutText(b, text, new Point((w - size.Width) / 2, (h + size.Height) / 2), HersheyFonts.HersheyDuplex, 0.8, new Scalar(20, 40, 70), 2, LineTypes.AntiAlias);
        return b;
    }

    static Mat Scene()
    {
        var bg = new Mat(1080, 1920, MatType.CV_8UC3);
        Cv2.Randu(bg, Scalar.All(10), Scalar.All(60));
        Cv2.GaussianBlur(bg, bg, new Size(0, 0), 3);
        return bg;
    }

    static void Paste(Mat scene, Mat b, int x, int y) { using var d = new Mat(scene, new Rect(x, y, b.Width, b.Height)); b.CopyTo(d); }

    [Fact]
    public void A_look_alike_button_with_other_text_is_not_mistaken_for_the_target_even_when_partial_matching_is_on()
    {
        using var retry = Button("RETRY (0/1)");
        using var scene = Scene();
        using (var other = Button("MODIFIERS")) Paste(scene, other, 600, 400);
        var hits = new Detector().Find(scene, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count == 0, $"clicked the wrong button: {string.Join(", ", hits.Select(h => $"({h.Rect.X},{h.Rect.Y}) {h.Method} score {h.Score:0.00} visible {h.Visible:P0}"))}");
    }

    [Fact]
    public void The_real_button_is_still_found_when_half_covered_and_beats_a_look_alike_next_to_it()
    {
        using var retry = Button("RETRY (0/1)");
        using var scene = Scene();
        using (var other = Button("MODIFIERS")) Paste(scene, other, 600, 400);
        Paste(scene, retry, 900, 400);
        Cv2.Rectangle(scene, new Rect(900 + 95, 395, 120, 60), new Scalar(25, 25, 25), -1);   // a popup over the right half
        var hits = new Detector().Find(scene, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.NotEmpty(hits);
        Assert.InRange(hits[0].Rect.X, 897, 903); Assert.InRange(hits[0].Rect.Y, 397, 403);
        Assert.DoesNotContain(hits, h => Math.Abs(h.Rect.X - 600) < 40);
    }

    [Fact]
    public void The_real_MODIFIERS_button_is_not_taken_for_the_real_RETRY_target()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var modifiers = RealButtons.Decode(RealButtons.Modifiers);
        using var scene = Scene();
        Paste(scene, modifiers, 700, 500);
        var hits = new Detector().Find(scene, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count == 0, $"clicked the wrong button: {string.Join(", ", hits.Select(h => $"({h.Rect.X},{h.Rect.Y}) {h.Method} score {h.Score:0.00} visible {h.Visible:P0}"))}");
    }

    [Fact]
    public void The_real_RETRY_button_is_found_with_its_right_half_covered_and_with_MODIFIERS_next_to_it()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var modifiers = RealButtons.Decode(RealButtons.Modifiers);
        using var scene = Scene();
        Paste(scene, modifiers, 500, 500);
        Paste(scene, retry, 900, 500);
        Cv2.Rectangle(scene, new Rect(900 + 64, 495, 80, 40), new Scalar(20, 20, 20), -1);
        var hits = new Detector().Find(scene, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.Contains(hits, h => Math.Abs(h.Rect.X - 900) <= 3 && Math.Abs(h.Rect.Y - 500) <= 3);
        Assert.DoesNotContain(hits, h => Math.Abs(h.Rect.X - 500) < 40);
    }
}

/// <summary>The real game strip: LEAVE, MODIFIERS and RETRY, with RETRY either gold (enabled) or greyed out (light text on a dark box). The target is the gold RETRY.</summary>
public class RealStripTests
{
    const int Ox = 20, Oy = 40;     // where the strip is pasted in the test frame

    static Mat Frame(Mat strip) { var bg = new Mat(120, strip.Width + 40, MatType.CV_8UC3, new Scalar(12, 12, 14)); using var d = new Mat(bg, new Rect(Ox, Oy, strip.Width, strip.Height)); strip.CopyTo(d); return bg; }

    static string Show(IEnumerable<Detection> hits) => string.Join(", ", hits.Select(h => $"({h.Rect.X - Ox},{h.Rect.Y - Oy}) {h.Method} {h.Score:0.00}"));

    [Fact]
    public void A_greyed_out_RETRY_is_found_by_its_shape_not_its_colour_and_LEAVE_and_MODIFIERS_are_not()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var frame = Frame(RealButtons.Decode(RealButtons.StripGreyedRetry));
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count > 0, "the greyed-out RETRY was not found");
        Assert.Equal("shape", hits[0].Method);
        Assert.InRange(hits[0].Center.X - Ox, 322, 445);                                      // on RETRY, not on LEAVE (40..173) or MODIFIERS (183..312)
        Assert.All(hits, h => Assert.True(h.Center.X - Ox > 312, "matched another button: " + Show(hits)));
    }

    [Fact]
    public void With_RETRY_gone_neither_LEAVE_nor_MODIFIERS_is_taken_for_it()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var strip = RealButtons.Decode(RealButtons.StripGoldRetry);
        Cv2.Rectangle(strip, new Rect(362, 0, strip.Width - 362, strip.Height), new Scalar(12, 12, 14), -1);   // RETRY is not on screen
        using var frame = Frame(strip);
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count == 0, "wrong button: " + Show(hits));
    }

    [Fact]
    public void The_enabled_gold_RETRY_is_found_exactly_and_the_buttons_beside_it_are_not()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var frame = Frame(RealButtons.Decode(RealButtons.StripGoldRetry));
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count > 0);
        Assert.Equal("template", hits[0].Method);
        Assert.InRange(hits[0].Center.X - Ox, 372, 505);
        Assert.All(hits, h => Assert.True(h.Center.X - Ox > 362, "matched another button: " + Show(hits)));
    }
}

public class ShapeCost
{
    [Fact]
    public void Shape_stage_on_a_full_hd_frame_is_cheap_enough_for_the_deep_stage()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        var bg = new Mat(1080, 1920, MatType.CV_8UC3); Cv2.Randu(bg, Scalar.All(10), Scalar.All(120));
        using var ctx = new FrameContext(bg);
        ShapeMatching.Find(ctx, retry, 1, null);                       // warm up (edge image, template cache)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) ShapeMatching.Find(ctx, retry, 1, null);
        long per = sw.ElapsedMilliseconds / 5;
        Console.WriteLine($"SHAPE-COST 1080p whole frame: {per} ms per search (edge image cached per frame)");
        using var fresh = new FrameContext(bg);
        sw.Restart(); ShapeMatching.Find(fresh, retry, 1, null);
        Console.WriteLine($"SHAPE-COST including the frame's edge image: {sw.ElapsedMilliseconds} ms");
        Assert.True(per < 400, $"{per} ms");
    }
}

public class ShapeUnderCursor
{
    static Mat Frame(Mat strip) { var bg = new Mat(140, strip.Width + 60, MatType.CV_8UC3, new Scalar(12, 12, 14)); using var d = new Mat(bg, new Rect(30, 50, strip.Width, strip.Height)); strip.CopyTo(d); return bg; }

    [Fact]
    public void The_greyed_RETRY_is_still_found_with_the_game_cursor_drawn_over_its_letters()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var strip = RealButtons.Decode(RealButtons.GreyedRetryUnderCursor); using var frame = Frame(strip);
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count > 0, "lost the button when the cursor is over its text");
        Assert.Equal("shape", hits[0].Method);
        Assert.InRange(hits[0].Rect.X - 30, 10, 40);
    }

    [Fact]
    public void A_cursor_over_part_of_the_label_does_not_make_another_button_match()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var strip = RealButtons.Decode(RealButtons.StripGoldRetry);
        Cv2.Rectangle(strip, new Rect(362, 0, strip.Width - 362, strip.Height), new Scalar(12, 12, 14), -1);        // RETRY is off screen
        using var frame = Frame(strip);
        Assert.Empty(new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25));
    }
}
