using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// D2 shape-matching tests. The big one: a template's edge map is polluted at its border (the blur+Sobel read reflected
/// pixels there), so the same button on a different background used to correlate at ~0.40 and was lost. On a plain
/// background the greyed-out RETRY must now be found by shape with a clear margin, while a different-label button stays rejected.
/// </summary>
public class D2ShapeTests
{
    static Mat Button(string text, int w = 190, int h = 48)
    {
        var b = new Mat(h, w, MatType.CV_8UC3);
        for (int y = 0; y < h; y++)
        {
            double f = y / (double)(h - 1);
            Cv2.Line(b, new Point(0, y), new Point(w, y), new Scalar(110 - 60 * f, 215 - 70 * f, 255 - 25 * f));
        }
        Cv2.Rectangle(b, new Rect(0, 0, w, h), new Scalar(30, 60, 90), 3);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheyDuplex, 0.8, 2, out _);
        Cv2.PutText(b, text, new Point((w - size.Width) / 2, (h + size.Height) / 2), HersheyFonts.HersheyDuplex, 0.8, new Scalar(20, 40, 70), 2, LineTypes.AntiAlias);
        return b;
    }

    static Mat Scene(int w = 1920, int h = 1080)
    {
        var bg = new Mat(h, w, MatType.CV_8UC3);
        Cv2.Randu(bg, Scalar.All(10), Scalar.All(60));
        Cv2.GaussianBlur(bg, bg, new Size(0, 0), 3);
        return bg;
    }

    static void Paste(Mat scene, Mat b, int x, int y) { using var d = new Mat(scene, new Rect(x, y, b.Width, b.Height)); b.CopyTo(d); }

    static Mat Greyed(Mat bgr)
    {
        using var g = new Mat(); Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        var r = new Mat(); Cv2.CvtColor(g, r, ColorConversionCodes.GRAY2BGR);
        return r;
    }

    /// <summary>
    /// The bug: the greyed retry sits on a flat dark strip — nothing like its gold capture surroundings. The border ring
    /// of the template's edge map (computed against its own reflected pixels) used to disagree so hard with the frame's
    /// ring (button vs new background) that a pixel-identical copy scored 0.40 and was missed. Interior-only scoring must see ~1.0.
    /// </summary>
    [Fact]
    public void A_greyed_out_button_on_a_plain_background_is_found_by_its_shape()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var modifiers = RealButtons.Decode(RealButtons.Modifiers);
        using var frame = new Mat(120, modifiers.Width + 40 + retry.Width + 40, MatType.CV_8UC3, new Scalar(12, 12, 14));
        Paste(frame, modifiers, 20, 40);
        using (var grey = Greyed(retry)) Paste(frame, grey, 60 + modifiers.Width, 40);
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count > 0, "the greyed-out button was not found at all");
        Assert.Equal("shape", hits[0].Method);
        Assert.InRange(hits[0].Rect.X, 57 + modifiers.Width, 63 + modifiers.Width);
        Assert.DoesNotContain(hits, h => Math.Abs(h.Rect.X - 20) < 40);
    }

    /// <summary>The other half of the contract: the same chrome with a different word on it must not match.</summary>
    [Fact]
    public void A_same_shape_button_with_a_different_label_stays_rejected_on_a_plain_background()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var modifiers = RealButtons.Decode(RealButtons.Modifiers);
        using var frame = new Mat(120, modifiers.Width + 40, MatType.CV_8UC3, new Scalar(12, 12, 14));
        using (var grey = Greyed(modifiers)) Paste(frame, grey, 20, 40);
        var hits = new Detector().Find(frame, retry, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.True(hits.Count == 0, $"wrong button: {string.Join(", ", hits.Select(h => $"({h.Rect.X},{h.Rect.Y}) {h.Method} {h.Score:0.00}"))}");
    }

    /// <summary>An inverted-polarity copy (light strokes on dark) has the same gradient field reversed: magnitude edges still match.</summary>
    [Fact]
    public void A_light_on_dark_copy_of_the_button_is_found_by_shape()
    {
        using var btn = Button("RETRY (0/1)");
        using var scene = Scene();
        using var inv = new Mat(); Cv2.BitwiseNot(btn, inv);
        Paste(scene, inv, 1200, 300);
        var hits = new Detector().Find(scene, btn, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.Contains(hits, h => h.Method == "shape" && Math.Abs(h.Rect.X - 1200) <= 3 && Math.Abs(h.Rect.Y - 300) <= 3);
    }

    /// <summary>A target hard against the frame edge must still be matchable: edge extraction must not assume neighbours exist.</summary>
    [Fact]
    public void A_greyed_button_touching_the_frame_border_is_still_found()
    {
        using var btn = Button("RETRY (0/1)");
        using var grey = Greyed(btn);
        using var scene = Scene(1400, 700);
        Paste(scene, grey, 0, 620);
        var hits = new Detector().Find(scene, btn, 0.75, 1, null, occlusion: true, minVisible: 0.25);
        Assert.Contains(hits, h => h.Method == "shape" && Math.Abs(h.Rect.X) <= 3 && Math.Abs(h.Rect.Y - 620) <= 3);
    }

    /// <summary>Shape-stage speed: a whole-frame find on 1080p must stay comfortably under the old ~160 ms.</summary>
    [Fact]
    public void Shape_find_on_1080p_is_quick()
    {
        using var retry = RealButtons.Decode(RealButtons.Retry);
        using var bg = new Mat(1080, 1920, MatType.CV_8UC3); Cv2.Randu(bg, Scalar.All(10), Scalar.All(120));
        using var ctx = new FrameContext(bg);
        ShapeMatching.Find(ctx, retry, 1, null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) ShapeMatching.Find(ctx, retry, 1, null);
        long per = sw.ElapsedMilliseconds / 5;
        Console.WriteLine($"D2 shape find 1080p: {per} ms");
        Assert.True(per < 380, $"{per} ms");
    }
}
