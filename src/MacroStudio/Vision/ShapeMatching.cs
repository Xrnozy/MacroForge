using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// Finds a target by its SHAPE only: the strokes of its text, icon and outline, with no regard for colour or for which part is light and which is dark.
/// The frame and the template are both reduced to edge strength (gradient magnitude) and correlated. A button that is greyed out, highlighted,
/// recoloured or drawn light-on-dark still matches its enabled self, and a different label on the same kind of button does not.
///
/// Two ideas keep it honest and cheap:
///  - Interior-only correlation. <see cref="EdgeOf"/> blurs (kernel reaches ~3 px) then applies a 3x3 Sobel, so the outermost ~4 px of any
///    edge image is computed against border-REFLECTED pixels. For the template that ring encodes whatever surrounded it at capture time;
///    for a frame window it encodes the surroundings NOW. The two disagree arbitrarily — a pixel-identical button on a new background
///    scored ~0.40 and was lost. The template's edge map is therefore cropped to its interior before correlating, and every compared
///    window comes from a region whose own border values are never looked at.
///  - Coarse at half size, verify at full size. The expensive whole-frame correlation runs on the half-size edge image and only nominates
///    a handful of places; each is then re-scored on a small full-resolution edge crop around the nomination. Same answer, ~6x cheaper.
/// </summary>
internal static class ShapeMatching
{
    /// <summary>Edge correlation a match must reach. Two different labels on the same button score far lower than the same label in another colour.</summary>
    public const double Floor = 0.80;

    /// <summary>How far into an edge image the border-reflection artifact reaches (Gaussian kernel ~7 taps + 3x3 Sobel = 4 px).</summary>
    const int Ring = 4;

    /// <summary>Coarse peaks worth a full-resolution check are gathered down to this score; the fine score decides.</summary>
    const double Gather = 0.30;

    /// <summary>Edge strength of a gray image (float): Sobel gradient magnitude after a very light blur.</summary>
    internal static Mat EdgeOf(Mat gray)
    {
        using var f = new Mat();
        gray.ConvertTo(f, MatType.CV_32F);
        Cv2.GaussianBlur(f, f, new Size(0, 0), 0.8);
        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(f, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(f, gy, MatType.CV_32F, 0, 1, 3);
        var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        return mag;
    }

    /// <summary>The border ring to cut off an edge image so only pixels computed from real neighbours are correlated.</summary>
    static int InsetFor(int w, int h) => Math.Min(3, Math.Max(1, (Math.Min(w, h) - 8) / 2));

    static Mat TemplateEdge(Scaled sc)
    {
        lock (sc) return sc.Edge ??= EdgeOf(sc.Gray);
    }

    // The frame's half-size edge image, made once per frame and shared by every target and scale (FrameContext owns only full size).
    static readonly ConditionalWeakTable<FrameContext, Mat> FrameHalfEdges = new();
    static Mat FrameHalfEdge(FrameContext ctx) => FrameHalfEdges.GetValue(ctx, c => EdgeOf(c.Half));

    // The template's half-size edge image, cached beside Scaled (which cannot hold extra fields for us).
    static readonly ConditionalWeakTable<Scaled, Mat> HalfTemplateEdges = new();
    static Mat HalfTemplateEdge(Scaled sc) => HalfTemplateEdges.GetValue(sc, s => EdgeOf(s.Half!));

    /// <summary>Best placements of the template's shape in the frame (or in <paramref name="hint"/>), best first.</summary>
    public static List<Detection> Find(FrameContext ctx, Mat tpl, double scale, Rect? hint, double floor = Floor)
    {
        List<Detection> last = [];
        foreach (var s in FindSteps(ctx, tpl, scale, hint, floor)) if (s != null) last = s;
        return last;
    }

    /// <summary>Same as <see cref="Find"/> in slices (the search edge image, then bands of the coarse map, then verification); null = more to do.</summary>
    public static IEnumerable<List<Detection>?> FindSteps(FrameContext ctx, Mat tpl, double scale, Rect? hint, double floor = Floor)
    {
        var sc = Prepared.Of(tpl).At(scale);
        if (sc.W < 16 || sc.H < 12) { yield return []; yield break; }
        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        var area = hint is { } h ? h.Intersect(bounds) : bounds;
        if (area.Width < sc.W || area.Height < sc.H) { yield return []; yield break; }

        // the full-resolution template edge, cropped to its interior: the part of its shape we can trust everywhere
        var te = TemplateEdge(sc);
        int ins = InsetFor(te.Width, te.Height);
        using var tei = new Mat(te, new Rect(ins, ins, te.Width - 2 * ins, te.Height - 2 * ins));
        Cv2.MeanStdDev(tei, out var tmean, out var tsd);
        if (tsd.Val0 < 2) { yield return []; yield break; }   // a template with almost no edges (a flat colour) has no shape to match

        // ---- coarse pass on the cheaper of the two edge images ----
        Mat cte;            // interior edge of the template at the search resolution (a view: do not dispose the owner)
        Rect sa;            // the searched area in search-image coordinates
        Point org;          // origin of the edge image below inside the search image
        double res;         // search pixels per frame pixel
        int sins;           // border inset used on the search-resolution template edge
        Mat search;         // the gray image the search edge image is built from
        bool whole;
        Mat? he = sc.Half is null ? null : HalfTemplateEdge(sc);
        int hins = he is null ? 0 : InsetFor(he.Width, he.Height);
        if (he != null && he.Width - 2 * hins >= 8 && he.Height - 2 * hins >= 5)
        {
            res = 0.5; sins = hins; search = ctx.Half; cte = he;
            // half pixels touched by the hinted/full area; the last column/row is dropped when the frame size is odd
            int ax = area.X / 2, ay = area.Y / 2;
            sa = new Rect(ax, ay, Math.Min(search.Width, (area.Right + 1) / 2) - ax, Math.Min(search.Height, (area.Bottom + 1) / 2) - ay);
        }
        else
        {
            res = 1; sins = ins; search = ctx.Gray; cte = te;
            sa = area;
        }
        if (sa.Width < cte.Width - 2 * sins || sa.Height < cte.Height - 2 * sins) { yield return []; yield break; }

        // the edge image covers the searched area plus a Ring apron so every correlated window sits on real (non-reflected) values;
        // when that is nearly the whole image anyway, use the shared per-frame edge image instead of a private crop
        var crop = new Rect(sa.X - Ring, sa.Y - Ring, sa.Width + 2 * Ring, sa.Height + 2 * Ring).Intersect(new Rect(0, 0, search.Width, search.Height));
        whole = crop.Width >= search.Width - 2 && crop.Height >= search.Height - 2;
        Mat ec;
        if (whole) { ec = res == 1 ? ctx.Edge : FrameHalfEdge(ctx); org = new Point(0, 0); }
        else
        {
            using var sub = new Mat(search, crop);
            ec = EdgeOf(sub);
            org = new Point(crop.X, crop.Y);
        }
        using var cteView = new Mat(cte, new Rect(sins, sins, cte.Width - 2 * sins, cte.Height - 2 * sins));
        Cv2.MeanStdDev(cteView, out var cmean, out var csd);
        yield return null;

        // the coarse map, filled band by band; map[j] correlates the interior edge at search-image position (sa + j)
        int mapW = sa.Width - cteView.Width + 1, mapH = sa.Height - cteView.Height + 1;
        using var map = new Mat(mapH, mapW, MatType.CV_32F);
        var cands = new List<Point>();
        try
        {
            int bands = Math.Clamp(map.Rows / 200, 1, 6);
            for (int b = 0; b < bands; b++)
            {
                int r0 = map.Rows * b / bands, r1 = map.Rows * (b + 1) / bands;
                using var src = new Mat(ec, new Rect(sa.X - org.X, sa.Y + r0 - org.Y, mapW + cteView.Width - 1, r1 - r0 + cteView.Height - 1));
                using var dst = new Mat(map, new Rect(0, r0, map.Width, r1 - r0));
                Cv2.MatchTemplate(src, cteView, dst, TemplateMatchModes.CCoeffNormed);
                if (b < bands - 1) yield return null;
            }
            Cv2.PatchNaNs(map, 0);

            // ---- nominations, best first, spaced like the template ----
            double gc = Math.Min(Gather, floor - 0.40);
            for (int tries = 0; tries < 40 && cands.Count < 20; tries++)
            {
                Cv2.MinMaxLoc(map, out _, out double mx, out _, out Point loc);
                if (mx < gc) break;
                var kill = new Rect(Math.Max(0, loc.X - cteView.Width / 2), Math.Max(0, loc.Y - cteView.Height / 2), cteView.Width, cteView.Height).Intersect(new Rect(0, 0, map.Width, map.Height));
                using (var m = new Mat(map, kill)) m.SetTo(Scalar.All(-2));
                // a window that is nearly flat has no shape to line up with: correlation there is numerically meaningless
                using (var win = new Mat(ec, new Rect(sa.X + loc.X - org.X, sa.Y + loc.Y - org.Y, cteView.Width, cteView.Height)))
                {
                    Cv2.MeanStdDev(win, out var wmean, out var wsd);
                    if (wmean.Val0 < 0.4 * cmean.Val0 || wsd.Val0 < 0.4 * csd.Val0) continue;
                }
                cands.Add(new Point((int)Math.Round((sa.X + loc.X - sins) / res), (int)Math.Round((sa.Y + loc.Y - sins) / res)));
                if (tries % 8 == 7) yield return null;
            }
        }
        finally { if (!whole) ec.Dispose(); }
        if (cands.Count == 0) { yield return []; yield break; }
        yield return null;

        // ---- full-resolution verification of each nomination ----
        var found = new List<Detection>();
        for (int i = 0; i < cands.Count && found.Count < 4; i++)
        {
            if (Verify(ctx, sc, tei, ins, tmean.Val0, tsd.Val0, cands[i], bounds, scale, floor) is { } d
                && !found.Any(f => Math.Abs(f.Center.X - d.Center.X) < Math.Min(sc.W, sc.H) / 2 && Math.Abs(f.Center.Y - d.Center.Y) < Math.Min(sc.W, sc.H) / 2))
                found.Add(d);
            if (i % 8 == 7) yield return null;
        }
        yield return found.Count > 1 ? found.OrderByDescending(f => f.Score).ToList() : found;
    }

    /// <summary>
    /// Re-score the template around one nominated position: edge-extract a small gray crop at full resolution, correlate the
    /// interior template edge there, take the best spot within a few pixels, then apply the occlusion-tolerant block score.
    /// </summary>
    static Detection? Verify(FrameContext ctx, Scaled sc, Mat tei, int ins, double tmean, double tsd, Point est, Rect bounds, double scale, double floor)
    {
        const int V = 5;    // how far the fine search may wander from the nomination (covers half-pixel rounding and peak spread)
        var want = new Rect(est.X - V - Ring, est.Y - V - Ring, sc.W + 2 * (V + Ring), sc.H + 2 * (V + Ring)).Intersect(bounds);
        int kx0 = Math.Max(0, est.X + ins - V - want.X), kx1 = Math.Min(want.Width - tei.Width, est.X + ins + V - want.X);
        int ky0 = Math.Max(0, est.Y + ins - V - want.Y), ky1 = Math.Min(want.Height - tei.Height, est.Y + ins + V - want.Y);
        if (kx1 < kx0 || ky1 < ky0) return null;
        using var gg = new Mat(ctx.Gray, want);
        using var ge = EdgeOf(gg);
        using var gs = new Mat(ge, new Rect(kx0, ky0, kx1 - kx0 + tei.Width, ky1 - ky0 + tei.Height));
        using var fm = new Mat();
        Cv2.MatchTemplate(gs, tei, fm, TemplateMatchModes.CCoeffNormed);
        Cv2.PatchNaNs(fm, 0);
        Cv2.MinMaxLoc(fm, out _, out double whole, out _, out _);
        // among the (near-)best spots pick the one closest to the nomination: a stroke matches all along itself and taking
        // the first maximum would slide a tracked target a pixel at a time
        var at = Detector.PeakNear(fm, whole, new Point(est.X + ins - want.X - kx0, est.Y + ins - want.Y - ky0));
        var k = new Point(kx0 + at.X, ky0 + at.Y);
        using (var win = new Mat(ge, new Rect(k.X, k.Y, tei.Width, tei.Height)))
        {
            Cv2.MeanStdDev(win, out var wmean, out var wsd);
            if (wmean.Val0 < 0.4 * tmean || wsd.Val0 < 0.4 * tsd) return null;
        }
        double score = Robust(ge, tei, k, whole);
        if (score < floor) return null;
        return new Detection(new Rect(want.X + k.X - ins, want.Y + k.Y - ins, sc.W, sc.H), score, scale, "shape");
    }

    /// <summary>
    /// The match score with the worst quarter of the label ignored: the template is cut into four vertical blocks, each correlated on its own, and the
    /// best three are averaged. A mouse cursor, a tooltip or a spark of animation over part of the letters then does not lose the button, while a
    /// button with another label stays low in every block.
    /// </summary>
    static double Robust(Mat ge, Mat tei, Point loc, double whole)
    {
        const int K = 4;
        var scores = new List<double>(K);
        for (int b = 0; b < K; b++)
        {
            int x0 = tei.Width * b / K, x1 = tei.Width * (b + 1) / K;
            using var tb = new Mat(tei, new Rect(x0, 0, x1 - x0, tei.Height));
            Cv2.MeanStdDev(tb, out _, out var sd);
            if (sd.Val0 < 4) continue;                                   // a block with no strokes in it says nothing
            using var wb = new Mat(ge, new Rect(loc.X + x0, loc.Y, x1 - x0, tei.Height));
            using var r = new Mat();
            Cv2.MatchTemplate(wb, tb, r, TemplateMatchModes.CCoeffNormed);
            Cv2.PatchNaNs(r, 0);
            scores.Add(Math.Clamp(r.At<float>(0, 0), -1, 1));
        }
        if (scores.Count < 3) return whole;
        scores.Sort();
        return Math.Max(whole, scores.Skip(1).Average());                 // never worse than the plain score
    }

    /// <summary>Re-confirm a shape match at (almost) its last place.</summary>
    public static Detection? Recheck(FrameContext ctx, Mat tpl, Detection last, int radius = 10)
    {
        var win = new Rect(last.Rect.X - radius, last.Rect.Y - radius, last.Rect.Width + 2 * radius, last.Rect.Height + 2 * radius);
        var r = Find(ctx, tpl, last.Scale, win, Floor - 0.1);
        return r.Count > 0 ? r[0] : null;
    }
}
