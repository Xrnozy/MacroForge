using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <param name="Method">"template" (whole-image match), "shape" (strokes only: ignores colour and light-on-dark vs dark-on-light), "patches" (part voting, survives occlusion) or "features" (AKAZE + RANSAC, survives rotation and occlusion).</param>
/// <param name="Visible">Fraction of the target that supported this match (1.0 = fully visible).</param>
/// <param name="Anchor">Where the target's centre is when the box is a bounding box of a transformed outline.</param>
public readonly record struct Detection(Rect Rect, double Score, double Scale, string Method = "template", double Visible = 1.0, Point? Anchor = null)
{
    public Point Center => Anchor ?? new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);

    public Detection Shift(int dx, int dy) =>
        this with { Rect = new Rect(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height), Anchor = Anchor is { } a ? new Point(a.X + dx, a.Y + dy) : null };
}

/// <summary>
/// Training-free template finder. Work is split into stages of very different cost so a scheduler can spend them wisely:
/// <see cref="Verify"/> (tracked target, window of a few pixels, sub-millisecond), <see cref="Light"/> (whole-frame match at the likely scales),
/// <see cref="DeepStage"/> (scale sweep, part voting, feature matching). Everything derived from the frame lives in <see cref="FrameContext"/>
/// and is shared by all targets.
/// </summary>
public sealed class Detector
{
    const int MaxPeaks = 8;
    /// <summary>A target that is being tracked stays tracked while its score is within this of the acceptance threshold.</summary>
    const double TrackSlack = 0.06;
    readonly Dictionary<string, Detection> _last = new();

    // ---------- per-frame convenience API (tests, one-off use) ----------

    public List<Detection> Find(Mat frame, Mat tpl, double threshold, double baseScale = 1, Rect? hint = null, bool occlusion = true, double minVisible = 0.45)
        => Search(frame, tpl, threshold, baseScale, hint, out _, occlusion, minVisible);

    public List<Detection> Search(Mat frame, Mat tpl, double threshold, double baseScale, Rect? hint, out double bestScore, bool occlusion = true, double minVisible = 0.45)
    {
        using var ctx = new FrameContext(frame);
        return Search(ctx, tpl, threshold, baseScale, hint, out bestScore, occlusion, minVisible);
    }

    /// <summary>All stages in order, stopping at the first that finds the target. <paramref name="minVisible"/> gates part voting.</summary>
    public List<Detection> Search(FrameContext ctx, Mat tpl, double threshold, double baseScale, Rect? hint, out double bestScore, bool occlusion = true, double minVisible = 0.45)
    {
        bestScore = -1;
        var prep = Prepared.Of(tpl);
        var r = TemplateStage(ctx, prep, threshold, ScaleOrder(baseScale).ToList(), hint, ref bestScore);
        if (r.Count > 0 || !occlusion) return r;
        // The feature pass towers over the other deep stages (~1 s of AKAZE vs ~0.2 s combined) and answers an independent
        // question, so it starts now — on private copies — and whatever a cheaper stage finds meanwhile still wins.
        var feature = FeatureAhead(ctx, tpl, hint);
        r = PatchStage(ctx, tpl, baseScale, hint, minVisible);
        if (r.Count > 0) return r;
        r = ShapeStage(ctx, tpl, baseScale, hint);
        if (r.Count > 0) return r;
        return feature?.Result ?? FeatureStage(ctx, tpl, hint);
    }

    /// <summary>
    /// Runs the feature stage on its own copies of the frame and template. An earlier stage may answer and return while it is
    /// still running: it owns everything it touches and simply finishes in the background. Null when the stage cannot possibly
    /// match (below AKAZE's 32 px floor, the same check <see cref="FeatureMatching"/> makes first).
    /// </summary>
    static Task<List<Detection>>? FeatureAhead(FrameContext ctx, Mat tpl, Rect? hint)
    {
        if (Math.Min(tpl.Width, tpl.Height) < 32) return null;
        var t = tpl.Clone();
        var shot = ctx.Bgr.Clone();
        var origin = ctx.Origin;
        return Task.Run(() =>
        {
            try
            {
                using var fctx = new FrameContext(shot, origin, owns: true);
                using var tc = t;
                return FeatureStage(fctx, tc, hint);
            }
            catch { try { shot.Dispose(); t.Dispose(); } catch { } return []; }   // a background copy must never fault the search
        });
    }

    // ---------- staged API (used by the scheduler) ----------

    /// <summary>Whole-frame (or hint-window) match at the two likely scales: this PC's DPI ratio and 1.0.</summary>
    /// <param name="extraScales">Scales to try before the usual two, best guess first (the last good scale, the scales other monitors would give).</param>
    public List<Detection> Light(FrameContext ctx, Mat tpl, double threshold, double baseScale, Rect? hint, out double bestScore, IReadOnlyList<double>? extraScales = null)
    {
        bestScore = -1;
        var scales = LightScales(baseScale);
        if (extraScales != null)
            foreach (var s in extraScales.Select(x => Math.Round(x, 2)).Where(x => x >= 0.3 && x <= 4).Reverse()) { scales.Remove(s); scales.Insert(0, s); }
        return TemplateStage(ctx, Prepared.Of(tpl), threshold, scales, hint, ref bestScore);
    }

    public const int DeepStages = 4;

    /// <summary>stage 0 = remaining scales, 1 = part voting (covered targets), 2 = shape (same strokes in other colours: greyed out, highlighted, light on dark), 3 = AKAZE features (rotated / resized / covered).</summary>
    public List<Detection> DeepStage(FrameContext ctx, Mat tpl, double threshold, double baseScale, int stage, double minVisible)
        => Drain(DeepSteps(ctx, tpl, threshold, baseScale, stage, minVisible, sliced: false));   // one shot: the feature pass runs over the whole frame at once, which costs ~35% less CPU than bands

    /// <summary>
    /// The same work as <see cref="DeepStage"/> cut into slices of a few tens of ms (one scale, one seed, one band of the frame): every yielded
    /// null means "call MoveNext again later"; the last element is the result (empty = nothing found). The frame must stay unchanged in between.
    /// </summary>
    public IEnumerable<List<Detection>?> DeepSteps(FrameContext ctx, Mat tpl, double threshold, double baseScale, int stage, double minVisible, bool sliced = true) => stage switch
    {
        0 => sliced ? ScaleSteps(ctx, Prepared.Of(tpl), threshold, ScaleOrder(baseScale).Except(LightScales(baseScale)).ToList()) : SweepOnce(ctx, tpl, threshold, baseScale),
        1 => PatchSteps(ctx, tpl, baseScale, null, minVisible),
        2 => ShapeSteps(ctx, tpl, baseScale, null),
        _ => FeatureSteps(ctx, tpl, null, sliced),
    };

    static List<Detection> Drain(IEnumerable<List<Detection>?> steps)
    {
        List<Detection> last = [];
        foreach (var s in steps) if (s != null) last = s;
        return last;
    }

    /// <summary>The whole remaining scale sweep as one step (the non-sliced deep stage): the scales are independent, so they run in parallel.</summary>
    static IEnumerable<List<Detection>?> SweepOnce(FrameContext ctx, Mat tpl, double threshold, double baseScale)
    {
        double best = -1;
        yield return TemplateStage(ctx, Prepared.Of(tpl), threshold, ScaleOrder(baseScale).Except(LightScales(baseScale)).ToList(), null, ref best);
    }

    /// <summary>Runs the parts in order; the first part that ends with a match wins. Slices of every part pass through.</summary>
    static IEnumerable<List<Detection>?> FirstHit(IEnumerable<IEnumerable<List<Detection>?>> parts)
    {
        foreach (var part in parts)
        {
            List<Detection>? fin = null;
            foreach (var s in part) { if (s == null) yield return null; else fin = s; }
            if (fin is { Count: > 0 }) { yield return fin; yield break; }
        }
        yield return [];
    }

    static IEnumerable<List<Detection>?> ScaleSteps(FrameContext ctx, Prepared prep, double threshold, List<double> scales)
    {
        foreach (var s in scales)
        {
            var c = Candidates(ctx, prep, s, threshold - 0.15, null);
            if (c.Count > 0 && c[0].Score >= threshold) { yield return c.Where(d => d.Score >= threshold).ToList(); yield break; }
            yield return null;
        }
        yield return [];
    }

    /// <summary>
    /// Re-confirm a target at (almost) the place it was last seen. The window is a few pixels around the last position, so this costs
    /// well under a millisecond; it is how a tracked target stays cheap. Returns null when it no longer matches there.
    /// </summary>
    public Detection? Verify(FrameContext ctx, Mat tpl, Detection last, double threshold, double minVisible, bool occlusion, int radius = 10)
    {
        switch (last.Method)
        {
            case "shape":
                return ShapeMatching.Recheck(ctx, tpl, last);
            case "patches":
            {
                var p = PatchVoting.Recheck(ctx, tpl, last.Scale, new Point(last.Rect.X, last.Rect.Y), minVisible);
                return p;
            }
            case "features":
            {
                int pad = Math.Max(last.Rect.Width, last.Rect.Height) / 2 + 24;
                var area = new Rect(last.Rect.X - pad, last.Rect.Y - pad, last.Rect.Width + 2 * pad, last.Rect.Height + 2 * pad);
                var f = FeatureStage(ctx, tpl, area);
                return f.Count > 0 ? f[0] : null;
            }
        }
        var prep = Prepared.Of(tpl);
        var sc = prep.At(last.Scale);
        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        var win = new Rect(last.Rect.X - radius, last.Rect.Y - radius, sc.W + 2 * radius, sc.H + 2 * radius).Intersect(bounds);
        if (win.Width >= sc.W && win.Height >= sc.H)
        {
            using var view = new Mat(ctx.Gray, win);
            using var map = ScoreMap(view, sc.Gray);
            Cv2.MinMaxLoc(map, out _, out double score, out _, out _);
            var at = PeakNear(map, score, new Point(last.Rect.X - win.X, last.Rect.Y - win.Y));
            // already identified and still in the same place: appearance may drift (hover, highlight, antialiasing) without losing it
            if (score >= Math.Max(0.65, threshold - TrackSlack))
            {
                var rect = new Rect(win.X + at.X, win.Y + at.Y, sc.W, sc.H);
                if (ColorOk(ctx, rect, sc, prep.ColorTolerance)) return new Detection(rect, score, last.Scale);
            }
        }
        // something may have covered part of it: the visible parts still tell where it is
        return occlusion ? PatchVoting.Recheck(ctx, tpl, last.Scale, new Point(last.Rect.X, last.Rect.Y), minVisible) : null;
    }

    // ---------- tracking convenience (per-frame API) ----------

    /// <summary>Find with a remembered position: verify in place, then a window around it, then the whole frame.</summary>
    public List<Detection> FindTracked(string id, Mat frame, Mat tpl, double threshold, double baseScale = 1, bool occlusion = true, double minVisible = 0.45)
    {
        using var ctx = new FrameContext(frame);
        Detection? last;
        lock (_last) last = _last.TryGetValue(id, out var l) ? l : null;
        List<Detection> r = [];
        if (last is { } ld)
        {
            if (Verify(ctx, tpl, ld, threshold, minVisible, occlusion) is { } v) r = [v];
            else
            {
                var c = ld.Center;
                var hint = new Rect(c.X - 200 - tpl.Width / 2, c.Y - 200 - tpl.Height / 2, 400 + tpl.Width, 400 + tpl.Height);
                r = Search(ctx, tpl, threshold, baseScale, hint, out _, occlusion, minVisible);
            }
        }
        else r = Search(ctx, tpl, threshold, baseScale, null, out _, occlusion, minVisible);
        lock (_last) { if (r.Count > 0) _last[id] = r[0]; else _last.Remove(id); }
        return r;
    }

    /// <summary>
    /// Threshold from the data itself: blank the region out of the same frame, find the best look-alike left
    /// (the "impostor"), and sit between it and a perfect match. Clamped to 0.70-0.95.
    /// </summary>
    public double AutoThreshold(Mat frame, Rect region)
    {
        using var tpl = new Mat(frame, region).Clone();
        using var blank = frame.Clone();
        using (var sub = new Mat(blank, region)) sub.SetTo(Cv2.Mean(frame));
        using var ctx = new FrameContext(blank);
        var c = Candidates(ctx, Prepared.Of(tpl), 1, 0.0, null);
        double impostor = c.Count > 0 ? Math.Max(0, c[0].Score) : 0;
        return Math.Clamp((impostor + 0.98) / 2, 0.70, 0.95);
    }

    // ---------- identity ----------

    /// <summary>
    /// Re-order matches by how well the pixels AROUND each one agree with the saved surroundings (the target itself is masked out).
    /// This is what tells two visually identical buttons apart: "the Play button next to the Server A label".
    /// Matches at another scale, or whose surroundings are off-screen, keep their score order after the ones that could be compared.
    /// </summary>
    public List<Detection> RankByContext(FrameContext ctx, List<Detection> hits, Mat context, int pad) => RankWithRing(ctx, hits, context, pad).Select(x => x.Item1).ToList();

    /// <summary>Same ordering as <see cref="RankByContext"/>, with each match's surroundings similarity (0..1, -1 = could not be compared).</summary>
    public List<(Detection, double)> RankWithRing(FrameContext ctx, List<Detection> hits, Mat context, int pad)
    {
        var scored = hits.Select(h => (H: h, Ring: Math.Abs(h.Scale - 1) < 0.05 ? RingSimilarity(ctx, h.Rect, context, pad) : -1)).ToList();
        // a clearly better surrounding wins; near-ties fall back to the match score
        return scored.OrderByDescending(x => Math.Round(x.Ring, 1)).ThenByDescending(x => x.H.Score).Select(x => (x.H, x.Ring)).ToList();
    }

    /// <summary>0..1, 1 = identical surroundings. -1 when nothing around the match can be compared.</summary>
    static double RingSimilarity(FrameContext ctx, Rect r, Mat context, int pad)
    {
        var want = new Rect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad);
        var vis = want.Intersect(new Rect(0, 0, ctx.Width, ctx.Height));
        var ctxRect = new Rect(vis.X - want.X, vis.Y - want.Y, vis.Width, vis.Height);
        if (vis.Width < 4 || vis.Height < 4 || ctxRect.Right > context.Width || ctxRect.Bottom > context.Height) return -1;
        using var a = new Mat(ctx.Bgr, vis);
        using var b = new Mat(context, ctxRect);
        using var diff = new Mat(); Cv2.Absdiff(a, b, diff);
        using var mask = new Mat(vis.Height, vis.Width, MatType.CV_8UC1, Scalar.All(255));
        var inner = new Rect(r.X - vis.X, r.Y - vis.Y, r.Width, r.Height).Intersect(new Rect(0, 0, vis.Width, vis.Height));
        if (inner.Width > 0 && inner.Height > 0) { using var m = new Mat(mask, inner); m.SetTo(Scalar.All(0)); }
        if (Cv2.CountNonZero(mask) < 16) return -1;
        var mean = Cv2.Mean(diff, mask);
        return Math.Clamp(1 - (mean.Val0 + mean.Val1 + mean.Val2) / 3 / 80, 0, 1);
    }

    // ---------- stages ----------

    static List<Detection> TemplateStage(FrameContext ctx, Prepared prep, double threshold, List<double> scales, Rect? hint, ref double bestScore)
    {
        scales = scales.Distinct().ToList();        // a repeated scale is pure waste — and two parallel passes must not share its Scaled
        if (scales.Count == 0) return [];
        double floor = threshold - 0.15;            // coarse peaks must be this close under the threshold to get a full-size re-match
        // The first scale is by far the most likely (this PC's DPI ratio, or the last good one): it runs alone so the usual hit
        // returns without paying for the sweep. The hint window and its full-frame fallback only ever apply to it.
        var c = Candidates(ctx, prep, scales[0], floor, hint);
        if (hint != null && !c.Any(d => d.Score >= threshold)) c = Candidates(ctx, prep, scales[0], floor, null); // moved out of the hint window
        if (c.Count > 0) bestScore = Math.Max(bestScore, c[0].Score);
        if (c.Count > 0 && c[0].Score >= threshold) return c.Where(d => d.Score >= threshold).ToList();

        // The rest of the sweep is independent work (one correlation map per scale) and, when the target is absent, all of it
        // runs: fan it out over the cores. Per-scale matching stays single-threaded, and results are judged in scale order,
        // so the answer is identical to the sequential sweep — it just arrives in a fraction of the wall time.
        if (scales.Count > 2)
        {
            _ = ctx.Gray; _ = ctx.Half;             // materialise the lazy pyramid before the parallel pass reads it
            var hits = new List<Detection>?[scales.Count - 1];
            try
            {
                Parallel.For(0, scales.Count - 1, k =>
                {
                    var r = Candidates(ctx, prep, scales[k + 1], floor, null);
                    if (r.Count > 0) hits[k] = r;
                });
            }
            catch (AggregateException a) when (a.InnerExceptions.Count > 0) { throw a.InnerExceptions[0]; }
            foreach (var r in hits)
            {
                if (r is not { Count: > 0 }) continue;
                bestScore = Math.Max(bestScore, r[0].Score);
                if (r[0].Score >= threshold) return r.Where(d => d.Score >= threshold).ToList();
            }
            return [];
        }
        for (int i = 1; i < scales.Count; i++)
        {
            c = Candidates(ctx, prep, scales[i], floor, null);
            if (c.Count > 0) bestScore = Math.Max(bestScore, c[0].Score);
            if (c.Count > 0 && c[0].Score >= threshold) return c.Where(d => d.Score >= threshold).ToList();
        }
        return [];
    }

    /// <summary>The target's strokes, whatever colour they are drawn in (a greyed-out or recoloured button still matches; a button with another label does not).</summary>
    static List<Detection> ShapeStage(FrameContext ctx, Mat tpl, double baseScale, Rect? hint) => Drain(ShapeSteps(ctx, tpl, baseScale, hint));

    static IEnumerable<List<Detection>?> ShapeSteps(FrameContext ctx, Mat tpl, double baseScale, Rect? hint) =>
        FirstHit(Scales(baseScale).SelectMany(s => hint is null ? new[] { ShapeMatching.FindSteps(ctx, tpl, s, null) } : new[] { ShapeMatching.FindSteps(ctx, tpl, s, hint), ShapeMatching.FindSteps(ctx, tpl, s, null) }));

    static List<Detection> PatchStage(FrameContext ctx, Mat tpl, double baseScale, Rect? hint, double minVisible) => Drain(PatchSteps(ctx, tpl, baseScale, hint, minVisible));

    static IEnumerable<List<Detection>?> PatchSteps(FrameContext ctx, Mat tpl, double baseScale, Rect? hint, double minVisible) =>
        FirstHit(Scales(baseScale).SelectMany(s => hint is null ? new[] { PatchVoting.FindSteps(ctx, tpl, s, minVisible, null) } : new[] { PatchVoting.FindSteps(ctx, tpl, s, minVisible, hint), PatchVoting.FindSteps(ctx, tpl, s, minVisible, null) }));

    static double[] Scales(double baseScale) => baseScale == 1 ? [1.0] : [baseScale, 1.0];

    static List<Detection> FeatureStage(FrameContext ctx, Mat tpl, Rect? hint) => Drain(FeatureSteps(ctx, tpl, hint, sliced: false));

    static IEnumerable<List<Detection>?> FeatureSteps(FrameContext ctx, Mat tpl, Rect? hint, bool sliced = true) =>
        FirstHit(hint is null ? new[] { FeatureMatching.FindSteps(ctx, tpl, null, sliced) } : new[] { FeatureMatching.FindSteps(ctx, tpl, hint, sliced), FeatureMatching.FindSteps(ctx, tpl, null, sliced) });

    static List<double> LightScales(double baseScale)
    {
        var l = new List<double> { Math.Round(baseScale, 2) };
        if (Math.Abs(baseScale - 1) > 0.005) l.Add(1.0);
        return l;
    }

    static IEnumerable<double> ScaleOrder(double baseScale)
    {
        var seen = new HashSet<double>();
        double[] sweep = [1, 0.93, 1.07, 0.87, 1.14, 0.8, 1.25];
        foreach (var origin in baseScale == 1 ? new[] { 1.0 } : new[] { baseScale, 1.0 })
            foreach (var k in sweep)
            {
                double s = Math.Round(origin * k, 2);
                if (s >= 0.3 && s <= 4 && seen.Add(s)) yield return s;
            }
    }

    /// <summary>Scored placements of the template at one scale, best first, after NMS. Floor = lowest coarse score kept.</summary>
    static List<Detection> Candidates(FrameContext ctx, Prepared prep, double scale, double floor, Rect? hint)
    {
        var sc = prep.At(scale);
        int tw = sc.W, th = sc.H;
        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        var area = hint is { } h ? h.Intersect(bounds) : bounds;
        if (area.Width < tw || area.Height < th || tw < 4 || th < 4) return [];

        // coarse search runs on the shared half-size gray (a view into it, no copy), full-size gray when the template is tiny
        double d = sc.Half != null ? 0.5 : 1;
        Mat g; int ox, oy;
        if (d == 1) { g = new Mat(ctx.Gray, area); ox = area.X; oy = area.Y; }
        else
        {
            int ax = area.X / 2, ay = area.Y / 2;
            g = new Mat(ctx.Half, new Rect(ax, ay, Math.Min(area.Width / 2, ctx.Half.Width - ax), Math.Min(area.Height / 2, ctx.Half.Height - ay)));
            ox = ax * 2; oy = ay * 2;
        }
        using (g)
        {
            var tg = d == 1 ? sc.Gray : sc.Half!;
            if (g.Width < tg.Width || g.Height < tg.Height) return [];
            using var map = ScoreMap(g, tg);

            var found = new List<Detection>();
            double coarseFloor = Math.Max(0.3, floor - 0.1);
            var mapBounds = new Rect(0, 0, map.Width, map.Height);
            var killVal = Scalar.All(-2);
            for (int k = 0; k < MaxPeaks; k++)
            {
                Cv2.MinMaxLoc(map, out _, out double mx, out _, out Point loc);
                if (mx < coarseFloor) break;
                var kill = new Rect(Math.Max(0, loc.X - tg.Width / 2), Math.Max(0, loc.Y - tg.Height / 2), tg.Width, tg.Height).Intersect(mapBounds);
                using (var m = new Mat(map, kill)) m.SetTo(killVal);

                int x0 = ox + (int)(loc.X / d), y0 = oy + (int)(loc.Y / d);
                var rr = new Rect(x0 - 4, y0 - 4, tw + 8, th + 8).Intersect(bounds);
                if (rr.Width < tw || rr.Height < th) continue;
                using var win = new Mat(ctx.Gray, rr);                 // structure only: a colour change barely moves this score
                using var fine = ScoreMap(win, sc.Gray);
                Cv2.MinMaxLoc(fine, out _, out double best, out _, out Point at);
                if (best < floor) continue;
                var rect = new Rect(rr.X + at.X, rr.Y + at.Y, tw, th);
                if (ColorOk(ctx, rect, sc, prep.ColorTolerance)) found.Add(new Detection(rect, best, scale)); // a different colour is a different thing
            }
            return Nms(found);
        }
    }

    /// <summary>The colour of the matched region is close enough to the target's colour (brightness ignored).</summary>
    static bool ColorOk(FrameContext ctx, Rect rect, Scaled sc, double tolerance)
    {
        if (tolerance >= 250) return true;
        using var region = new Mat(ctx.Bgr, rect);
        return ColorMath.Similar(Cv2.Mean(region), sc.MeanBgr, tolerance);
    }

    static List<Detection> Nms(List<Detection> c)
    {
        var kept = new List<Detection>();
        foreach (var x in c.OrderByDescending(x => x.Score))
            if (!kept.Any(k => Math.Abs(k.Center.X - x.Center.X) < Math.Min(x.Rect.Width, x.Rect.Height) / 2 && Math.Abs(k.Center.Y - x.Center.Y) < Math.Min(x.Rect.Width, x.Rect.Height) / 2))
                kept.Add(x);
        return kept;
    }

    /// <summary>
    /// The position among the (near-)best ones that is closest to <paramref name="expected"/>. A stroke or edge matches equally well all along itself;
    /// taking the first maximum would always pick the same end of it and make a tracked target slide a few pixels every check.
    /// </summary>
    internal static Point PeakNear(Mat map, double max, Point expected, double tol = 0.01)
    {
        using var bin = new Mat();
        Cv2.InRange(map, new Scalar(max - tol), new Scalar(max), bin);   // one pass, straight to an 8U mask
        Cv2.FindNonZero(bin, bin);
        Point best = expected; double bd = double.MaxValue;
        int rows = bin.Rows;
        for (int i = 0; i < rows; i++)
        {
            var p = bin.Get<Point>(i);
            double d = (double)(p.X - expected.X) * (p.X - expected.X) + (double)(p.Y - expected.Y) * (p.Y - expected.Y);
            if (d < bd) { bd = d; best = p; }
        }
        return bd == double.MaxValue ? expected : best;
    }

    /// <summary>Mean over channels of normalised correlation (higher = better). Flat channels fall back to a squared-difference score.</summary>
    internal static Mat ScoreMap(Mat img, Mat tpl)
    {
        int n = img.Channels();
        if (n == 1) return OneChannel(img, tpl);
        var imgCh = img.Split(); var tCh = tpl.Split();
        var sum = new Mat(img.Rows - tpl.Rows + 1, img.Cols - tpl.Cols + 1, MatType.CV_32F, Scalar.All(0));
        using var r = new Mat();                                   // matchTemplate re-uses this buffer for every channel
        for (int c = 0; c < n; c++)
        {
            OneChannel(imgCh[c], tCh[c], r);
            Cv2.Add(sum, r, sum);
            imgCh[c].Dispose(); tCh[c].Dispose();
        }
        sum.ConvertTo(sum, MatType.CV_32F, 1.0 / n);
        return sum;
    }

    /// <summary>Per-template match choice (normalised correlation vs. squared difference): depends only on the template's own spread, so it is computed once.</summary>
    sealed class TplStat { public bool Flat; }
    static readonly ConditionalWeakTable<Mat, TplStat> TplStats = new();

    static Mat OneChannel(Mat img, Mat tpl)
    {
        var r = new Mat();
        OneChannel(img, tpl, r);
        return r;
    }

    /// <summary>
    /// Normalised cross-correlation for one channel pair. CCOEFF_NORMED (not CCORR_NORMED) is the right mode for UI targets:
    /// it subtracts each patch's mean and divides by its energy, so a hover tint, a gain change or antialiasing on the same
    /// strokes does not move the score, while a patch with different content still drops. A flat template has no variance to
    /// normalise, so it falls back to 1 - normed squared difference.
    /// </summary>
    static void OneChannel(Mat img, Mat tpl, Mat r)
    {
        var stat = TplStats.GetValue(tpl, t =>
        {
            Cv2.MeanStdDev(t, out _, out var sd);
            return new TplStat { Flat = sd.Val0 < 1.5 };
        });
        if (stat.Flat)
        {
            Cv2.MatchTemplate(img, tpl, r, TemplateMatchModes.SqDiffNormed);
            Cv2.Subtract(Scalar.All(1), r, r);
        }
        else Cv2.MatchTemplate(img, tpl, r, TemplateMatchModes.CCoeffNormed);
        Cv2.PatchNaNs(r, 0);
    }
}
