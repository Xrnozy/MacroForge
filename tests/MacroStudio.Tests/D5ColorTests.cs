using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

/// <summary>
/// The colour-tolerance model (agent D5). <see cref="ColorMath.Similar"/> compares the two region averages it is
/// handed by <c>Detector.ColorOk</c> and <c>PatchVoting</c>: brightness must be ignored for coloured targets (a hover
/// highlight or a pressed state changes lightness only), a different hue or saturation must still reject, and for
/// near-grey targets — which carry no hue at all — lightness is the only signal left.
/// </summary>
public class D5ColorTests
{
    static Scalar Bgr(double b, double g, double r) => new(b, g, r);

    static readonly Scalar Orange = Bgr(40, 140, 230);    // the button colour used across the vision tests
    static readonly Scalar Green = Bgr(70, 190, 70);
    static readonly Scalar Red = Bgr(50, 50, 215);
    static readonly Scalar Blue = Bgr(215, 90, 40);

    static Scalar Scale(Scalar c, double k) => Bgr(Math.Min(255, c.Val0 * k), Math.Min(255, c.Val1 * k), Math.Min(255, c.Val2 * k));
    static Scalar Add(Scalar c, double d) => Bgr(Math.Clamp(c.Val0 + d, 0, 255), Math.Clamp(c.Val1 + d, 0, 255), Math.Clamp(c.Val2 + d, 0, 255));
    /// <summary>The colour pulled <paramref name="t"/> of the way towards grey (saturation wash-out).</summary>
    static Scalar Wash(Scalar c, double t) => Bgr(c.Val0 + (128 - c.Val0) * t, c.Val1 + (128 - c.Val1) * t, c.Val2 + (128 - c.Val2) * t);

    /// <summary>The colour with its hue rotated by <paramref name="dh"/> OpenCV hue steps (half-degrees).</summary>
    static Scalar ShiftHue(Scalar c, int dh)
    {
        using var m = new Mat(1, 1, MatType.CV_8UC3, c);
        using var hsv = Hue(m, dh);
        return Cv2.Mean(hsv);
    }

    /// <summary>A whole image hue-rotated by <paramref name="dh"/> OpenCV hue steps (the same transform the robustness suite uses).</summary>
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

    /// <summary>The kind of target this check actually sees: a gradient fill, a darker border, white text — its mean is what is compared.</summary>
    static Mat FlatButton(Scalar fill, string text = "PLAY")
    {
        var b = new Mat(40, 110, MatType.CV_8UC3, fill);
        for (int y = 0; y < 40; y++) Cv2.Line(b, new Point(0, y), new Point(109, y), new Scalar(fill.Val0 * (0.8 + 0.2 * y / 40.0), fill.Val1 * (0.8 + 0.2 * y / 40.0), fill.Val2 * (0.8 + 0.2 * y / 40.0)));
        Cv2.Rectangle(b, new Rect(0, 0, 110, 40), new Scalar(fill.Val0 * 0.5, fill.Val1 * 0.5, fill.Val2 * 0.5), 2);
        Cv2.PutText(b, text, new Point(26, 27), HersheyFonts.HersheySimplex, 0.8, Scalar.All(255), 2);
        return b;
    }

    // ---- brightness must be ignored for coloured targets (hover / pressed states) ----

    [Theory]
    [InlineData(0.5)]   // much darker, same hue
    [InlineData(0.75)]
    [InlineData(1.3)]
    public void A_coloured_target_ignores_gain(double gain)
    {
        Assert.True(ColorMath.Similar(Orange, Scale(Orange, gain), ColorMath.DefaultTolerance),
            $"orange vs x{gain} darker/brighter: distance {ColorMath.Distance(Orange, Scale(Orange, gain)):0.0} must stay under the default tolerance");
    }

    [Fact]
    public void A_coloured_target_ignores_an_additive_highlight()
    {
        var hover = Add(Orange, 40);
        Assert.True(ColorMath.Similar(Orange, hover, ColorMath.DefaultTolerance),
            $"distance {ColorMath.Distance(Orange, hover):0.0} must stay under the default tolerance");
    }

    // ---- hue and saturation differences must still reject ----

    [Theory]
    [InlineData(4)]     // the "+8 deg" robustness variant: a tint
    [InlineData(10)]    // the "+20 deg" robustness variant: still the same button
    public void A_small_hue_turn_of_a_button_is_allowed(int dh)
    {
        using var original = FlatButton(Orange);
        using var shifted = Hue(original, dh);
        double d = ColorMath.Distance(Cv2.Mean(original), Cv2.Mean(shifted));
        Assert.True(ColorMath.Similar(Cv2.Mean(original), Cv2.Mean(shifted), ColorMath.DefaultTolerance),
            $"button hue +{dh}cv: mean distance {d:0.0} must stay under the default tolerance");
    }

    [Theory]
    [InlineData(22)]    // the "+45 deg" robustness variant
    [InlineData(40)]
    public void A_hue_turn_beyond_a_tint_is_rejected(int dh)
    {
        using var original = FlatButton(Orange);
        using var shifted = Hue(original, dh);
        double d = ColorMath.Distance(Cv2.Mean(original), Cv2.Mean(shifted));
        Assert.False(ColorMath.Similar(Cv2.Mean(original), Cv2.Mean(shifted), ColorMath.DefaultTolerance),
            $"button hue +{dh}cv: mean distance {d:0.0} is a different colour");
    }

    [Theory]
    [InlineData(6, true)]    // a tint of a few degrees on a pure colour
    [InlineData(15, false)]  // ~40 on the distance scale: the documented "clearly different hue"
    [InlineData(30, false)]
    public void A_pure_colours_hue_scale(int dh, bool expected)
    {
        var shifted = ShiftHue(Orange, dh);
        double d = ColorMath.Distance(Orange, shifted);
        Console.WriteLine($"pure orange hue +{dh}cv: distance {d:0.0}");
        Assert.Equal(expected, ColorMath.Similar(Orange, shifted, ColorMath.DefaultTolerance));
    }

    [Theory]
    [InlineData(nameof(Green))]
    [InlineData(nameof(Red))]
    [InlineData(nameof(Blue))]
    public void A_clearly_different_colour_is_rejected(string other)
    {
        var c = other switch { nameof(Green) => Green, nameof(Red) => Red, _ => Blue };
        Assert.True(ColorMath.Distance(Orange, c) > ColorMath.DefaultTolerance,
            $"orange vs {other}: distance {ColorMath.Distance(Orange, c):0.0}");
        Assert.False(ColorMath.Similar(Orange, c, ColorMath.DefaultTolerance));
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(0.8)]
    public void A_washed_out_version_is_rejected_when_most_of_the_colour_is_gone(double t)
    {
        var washed = Wash(Orange, t);
        Console.WriteLine($"wash {t:0.0}: distance {ColorMath.Distance(Orange, washed):0.0}");
        Assert.False(ColorMath.Similar(Orange, washed, ColorMath.DefaultTolerance),
            $"a {t:0%} desaturated look-alike must not pass for the saturated target");
    }

    [Fact]
    public void A_slightly_washed_version_is_still_allowed()
    {
        var washed = Wash(Orange, 0.25);
        Assert.True(ColorMath.Similar(Orange, washed, ColorMath.DefaultTolerance),
            $"distance {ColorMath.Distance(Orange, washed):0.0}");
    }

    // ---- grey targets: there is no hue to compare, lightness is everything ----

    [Fact]
    public void Black_and_white_are_not_the_same_colour()
    {
        // Before the fix both sat at the neutral point of the chroma plane: distance 0, always "similar".
        Assert.True(ColorMath.Distance(Bgr(0, 0, 0), Bgr(255, 255, 255)) > ColorMath.DefaultTolerance);
        Assert.False(ColorMath.Similar(Bgr(0, 0, 0), Bgr(255, 255, 255), ColorMath.DefaultTolerance));
    }

    [Theory]
    [InlineData(60, 200)]    // dark grey vs light grey
    [InlineData(0, 128)]     // black vs mid grey
    [InlineData(128, 255)]   // mid grey vs white
    public void Very_different_greys_are_not_the_same_target(int a, int b)
    {
        Assert.False(ColorMath.Similar(Bgr(a, a, a), Bgr(b, b, b), ColorMath.DefaultTolerance),
            $"grey {a} vs grey {b}: distance {ColorMath.Distance(Bgr(a, a, a), Bgr(b, b, b)):0.0}");
    }

    [Theory]
    [InlineData(120, 140)]   // a grey hover is still allowed...
    [InlineData(90, 110)]
    public void Nearby_greys_are_still_similar(int a, int b)
    {
        Assert.True(ColorMath.Similar(Bgr(a, a, a), Bgr(b, b, b), ColorMath.DefaultTolerance),
            $"grey {a} vs grey {b}: distance {ColorMath.Distance(Bgr(a, a, a), Bgr(b, b, b)):0.0}");
    }

    [Fact]
    public void A_grey_target_does_not_match_a_saturated_colour()
    {
        Assert.False(ColorMath.Similar(Bgr(150, 150, 150), Orange, ColorMath.DefaultTolerance));
        Assert.False(ColorMath.Similar(Bgr(200, 200, 200), Green, ColorMath.DefaultTolerance));
    }

    // ---- dark colours: chroma compresses toward black, a black region must not pass for a dark red one ----

    [Fact]
    public void A_dark_colour_is_not_matched_by_black()
    {
        var darkRed = Bgr(15, 15, 80);
        Console.WriteLine($"dark red vs black: {ColorMath.Distance(darkRed, Bgr(0, 0, 0)):0.0}; " +
                          $"dark red vs itself x0.8: {ColorMath.Distance(darkRed, Scale(darkRed, 0.8)):0.0}");
        Assert.True(ColorMath.Distance(darkRed, Bgr(0, 0, 0)) > ColorMath.Distance(darkRed, Scale(darkRed, 0.5)),
            "a black region must be farther from a dark colour than the same colour darkened");
        Assert.False(ColorMath.Similar(darkRed, Bgr(0, 0, 0), ColorMath.DefaultTolerance));
    }

    // ---- tolerance semantics ----

    [Fact]
    public void Tolerance_is_a_hard_boundary()
    {
        double d = ColorMath.Distance(Orange, Add(Orange, 24));
        Assert.True(d > 1, "test pair should have a non-trivial distance");
        Assert.True(ColorMath.Similar(Orange, Add(Orange, 24), d + 0.5));
        Assert.False(ColorMath.Similar(Orange, Add(Orange, 24), d - 0.5));
    }

    [Theory]
    [InlineData(250)]
    [InlineData(1000)]
    public void A_tolerance_of_250_or_more_turns_the_check_off(double tol)
    {
        Assert.True(ColorMath.Similar(Orange, Green, tol));
        Assert.True(ColorMath.Similar(Bgr(0, 0, 0), Bgr(255, 255, 255), tol));
    }

    [Fact]
    public void Identical_means_pass_at_any_tolerance()
    {
        Assert.True(ColorMath.Similar(Orange, Orange, 0));
        Assert.True(ColorMath.Similar(Bgr(0, 0, 0), Bgr(0, 0, 0), 0));
    }

    [Fact]
    public void A_negative_or_missing_tolerance_rejects_nothing_but_differences()
    {
        Assert.True(ColorMath.Similar(Orange, Orange, -5));          // identical still passes
        Assert.False(ColorMath.Similar(Orange, Green, -5));          // anything else does not
    }

    // ---- what the API can and cannot see ----

    [Fact]
    public void The_alpha_channel_is_ignored()
    {
        var a = new Scalar(10, 20, 30, 0);
        var b = new Scalar(10, 20, 30, 255);
        Assert.Equal(0, ColorMath.Distance(a, b));
        Assert.True(ColorMath.Similar(a, b, ColorMath.DefaultTolerance));
    }

    static Mat Gradient(Scalar top, Scalar bottom, int w = 60, int h = 30)
    {
        var m = new Mat(h, w, MatType.CV_8UC3);
        for (int y = 0; y < h; y++)
        {
            double t = y / (double)(h - 1);
            var c = Bgr(top.Val0 + (bottom.Val0 - top.Val0) * t, top.Val1 + (bottom.Val1 - top.Val1) * t, top.Val2 + (bottom.Val2 - top.Val2) * t);
            Cv2.Line(m, new Point(0, y), new Point(w - 1, y), c);
        }
        return m;
    }

    [Fact]
    public void A_gradient_buttons_mean_behaves_like_its_middle_colour()
    {
        // the real buttons are gradients; their mean sits between the ends and must stay inside tolerance
        using var grad = Gradient(Scale(Orange, 0.8), Scale(Orange, 1.1));
        using var grad2 = Gradient(Scale(Orange, 0.85), Scale(Orange, 1.2));
        var mean = Cv2.Mean(grad);
        Assert.True(ColorMath.Similar(mean, Cv2.Mean(grad2), ColorMath.DefaultTolerance),
            $"two orange gradients: distance {ColorMath.Distance(mean, Cv2.Mean(grad2)):0.0}");
        using var redGrad = Gradient(Scale(Red, 0.8), Scale(Red, 1.1));
        Assert.False(ColorMath.Similar(mean, Cv2.Mean(redGrad), ColorMath.DefaultTolerance), "an orange and a red gradient");
    }

    [Fact]
    public void A_mean_alone_cannot_see_how_the_pixels_are_arranged()
    {
        // Two regions can share an average colour with different content: the mean check passes for them BY DESIGN —
        // that is why the pipeline also matches structure (gray correlation) and per-patch colours, which this API never sees.
        using var split = new Mat(30, 60, MatType.CV_8UC3);
        using (var l = new Mat(split, new Rect(0, 0, 30, 30))) l.SetTo(Red);
        using (var r = new Mat(split, new Rect(30, 0, 30, 30))) r.SetTo(Blue);
        var mean = Cv2.Mean(split);   // a purple-ish average
        using var solid = new Mat(30, 60, MatType.CV_8UC3, mean);
        Assert.True(ColorMath.Similar(mean, Cv2.Mean(solid), ColorMath.DefaultTolerance),
            "same average = same colour to this check; discrimination of arrangement is the other stages' job");
    }

    // ---- speed: called per candidate ----

    [Fact]
    public void The_check_is_cheap_enough_to_run_per_candidate()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int yes = 0;
        for (int i = 0; i < 200_000; i++)
            if (ColorMath.Similar(Orange, Bgr(i % 255, (i / 256) % 255, 128), ColorMath.DefaultTolerance)) yes++;
        sw.Stop();
        Console.WriteLine($"200k colour checks: {sw.ElapsedMilliseconds} ms ({yes} similar)");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms is too slow for a per-candidate check");
    }

    [Fact]
    public void Report_the_distance_scale()
    {
        var pairs = new (string, Scalar, Scalar)[]
        {
            ("identical", Orange, Orange),
            ("orange x0.8", Orange, Scale(Orange, 0.8)),
            ("orange x0.5", Orange, Scale(Orange, 0.5)),
            ("orange +28", Orange, Add(Orange, 28)),
            ("orange +60", Orange, Add(Orange, 60)),
            ("orange hue+12deg", Orange, ShiftHue(Orange, 6)),
            ("orange hue+30deg", Orange, ShiftHue(Orange, 15)),
            ("orange hue+60deg", Orange, ShiftHue(Orange, 30)),
            ("orange wash 25%", Orange, Wash(Orange, 0.25)),
            ("orange wash 60%", Orange, Wash(Orange, 0.6)),
            ("orange vs green", Orange, Green),
            ("orange vs red", Orange, Red),
            ("orange vs blue", Orange, Blue),
            ("black vs white", Bgr(0, 0, 0), Bgr(255, 255, 255)),
            ("black vs grey60", Bgr(0, 0, 0), Bgr(60, 60, 60)),
            ("grey120 vs grey140", Bgr(120, 120, 120), Bgr(140, 140, 140)),
            ("grey60 vs grey200", Bgr(60, 60, 60), Bgr(200, 200, 200)),
            ("darkred vs black", Bgr(15, 15, 80), Bgr(0, 0, 0)),
            ("darkred vs darkred x0.5", Bgr(15, 15, 80), Scale(Bgr(15, 15, 80), 0.5)),
            ("grey vs orange", Bgr(150, 150, 150), Orange),
            ("warm grey vs cool grey", Bgr(120, 128, 136), Bgr(136, 128, 120)),
        };
        foreach (var (n, a, b) in pairs)
            Console.WriteLine($"COLOR {n,-26} distance {ColorMath.Distance(a, b),6:0.0}  ->  {(ColorMath.Similar(a, b, ColorMath.DefaultTolerance) ? "similar" : "REJECTED")}");
        using var btn = FlatButton(Orange);
        foreach (var (n, v) in new (string, Mat)[] { ("btn hue+8deg", Hue(btn, 4)), ("btn hue+20deg", Hue(btn, 10)), ("btn hue+45deg", Hue(btn, 22)), ("btn green", FlatButton(Green)), ("btn grey", FlatButton(Bgr(150, 150, 150))) })
            using (v) Console.WriteLine($"COLOR {n,-26} distance {ColorMath.Distance(Cv2.Mean(btn), Cv2.Mean(v)),6:0.0}  ->  {(ColorMath.Similar(Cv2.Mean(btn), Cv2.Mean(v), ColorMath.DefaultTolerance) ? "similar" : "REJECTED")}");
    }
}
