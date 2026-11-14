using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// Colour comparison for the region averages it is handed (mean of the matched region vs the template's mean).
/// Two regimes share one scale:
///  - coloured targets: distance in the (a, b) chroma plane of CIE Lab. A hover highlight or a darker "pressed"
///    state changes lightness (L) only and costs nothing; a real change of hue or saturation costs a lot.
///  - near-grey targets: grey, white and black all sit at the neutral point of the chroma plane, where there is
///    no hue to compare. For those the distance falls back to the lightness difference — otherwise a black target
///    would accept a white region — and the tolerance reads as "allowed lightness drift".
/// 0 = same colour. Roughly: slight tint 5-15, a clearly different hue 40+, orange vs green 80+, dark vs light grey 80+.
/// </summary>
public static class ColorMath
{
    public const double DefaultTolerance = 30;

    /// <summary>Chroma magnitude (|ab*|) below which a colour counts as neutral — no hue worth comparing. Covers slightly tinted greys.</summary>
    const double NeutralChroma = 20;
    /// <summary>How much a lightness difference counts between two near-neutral colours. At the default tolerance of 30 this lets a grey drift ±20 L and rejects grey pairs about 25 L or more apart (black vs mid-grey is ~54).</summary>
    const double NeutralLightnessWeight = 1.5;

    // The template's average is compared against many candidates in a row: remember the last conversion.
    [ThreadStatic] static Scalar _memoIn;
    [ThreadStatic] static (double L, double A, double B) _memoOut;
    [ThreadStatic] static bool _memoSet;

    /// <summary>CIE Lab (D65) of a colour given as BGR 0-255 (OpenCV order, e.g. from <c>Cv2.Mean</c>). Any alpha in the scalar is ignored.</summary>
    static (double L, double A, double B) Lab(Scalar bgr)
    {
        if (_memoSet && _memoIn.Val0 == bgr.Val0 && _memoIn.Val1 == bgr.Val1 && _memoIn.Val2 == bgr.Val2) return _memoOut;
        static double Lin(double c) { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        double r = Lin(bgr.Val2), g = Lin(bgr.Val1), b = Lin(bgr.Val0);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        var v = (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        _memoIn = bgr; _memoOut = v; _memoSet = true;
        return v;
    }

    /// <summary>(a, b) chroma of a colour given as BGR 0-255 (OpenCV order, e.g. from <c>Cv2.Mean</c>).</summary>
    public static (double A, double B) Chroma(Scalar bgr)
    {
        var (_, a, b) = Lab(bgr);
        return (a, b);
    }

    public static double Distance(Scalar bgrA, Scalar bgrB)
    {
        var (l1, a1, b1) = Lab(bgrA); var (l2, a2, b2) = Lab(bgrB);
        double da = a1 - a2, db = b1 - b2;
        double d = Math.Sqrt(da * da + db * db);
        // Both near the neutral point: chroma carries nothing (greys have no hue), so lightness is all that is left to compare.
        double cmax = Math.Max(Math.Sqrt(a1 * a1 + b1 * b1), Math.Sqrt(a2 * a2 + b2 * b2));
        if (cmax < NeutralChroma)
            d += NeutralLightnessWeight * (1 - cmax / NeutralChroma) * Math.Abs(l1 - l2);
        return d;
    }

    /// <summary>True when the two average colours are within <paramref name="tolerance"/>. A tolerance of 250 or more turns the check off.</summary>
    public static bool Similar(Scalar bgrA, Scalar bgrB, double tolerance)
    {
        if (tolerance >= 250) return true;
        if (bgrA.Val0 == bgrB.Val0 && bgrA.Val1 == bgrB.Val1 && bgrA.Val2 == bgrB.Val2) return true;   // identical averages: free
        return Distance(bgrA, bgrB) <= Math.Max(tolerance, 0);
    }
}
