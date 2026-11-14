using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>
/// D7: screen-capture cost and per-frame caching — the reused GDI buffer in <see cref="ScreenCapture"/>
/// and the pooled gray/half/edge buffers in <see cref="FrameContext"/>.
/// </summary>
public sealed class D7CaptureTests
{
    readonly ITestOutputHelper _o;
    public D7CaptureTests(ITestOutputHelper o) => _o = o;

    static Mat Bgr(int w, int h, Scalar fill) => new(h, w, MatType.CV_8UC3, fill);

    // ---------- DIB -> BGR copy: the reusable DIB may be wider than the blt it just received ----------

    [Fact]
    public void Bgr_copy_from_a_wider_dib_reads_the_right_pixels()
    {
        using var dib = new Mat(12, 16, MatType.CV_8UC4);
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 16; x++)
                dib.Set(y, x, new Vec4b((byte)(x * 3), (byte)(y * 5), (byte)(x + 2 * y), 255));
        using var bgr = ScreenCapture.BgrFromDib(dib.Data, 16, 10, 7);
        Assert.Equal((10, 7), (bgr.Width, bgr.Height));
        Assert.Equal(3, bgr.Channels());
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 10; x++)
            {
                var s = dib.At<Vec4b>(y, x);
                var d = bgr.At<Vec3b>(y, x);
                Assert.Equal((s.Item0, s.Item1, s.Item2), (d.Item0, d.Item1, d.Item2));
            }
    }

    [Fact]
    public void Bgr_copy_when_the_blit_fills_the_whole_dib()
    {
        using var dib = new Mat(6, 8, MatType.CV_8UC4);
        Cv2.Randu(dib, Scalar.All(0), Scalar.All(255));
        using var bgr = ScreenCapture.BgrFromDib(dib.Data, 8, 8, 6);
        Assert.Equal((8, 6), (bgr.Width, bgr.Height));
        Assert.Equal(3, bgr.Channels());
        using var back = new Mat();
        Cv2.CvtColor(dib, back, ColorConversionCodes.BGRA2BGR);
        Assert.Equal(0, Cv2.Norm(bgr, back, NormTypes.L1));
    }

    // ---------- real GDI: the per-thread DC/DIB is reused across grabs (skips where there is no desktop) ----------

    static bool HasDesktop()
    {
        try { using var m = ScreenCapture.Capture(new ScreenRect(0, 0, 32, 24)); return !m.Empty(); }
        catch { return false; }
    }

    [Fact]
    public void Grabs_of_different_sizes_reuse_one_dib_and_each_returns_an_independent_copy()
    {
        if (!HasDesktop()) { _o.WriteLine("skipped: no usable desktop in this session"); return; }
        long allocs = ScreenCapture.ThreadDibAllocs;
        using var small = ScreenCapture.Capture(new ScreenRect(0, 0, 64, 48));
        using var keep = small.Clone();
        using var big = ScreenCapture.Capture(new ScreenRect(0, 0, 200, 150));   // grows the DIB once
        using var again = ScreenCapture.Capture(new ScreenRect(0, 0, 64, 48));   // fits: no new DIB
        Assert.True(ScreenCapture.ThreadDibAllocs <= allocs + 2, $"{ScreenCapture.ThreadDibAllocs - allocs} DIB allocs for 4 grabs"); // at most: 64x48, then grow to 200x150; the rest reuse
        Assert.Equal((200, 150), (big.Width, big.Height));
        Assert.Equal((64, 48), (again.Width, again.Height));
        Assert.Equal(0, Cv2.Norm(small, keep, NormTypes.L1));                    // a returned mat is a copy, never a view into the reused DIB
    }

    [Fact]
    public void A_sample_uses_the_same_cached_dib_and_keeps_its_scaled_shape()
    {
        if (!HasDesktop()) { _o.WriteLine("skipped: no usable desktop in this session"); return; }
        using var full = ScreenCapture.Capture(new ScreenRect(0, 0, 640, 480));
        long allocs = ScreenCapture.ThreadDibAllocs;
        using var s = ScreenCapture.CaptureSample(new ScreenRect(0, 0, 640, 480), 8);
        Assert.Equal((80, 60), (s.Width, s.Height));
        Assert.Equal(3, s.Channels());
        Assert.Equal(allocs, ScreenCapture.ThreadDibAllocs);                     // the sample fits the DIB the big grab allocated
    }

    // The pre-change implementation (fresh DC + DIB per grab), kept here for an honest before/after on the same machine.
    static class OldStyle
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct BI { public uint Size; public int W, H; public ushort Planes, Bpp; public uint Comp, SizeImg; public int Xpm, Ypm; public uint Used, Important; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BI bi, uint usage, out nint bits, nint section, uint offset);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);

        public static Mat Capture(ScreenRect r)
        {
            nint screen = GetDC(0);
            nint mem = CreateCompatibleDC(screen);
            var bi = new BI { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BI>(), W = r.Width, H = -r.Height, Planes = 1, Bpp = 32 };
            nint bmp = CreateDIBSection(screen, ref bi, 0, out nint bits, 0, 0);
            try
            {
                nint old = SelectObject(mem, bmp);
                BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000);
                SelectObject(mem, old);
                using var bgra = Mat.FromPixelData(r.Height, r.Width, MatType.CV_8UC4, bits);
                var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                return bgr;
            }
            finally { if (bmp != 0) DeleteObject(bmp); DeleteDC(mem); ReleaseDC(0, screen); }
        }
    }

    [Fact]
    public void Capture_cost_is_reported_for_before_after_comparison()
    {
        if (!HasDesktop()) { _o.WriteLine("skipped: no usable desktop in this session"); return; }
        var mon = MacroStudio.Native.Screens.Current().Monitors[0];
        using (var warm = ScreenCapture.Capture(mon)) { }                        // fill the thread cache outside the measurement
        using (var warm2 = OldStyle.Capture(mon)) { }
        // alternating old/new samples: machine-load drift hits both sides about equally, so the medians stay comparable
        var nw = new List<double>(); var old = new List<double>(); var rnw = new List<double>(); var rold = new List<double>();
        var rblt = new List<double>(); var rcopy = new List<double>();
        var region = new ScreenRect(mon.X, mon.Y, 160, 120);
        for (int i = 0; i < 14; i++)
        {
            var sw = Stopwatch.StartNew();
            // alternate which side goes first inside each pair: order effects would otherwise look like regressions
            bool newFirst = (i & 1) == 0;
            using (var m = newFirst ? ScreenCapture.Capture(mon) : OldStyle.Capture(mon)) { }
            (newFirst ? nw : old).Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            using (var m = newFirst ? OldStyle.Capture(mon) : ScreenCapture.Capture(mon)) { }
            (newFirst ? old : nw).Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            using (var m = newFirst ? ScreenCapture.Capture(region) : OldStyle.Capture(region)) { }
            (newFirst ? rnw : rold).Add(sw.Elapsed.TotalMilliseconds);
            if (newFirst) { rblt.Add(ScreenCapture.LastTimes.Blt); rcopy.Add(ScreenCapture.LastTimes.Copy); }
            sw.Restart();
            using (var m = newFirst ? OldStyle.Capture(region) : ScreenCapture.Capture(region)) { }
            (newFirst ? rold : rnw).Add(sw.Elapsed.TotalMilliseconds);
            if (!newFirst) { rblt.Add(ScreenCapture.LastTimes.Blt); rcopy.Add(ScreenCapture.LastTimes.Copy); }
        }
        double Med(List<double> l) { l.Sort(); return l[l.Count / 2]; }
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) using (var m = ScreenCapture.CaptureSample(mon, 8)) { }
        _o.WriteLine($"GDI {mon.Width}x{mon.Height}: full {Med(nw):F1} ms (old-style {Med(old):F1}), 160x120 region {Med(rnw):F2} ms (old-style {Med(rold):F2}) [blt {Med(rblt):F2} + copy {Med(rcopy):F2}], 1/8 sample {sw2.Elapsed.TotalMilliseconds / 10:F2} ms");
    }

    // ---------- FrameContext: derived buffers are pooled across contexts, never aliased while alive ----------

    [Fact]
    public void Gray_and_half_buffers_are_reused_by_the_next_context_with_fresh_content()
    {
        using var a = Bgr(64, 48, new Scalar(10, 200, 30));
        using var b = Bgr(64, 48, new Scalar(200, 20, 90));
        Mat g1, h1;
        using (var c1 = new FrameContext(a)) { g1 = c1.Gray; h1 = c1.Half; }
        using (var c2 = new FrameContext(b))
        {
            Assert.Same(g1, c2.Gray);                                            // handed out again from the pool
            Assert.Same(h1, c2.Half);
            using var want = new Mat();
            Cv2.CvtColor(b, want, ColorConversionCodes.BGR2GRAY);
            Assert.Equal(0, Cv2.Norm(c2.Gray, want, NormTypes.L1));              // rewritten, not stale
        }
    }

    [Fact]
    public void The_edge_buffer_is_disposed_with_its_context()
    {
        using var f = Bgr(80, 60, new Scalar(50, 60, 70));
        Mat e1;
        using (var c1 = new FrameContext(f)) e1 = c1.Edge;
        Assert.True(e1.IsDisposed);                                            // not pooled (nobody rents that shape): freed, not stashed
        using (var c2 = new FrameContext(f)) Assert.NotSame(e1, c2.Edge);
    }

    [Fact]
    public void An_owned_frame_goes_back_to_the_pool_but_a_borrowed_one_is_untouched()
    {
        var shot = Bgr(96, 72, new Scalar(7, 8, 9));
        using (var c = new FrameContext(shot, owns: true)) { }
        Assert.Same(shot, FrameBuffers.Rent(72, 96, MatType.CV_8UC3));   // the owned frame's buffer came back for the next grab
        using var kept = Bgr(96, 72, new Scalar(1, 1, 1));
        using (var c = new FrameContext(kept)) { }
        Assert.False(kept.IsDisposed);                                 // owns:false: the caller's mat is left alone
    }

    [Fact]
    public void Live_contexts_never_share_a_buffer()
    {
        using var f = Bgr(64, 48, new Scalar(1, 2, 3));
        using var c1 = new FrameContext(f);
        using var c2 = new FrameContext(f);
        Assert.NotSame(c1.Gray, c2.Gray);
        Assert.NotSame(c1.Half, c2.Half);
    }

    [Fact]
    public void Access_after_dispose_throws_and_double_dispose_returns_nothing_twice()
    {
        using var f = Bgr(64, 48, new Scalar(1, 2, 3));
        var c = new FrameContext(f);
        _ = c.Gray;
        c.Dispose();
        c.Dispose();                                                           // a double dispose must not return the buffer twice
        Assert.Throws<ObjectDisposedException>(() => { _ = c.Gray; });
        Assert.Throws<ObjectDisposedException>(() => { _ = c.Half; });
    }

    [Fact]
    public void The_pool_stays_bounded_under_size_churn()
    {
        int before = FrameBuffers.PooledCount;
        for (int w = 20; w < 60; w += 4)
        {
            using var f = Bgr(w, 30, new Scalar(1, 2, 3));
            using (var c = new FrameContext(f)) _ = c.Gray;
        }
        // at most one retained buffer per distinct shape, whatever else the process has pooled
        Assert.True(FrameBuffers.PooledCount <= before + 10, $"pool held {FrameBuffers.PooledCount} mats (was {before})");
    }

    // ---------- end-to-end: detection on pooled frames still lands on the target ----------

    [Fact]
    public void Repeated_per_frame_searches_stay_correct()
    {
        using var desk = Desktop.Make();
        using var frame = new Mat(desk, new Rect(0, 0, 1920, 1080)).Clone();
        using var tpl = RealButtons.Decode(RealButtons.Retry);
        using (var d = new Mat(frame, new Rect(700, 500, tpl.Width, tpl.Height))) tpl.CopyTo(d);
        var det = new Detector();
        var first = det.Find(frame, tpl, 0.85);
        Assert.NotEmpty(first);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) det.Find(frame, tpl, 0.85);
        long per = (GC.GetAllocatedBytesForCurrentThread() - a0) / 10;
        _o.WriteLine($"10 full-frame finds: {sw.Elapsed.TotalMilliseconds:F0} ms total, {per} managed B/find");
        Assert.Equal(first[0].Rect, det.Find(frame, tpl, 0.85)[0].Rect);
    }
}
