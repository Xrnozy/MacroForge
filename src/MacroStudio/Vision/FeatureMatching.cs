using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// AKAZE keypoints + ratio test + RANSAC homography. Needs a textured target (icons, artwork, text), but then
/// survives rotation, large resizing and heavy occlusion because any ~10 consistent keypoints define the outline.
/// </summary>
internal static class FeatureMatching
{
    sealed class Feat { public Point2f[] Pts = []; public Mat Desc = new(); }
    static readonly ConditionalWeakTable<Mat, Feat> Cache = new();

    const int MinTemplateKeypoints = 12, MinInliers = 6, Pad = 48, MinSide = 32;

    /// <summary>
    /// AKAZE drops keypoints near the image edge, which empties a small cropped template. Pad the template with a
    /// replicated border, detect, and keep only keypoints that fall inside the original pixels. The template pass is
    /// cached per template image and stays maximally permissive: it is the frame pass that is tuned for speed.
    /// </summary>
    static Feat Of(Mat tpl) => Cache.GetValue(tpl, t =>
    {
        using var gray = new Mat(); Cv2.CvtColor(t, gray, ColorConversionCodes.BGR2GRAY);
        using var padded = new Mat(); Cv2.CopyMakeBorder(gray, padded, Pad, Pad, Pad, Pad, BorderTypes.Replicate);
        using var akaze = AKAZE.Create(threshold: 0.0001f);
        using var desc = new Mat();
        akaze.DetectAndCompute(padded, null, out KeyPoint[] kp, desc);
        var keep = Enumerable.Range(0, kp.Length)
            .Where(i => kp[i].Pt.X - Pad >= -2 && kp[i].Pt.Y - Pad >= -2 && kp[i].Pt.X - Pad <= t.Width + 2 && kp[i].Pt.Y - Pad <= t.Height + 2).ToArray();
        var f = new Feat { Pts = keep.Select(i => new Point2f(kp[i].Pt.X - Pad, kp[i].Pt.Y - Pad)).ToArray() };
        if (keep.Length > 0)
        {
            f.Desc = new Mat(keep.Length, desc.Cols, desc.Type());
            var src = desc.AsRows<byte>(); var dst = f.Desc.AsRows<byte>();
            for (int j = 0; j < keep.Length; j++) src[keep[j]].CopyTo(dst[j]);
        }
        return f;
    });

    /// <summary>Rows per AKAZE slice, and the extra rows read above and below a slice so its keypoints near the cut see the same surroundings as in one pass.</summary>
    const int BandRows = 96, BandOverlap = 32;

    // Frame-pass AKAZE settings. Threshold and per-octave layer count must match the (cached) template pass: real
    // rotated-icon matches ride on the very weakest keypoints (raising the threshold even to 2e-4 lost them) and on the
    // same scale-space sampling on both sides. The fourth octave — a pyramid level only ~135-270 rows tall on a 1080p
    // frame — buys scale range no UI target needs (matches are capped near ~2.2x by the area-ratio sanity check), so
    // the frame pyramid stops at three octaves while the template keeps four; three frame octaves still cover a target
    // rendered several times bigger than recorded.
    const float FrameThreshold = 0.0001f;
    const int FrameOctaveLayers = 4;
    const int FrameOctaves = 3;

    /// <summary>
    /// AKAZE keypoints of one frame area, computed once and shared by every target that needs them in this frame. A big area is done in horizontal
    /// bands, one per step (each band keeps only the keypoints in its own rows), so no step takes the ~0.5 s a full 1080p pass needs. When an
    /// earlier pass already covered a bigger area, the window's keypoints are just filtered out of it — no new detection run.
    /// </summary>
    static IEnumerable<(KeyPoint[] Kp, Mat Desc)?> FrameFeatureSteps(FrameContext ctx, Rect area, bool sliced)
    {
        (KeyPoint[], Mat)? cached = null;
        lock (ctx)   // the clip runs under the lock: a concurrent bigger/same-area pass could swap and dispose the cached matrix
        {
            if (ctx.FeatureKp != null && ctx.FeatureDesc != null)
            {
                if (ctx.FeatureArea == area) cached = (ctx.FeatureKp, ctx.FeatureDesc);
                else if (ctx.FeatureArea.Contains(area)) cached = Subset(ctx.FeatureKp, ctx.FeatureDesc, ctx.FeatureArea, area);
            }
        }
        if (cached != null) { yield return cached; yield break; }
        int n = sliced ? Math.Max(1, (int)Math.Round(area.Height / (double)BandRows)) : 1;   // one shot: a single pass is cheaper than bands (no overlap rows)
        var kps = new List<KeyPoint>(); var chunks = new List<(byte[] Data, int Rows)>(); int cols = 0;
        using var akaze = AKAZE.Create(threshold: FrameThreshold, nOctaves: FrameOctaves, nOctaveLayers: FrameOctaveLayers);
        for (int b = 0; b < n; b++)
        {
            int c0 = area.Height * b / n, c1 = area.Height * (b + 1) / n;
            int y0 = n == 1 ? 0 : Math.Max(0, c0 - BandOverlap), y1 = n == 1 ? area.Height : Math.Min(area.Height, c1 + BandOverlap);
            using var gray = new Mat(ctx.Gray, new Rect(area.X, area.Y + y0, area.Width, y1 - y0));
            using var d = new Mat();
            akaze.DetectAndCompute(gray, null, out KeyPoint[] kp, d);
            if (kp.Length > 0 && !d.Empty())
            {
                cols = Math.Max(cols, d.Cols);
                var rows = d.AsRows<byte>();
                var chunk = new byte[kp.Length * cols];          // upper bound; a few overlap rows are dropped below
                int cnt = 0;
                for (int i = 0; i < kp.Length; i++)
                {
                    double y = kp[i].Pt.Y + y0;
                    if (y < c0 || (y >= c1 && b < n - 1)) continue;          // another band's rows
                    var k = kp[i]; k.Pt = new Point2f(k.Pt.X, (float)y);
                    kps.Add(k);
                    rows[i].CopyTo(chunk.AsSpan(cnt * cols, cols)); cnt++;
                }
                if (cnt > 0) chunks.Add((chunk, cnt));
            }
            if (b < n - 1) yield return null;
        }
        var desc = new Mat();
        if (kps.Count > 0)
        {
            desc.Create(kps.Count, cols, MatType.CV_8UC1);
            var dst = desc.AsRows<byte>();
            int r = 0;
            foreach (var (data, rows) in chunks)
                for (int j = 0; j < rows; j++, r++)
                    data.AsSpan(j * cols, cols).CopyTo(dst[r]);
        }
        var arr = kps.ToArray();
        lock (ctx)
        {
            ctx.FeatureDesc?.Dispose();
            ctx.FeatureArea = area; ctx.FeatureKp = arr; ctx.FeatureDesc = desc;
        }
        yield return (arr, desc);
    }

    /// <summary>The keypoints a bigger cached pass found inside <paramref name="area"/>, rebased to area coordinates, with their descriptor rows.</summary>
    static (KeyPoint[], Mat) Subset(KeyPoint[] kp, Mat desc, Rect sup, Rect area)
    {
        int x0 = area.X - sup.X, y0 = area.Y - sup.Y, x1 = x0 + area.Width, y1 = y0 + area.Height;
        var keep = new List<int>(kp.Length / 2);
        for (int i = 0; i < kp.Length; i++)
            if (kp[i].Pt.X >= x0 && kp[i].Pt.X < x1 && kp[i].Pt.Y >= y0 && kp[i].Pt.Y < y1) keep.Add(i);
        if (keep.Count == 0) return ([], new Mat());
        var pts = new KeyPoint[keep.Count];
        var d = new Mat(keep.Count, desc.Cols, desc.Type());
        var src = desc.AsRows<byte>(); var dst = d.AsRows<byte>();
        for (int j = 0; j < keep.Count; j++)
        {
            var k = kp[keep[j]]; k.Pt = new Point2f(k.Pt.X - x0, k.Pt.Y - y0);
            pts[j] = k;
            src[keep[j]].CopyTo(dst[j]);
        }
        return (pts, d);
    }

    public static Detection? Find(FrameContext ctx, Mat tpl, Rect? hint)
    {
        List<Detection> last = [];
        foreach (var s in FindSteps(ctx, tpl, hint, sliced: false)) if (s != null) last = s;
        return last.Count > 0 ? last[0] : null;
    }

    /// <summary>Same as <see cref="Find"/> in slices (one AKAZE band per step, then the matching); null = more to do, the last element is the result (empty = none).</summary>
    public static IEnumerable<List<Detection>?> FindSteps(FrameContext ctx, Mat tpl, Rect? hint, bool sliced = true)
    {
        // shortcut: a target under 32 px on a side has too little texture for AKAZE and the frame pass costs ~0.5 s; patches and shape cover it
        if (Math.Min(tpl.Width, tpl.Height) < MinSide) { yield return []; yield break; }
        var tf = Of(tpl);
        if (tf.Pts.Length < MinTemplateKeypoints || tf.Desc.Empty()) { yield return []; yield break; }

        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        var area = hint is { } h ? h.Intersect(bounds) : bounds;
        if (area.Width < 16 || area.Height < 16) { yield return []; yield break; }
        (KeyPoint[] Kp, Mat Desc)? ff = null;
        foreach (var step in FrameFeatureSteps(ctx, area, sliced)) { if (step is { } x) ff = x; else yield return null; }
        var (fk, fd) = ff!.Value;
        // the cached descriptor matrix is owned by the frame context; a subset clipped out of a bigger pass is temporary
        bool owns = !ReferenceEquals(fd, ctx.FeatureDesc);
        try
        {
            // the frame's keypoint set cannot support the target: fewer points than a homography needs
            if (fk.Length < MinInliers || fd.Empty()) { yield return []; yield break; }
            yield return null;
            var res = Match(tpl, tf, fk, fd, area, bounds);
            yield return res is { } d ? [d] : [];
        }
        finally { if (owns) fd.Dispose(); }
    }

    /// <summary>RANSAC keeps at most this many attempts: ~99.97% success at the worst viable inlier share (0.3), half the default worst case.</summary>
    const int MaxRansacIters = 1000;

    static Detection? Match(Mat tpl, Feat tf, KeyPoint[] fk, Mat fd, Rect area, Rect bounds)
    {
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var knn = matcher.KnnMatch(tf.Desc, fd, 2);
        var good = knn.Where(m => m.Length == 2 && m[0].Distance < 0.85 * m[1].Distance).Select(m => m[0]).ToList();
        if (good.Count < MinInliers) return null;

        var src = good.Select(m => new Point2d(tf.Pts[m.QueryIdx].X, tf.Pts[m.QueryIdx].Y)).ToArray();
        var dst = good.Select(m => new Point2d(fk[m.TrainIdx].Pt.X + area.X, fk[m.TrainIdx].Pt.Y + area.Y)).ToArray(); // keypoints are relative to the searched area
        using var mask = new Mat();
        using var hm = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3, mask, MaxRansacIters);
        if (hm == null || hm.Empty()) return null;
        int inliers = Cv2.CountNonZero(mask);
        if (inliers < MinInliers || inliers < 0.3 * good.Count) return null;

        double w = tpl.Width, hgt = tpl.Height;
        var quad = Cv2.PerspectiveTransform([new Point2d(0, 0), new Point2d(w, 0), new Point2d(w, hgt), new Point2d(0, hgt)], hm);
        var contour = quad.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
        double areaRatio = Cv2.ContourArea(contour) / (w * hgt);
        if (!Cv2.IsContourConvex(contour) || areaRatio < 0.2 || areaRatio > 5) return null; // a wild warp is a false match
        var c = Cv2.PerspectiveTransform([new Point2d(w / 2, hgt / 2)], hm)[0];
        var box = Cv2.BoundingRect(contour).Intersect(bounds);
        if (box.Width < 2 || box.Height < 2) return null;
        double visible = Math.Min(1, inliers / Math.Max(MinInliers, 0.3 * tf.Pts.Length));
        double score = Math.Clamp(inliers / (double)good.Count, 0, 1) * Math.Min(1, inliers / 20.0);
        return new Detection(box, score, Math.Sqrt(areaRatio), "features", visible, new Point((int)Math.Round(c.X), (int)Math.Round(c.Y)));
    }
}
