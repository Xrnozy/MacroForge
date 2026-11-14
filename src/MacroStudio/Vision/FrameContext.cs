using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// Process-wide cache for the big scratch buffers a <see cref="FrameContext"/> derives from its frame. A 1080p frame
/// needs ~10 MB of gray / half-size / edge mats and the watcher builds a context per tick, so without reuse those
/// megabytes were allocated and freed every tick. Bounded: at most a few frames' worth of each shape is kept.
/// Mats a context gives back are only handed out again under the same contract they already lived under: nothing may
/// touch a context's derived mats after the context is disposed.
/// </summary>
internal static class FrameBuffers
{
    internal const int MaxPerShape = 8;                      // a few same-size contexts may be alive at once
    const long MaxBytes = 384L << 20;                        // hard cap so odd sizes cannot pile up
    static readonly Dictionary<(int Rows, int Cols, MatType Type), Stack<Mat>> Free = new();
    static long _bytes;

    internal static int PooledCount { get { lock (Free) { int n = 0; foreach (var s in Free.Values) n += s.Count; return n; } } }

    public static Mat Rent(int rows, int cols, MatType type)
    {
        var key = (rows, cols, type);
        lock (Free)
            if (Free.TryGetValue(key, out var s))
                while (s.Count > 0)
                {
                    var m = s.Pop();
                    if (m.IsDisposed) continue;
                    _bytes -= Bytes(m);
                    return m;
                }
        return new Mat(rows, cols, type);
    }

    public static void Return(Mat? m)
    {
        if (m == null || m.IsDisposed) return;
        if (m.Empty() || m.IsSubmatrix() || !m.IsContinuous()) { m.Dispose(); return; }  // a view would alias its parent when handed out again
        var key = (m.Rows, m.Cols, m.Type());
        long sz = Bytes(m);
        lock (Free)
        {
            if (!Free.TryGetValue(key, out var s)) Free[key] = s = new Stack<Mat>();
            if (s.Count >= MaxPerShape || _bytes + sz > MaxBytes) { m.Dispose(); return; }
            s.Push(m);
            _bytes += sz;
        }
    }

    static long Bytes(Mat m) => (long)(m.Total() * m.ElemSize());
}

/// <summary>
/// One captured image plus everything derived from it (gray, half-size gray, keypoints), computed once on demand and shared by
/// every target searched in that frame. <see cref="Origin"/> is where this image sits on the desktop (region captures).
/// An OWNED Bgr goes back to <see cref="FrameBuffers"/> on Dispose rather than being freed (it is almost always a capture
/// buffer, so the next grab reuses it); handing the same mat to two owning contexts was always a violation and now also risks
/// aliasing, so each owned frame must still reach exactly one context.
/// </summary>
public sealed class FrameContext : IDisposable
{
    readonly bool _owns;
    Mat? _gray, _half, _edge;
    int _disposed;

    public Mat Bgr { get; }
    public Point Origin { get; }
    public int Width => Bgr.Width;
    public int Height => Bgr.Height;

    // AKAZE result for one area, shared by all targets that need it in this frame
    internal Rect FeatureArea;
    internal KeyPoint[]? FeatureKp;
    internal Mat? FeatureDesc;

    public FrameContext(Mat bgr, Point origin = default, bool owns = false) { Bgr = bgr; Origin = origin; _owns = owns; }

    void Alive() { if (_disposed != 0) throw new ObjectDisposedException(nameof(FrameContext)); }

    /// <summary>Gray copy of the frame, made on first use. Its buffer comes from <see cref="FrameBuffers"/> and returns there on Dispose.</summary>
    public Mat Gray
    {
        get
        {
            Alive();
            if (_gray == null) { _gray = FrameBuffers.Rent(Height, Width, MatType.CV_8UC1); Cv2.CvtColor(Bgr, _gray, ColorConversionCodes.BGR2GRAY); }
            return _gray;
        }
    }

    /// <summary>Gray at half size: the coarse search runs here, at a quarter of the pixels.</summary>
    public Mat Half
    {
        get
        {
            Alive();
            if (_half == null) { _half = FrameBuffers.Rent(Math.Max(1, Height / 2), Math.Max(1, Width / 2), MatType.CV_8UC1); Cv2.Resize(Gray, _half, new Size(_half.Width, _half.Height), 0, 0, InterpolationFlags.Area); }
            return _half;
        }
    }

    /// <summary>Edge strength of the frame (see <see cref="ShapeMatching"/>), made on first use and shared by every target.</summary>
    internal Mat Edge { get { Alive(); return _edge ??= ShapeMatching.EdgeOf(Gray); } }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_owns) FrameBuffers.Return(Bgr);   // owned frames are almost always capture buffers: recycle, don't free
        FrameBuffers.Return(_gray); FrameBuffers.Return(_half); FrameBuffers.Return(FeatureDesc);
        _edge?.Dispose();   // EdgeOf allocates inside ShapeMatching: this shape is never rented, so pooling it would just sit there
        _gray = _half = _edge = null; FeatureDesc = null;
    }
}

/// <summary>A template at one scale with its gray and half-size gray ready, so nothing is resized or converted per frame.</summary>
internal sealed class Scaled
{
    public Mat Bgr { get; }
    public Mat Gray { get; }
    public Mat? Half { get; }                 // null when the template is too small to survive halving
    public int W => Bgr.Width;
    public int H => Bgr.Height;
    Scalar? _mean;
    /// <summary>Average colour of the template (BGR), for the colour check.</summary>
    public Scalar MeanBgr => _mean ??= Cv2.Mean(Bgr);
    public object? PatchData;                 // PatchVoting's cached patch layout
    public Mat? Edge;                         // ShapeMatching's cached edge image of the template

    public Scaled(Mat src, double scale)
    {
        if (Math.Abs(scale - 1) < 0.005) Bgr = src;
        else
        {
            Bgr = new Mat();
            Cv2.Resize(src, Bgr, new Size(Math.Max(1, (int)Math.Round(src.Width * scale)), Math.Max(1, (int)Math.Round(src.Height * scale))), 0, 0,
                scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        }
        Gray = new Mat(); Cv2.CvtColor(Bgr, Gray, ColorConversionCodes.BGR2GRAY);
        if (Math.Min(W, H) >= 16)
        {
            Half = new Mat();
            Cv2.Resize(Gray, Half, new Size(Math.Max(1, W / 2), Math.Max(1, H / 2)), 0, 0, InterpolationFlags.Area);
        }

    }
}

/// <summary>Per-template cache of <see cref="Scaled"/> versions, keyed by the template Mat so a registered target is prepared once.</summary>
internal sealed class Prepared
{
    static readonly ConditionalWeakTable<Mat, Prepared> Cache = new();
    readonly Mat _src;
    readonly Dictionary<int, Scaled> _scales = new();

    Prepared(Mat src) => _src = src;

    /// <summary>Allowed colour drift for this template (set from the target's settings before searching).</summary>
    public double ColorTolerance { get; set; } = ColorMath.DefaultTolerance;

    public static Prepared Of(Mat tpl) => Cache.GetValue(tpl, t => new Prepared(t));

    public Scaled At(double scale)
    {
        int key = (int)Math.Round(scale * 100);
        lock (_scales)
        {
            if (!_scales.TryGetValue(key, out var s))
            {
                if (_scales.Count > 24) _scales.Clear(); // sweeps use a handful; bound the memory anyway
                _scales[key] = s = new Scaled(_src, key / 100.0);
            }
            return s;
        }
    }
}
