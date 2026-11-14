using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroStudio.Model;
using MacroStudio.Native;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>GDI BitBlt capture in physical pixels (the app is PerMonitorV2). Exclusive-fullscreen games may come back black.</summary>
public static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint dc, int mode);

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>
    /// Everything a grab needs from GDI, kept per thread between calls: the screen DC, one memory DC and one DIB
    /// that only ever grows to the biggest region seen so far. Creating and destroying all of these per grab cost
    /// a VirtualAlloc of the whole frame plus half a dozen syscalls — a small share of a full-screen grab but most
    /// of the cost of the little verification windows the tracker takes all the time. The DIB is never shrunk: the
    /// big monitor grab that sized it comes back every time the screen changes.
    /// </summary>
    sealed class Cache : IDisposable
    {
        /// <summary>
        /// One grow-only DIB. Small grabs go to a separate slot: a 160×120 blt inside a 1920-wide DIB writes every
        /// row ~7.7 KB apart, and the BGR copy-out reads the same scattered lines — measurably slower than a packed
        /// little buffer. The big slot is never shrunk: the monitor grab that sized it comes back every screen change.
        /// </summary>
        public sealed class Slot
        {
            public nint Bmp, Bits;
            public int W, H;
            public void Free() { if (Bmp != 0) DeleteObject(Bmp); Bmp = Bits = 0; W = H = 0; }
        }

        const int SmallLimit = 500_000;   // pixels: change samples and verify windows stay small; monitor grabs are megapixels
        readonly Slot _big = new(), _small = new();
        public nint Screen, Mem, Stock;
        public int Allocs;
        public double BltMs, CopyMs;   // diagnostic: last grab's blt vs pixel copy-out cost

        /// <summary>The handles die with their thread (thread-static ownership): a dead capturing thread must not leak its DCs and DIB.</summary>
        ~Cache() => Dispose();

        public void Open()
        {
            if (Screen == 0 && (Screen = GetDC(0)) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "No screen DC");
            if (Mem == 0) Mem = CreateCompatibleDC(Screen);
        }

        /// <summary>The slot for a w×h blt, grown when the request outgrows it.</summary>
        public Slot Ensure(int w, int h)
        {
            var s = (long)w * h < SmallLimit ? _small : _big;
            if (s.Bmp == 0 || w > s.W || h > s.H)
            {
                int nw = Math.Max(w, s.W), nh = Math.Max(h, s.H);
                var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = nw, biHeight = -nh, biPlanes = 1, biBitCount = 32 };
                nint bmp = CreateDIBSection(Screen, ref bi, 0, out nint bits, 0, 0);
                if (bmp == 0 || bits == 0) { if (bmp != 0) DeleteObject(bmp); throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate capture buffer"); }
                nint prev = SelectObject(Mem, bmp);                   // whatever was last selected comes out of the DC
                if (Stock == 0) Stock = prev;                         // the DC's own stock bitmap, returned the first select ever
                if (s.Bmp != 0) DeleteObject(s.Bmp);                  // this slot's old DIB — the swap left it unselected
                s.Bmp = bmp; s.Bits = bits; s.W = nw; s.H = nh; Allocs++;
            }
            else SelectObject(Mem, s.Bmp);   // the DC holds only the last-grown bitmap: select this slot's DIB back in
            return s;
        }

        public void Dispose()
        {
            if (Mem != 0) { if (Stock != 0) SelectObject(Mem, Stock); _big.Free(); _small.Free(); DeleteDC(Mem); }
            if (Screen != 0) ReleaseDC(0, Screen);
            Screen = Mem = Stock = 0;
            GC.SuppressFinalize(this);
        }
    }

    [ThreadStatic] static Cache? _cache;
    static Cache Buf => _cache ??= new Cache();

    /// <summary>Test hook: DIBs this thread has allocated so far (a reused buffer does not count again).</summary>
    internal static long ThreadDibAllocs => _cache?.Allocs ?? 0;

    /// <summary>Test hook: the last grab's BitBlt time and DIB-to-mat copy time on this thread.</summary>
    internal static (double Blt, double Copy) LastTimes => _cache is { } c ? (c.BltMs, c.CopyMs) : (0, 0);

    /// <summary>Release this thread's cached DCs and DIB (also used to recover from a stale handle).</summary>
    internal static void ReleaseThreadCache() { _cache?.Dispose(); _cache = null; }

    /// <summary>
    /// Copies the top-left w×h pixels of the 32bpp BGRA DIB into a BGR mat the caller owns. The DIB may be
    /// wider than the blt it just received (<paramref name="strideW"/> is the buffer's real width). The output comes
    /// from <see cref="FrameBuffers"/>: an owning <see cref="FrameContext"/> hands it back on Dispose, so the capture
    /// buffer itself also cycles between ticks (a caller that just disposes it is fine too — the pool simply loses one).
    /// </summary>
    internal static Mat BgrFromDib(nint bits, int strideW, int w, int h)
    {
        using var all = Mat.FromPixelData(h, strideW, MatType.CV_8UC4, bits);
        var bgr = FrameBuffers.Rent(h, w, MatType.CV_8UC3);
        if (w == strideW) Cv2.CvtColor(all, bgr, ColorConversionCodes.BGRA2BGR);
        else
        {
            using var view = new Mat(all, new Rect(0, 0, w, h));
            Cv2.CvtColor(view, bgr, ColorConversionCodes.BGRA2BGR); // copies, so the DIB can be reused
        }
        return bgr;
    }

    /// <summary>
    /// A 1/divisor nearest-neighbour sample of a screen rectangle, scaled by GDI while copying (about a third of the CPU of a full
    /// capture). Only good for "did the screen change?" checks, not for matching.
    /// </summary>
    public static Mat CaptureSample(ScreenRect region, int divisor = 8)
    {
        int w = Math.Max(1, region.Width / divisor), h = Math.Max(1, region.Height / divisor);
        for (int i = 0; ; i++)
            try
            {
                var c = Buf; c.Open(); var s = c.Ensure(w, h);
                SetStretchBltMode(c.Mem, 3 /*COLORONCOLOR: drop pixels, no averaging*/);
                if (!StretchBlt(c.Mem, 0, 0, w, h, c.Screen, region.X, region.Y, region.Width, region.Height, SRCCOPY))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Screen sample failed");
                return BgrFromDib(s.Bits, s.W, w, h);
            }
            catch when (i == 0) { ReleaseThreadCache(); }   // a stale handle after a display change: rebuild once, then report
    }

    /// <summary>BGR frame of the given screen rectangle (default: the whole virtual desktop). Caller disposes.</summary>
    public static Mat Capture(ScreenRect? region = null)
    {
        var r = region ?? Screens.Current().Virtual;
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("Empty capture region");
        for (int i = 0; ; i++)
            try
            {
                var c = Buf; c.Open(); var s = c.Ensure(r.Width, r.Height);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!BitBlt(c.Mem, 0, 0, r.Width, r.Height, c.Screen, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Screen capture failed");
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                var m = BgrFromDib(s.Bits, s.W, r.Width, r.Height);
                c.BltMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                c.CopyMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return m;
            }
            catch when (i == 0) { ReleaseThreadCache(); }
    }
}
