using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MacroStudio.Model;

namespace MacroStudio.Views;

/// <summary>Win32 placement in physical pixels, so full-screen overlays are exact on mixed-DPI multi-monitor setups.</summary>
internal static class WindowPlacement
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public PT Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>
    /// A borderless window maximises over the whole monitor, taskbar included. Tell Windows the maximised size is the monitor's work area
    /// (the screen minus the taskbar), on whichever monitor the window is on.
    /// </summary>
    public static void MaximizeToWorkArea(Window w)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle())?.AddHook((nint h, int msg, nint wp, nint lp, ref bool handled) =>
        {
            if (msg != 0x24 /* WM_GETMINMAXINFO */) return 0;
            var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (mon != 0 && GetMonitorInfo(mon, ref mi))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lp);
                mmi.MaxPosition = new PT { X = mi.Work.L - mi.Monitor.L, Y = mi.Work.T - mi.Monitor.T };
                mmi.MaxSize = new PT { X = mi.Work.R - mi.Work.L, Y = mi.Work.B - mi.Work.T };
                Marshal.StructureToPtr(mmi, lp, true);
            }
            return 0;   // not handled: WPF still applies its own minimum / maximum sizes
        });
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11: let the system round this window's corners (and shadow it). Done by the window manager, so there is no layered-window cost while the window is dragged. Older Windows ignores it.</summary>
    public static void RoundCorners(Window w)
    {
        try { int round = 2; /* DWMWCP_ROUND */ DwmSetWindowAttribute(new WindowInteropHelper(w).EnsureHandle(), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)); }
        catch { }
    }

    public static void Place(Window w, ScreenRect r)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowPos(h, -1 /*HWND_TOPMOST*/, r.X, r.Y, r.Width, r.Height, 0x10 /*SWP_NOACTIVATE*/);
    }

    /// <summary>Make the window ignore the mouse and never take focus.</summary>
    public static void ClickThrough(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80000 | 0x08000000 | 0x80); // TRANSPARENT | LAYERED | NOACTIVATE | TOOLWINDOW
    }

    /// <summary>Keep the window out of screen captures, so overlay boxes never alter what the detector sees (Windows 10 2004+).</summary>
    public static void ExcludeFromCapture(Window w, bool excluded = true) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(w).EnsureHandle(), excluded ? 0x11u /*WDA_EXCLUDEFROMCAPTURE*/ : 0u);

    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <summary>The window's rectangle in physical screen pixels.</summary>
    public static (int L, int T, int R, int B) Bounds(nint h) { GetWindowRect(h, out var r); return (r.L, r.T, r.R, r.B); }

    /// <summary>
    /// While a macro plays with this window on screen: the window never takes focus (replayed keys always go to the target app) and
    /// stays out of screen captures (it can never be mistaken for the target). Undone with <c>on = false</c>.
    /// </summary>
    public static void PlaybackMode(nint h, bool on)
    {
        int ex = GetWindowLong(h, -20);
        SetWindowLong(h, -20, on ? ex | 0x08000000 : ex & ~0x08000000);   // WS_EX_NOACTIVATE
        SetWindowDisplayAffinity(h, on ? 0x11u : 0u);
    }

    /// <summary>Clicking the window does not take focus from the app being recorded.</summary>
    public static void NoActivate(nint h, bool on)
    {
        int ex = GetWindowLong(h, -20);
        SetWindowLong(h, -20, on ? ex | 0x08000000 : ex & ~0x08000000);
    }

    /// <summary>Let replayed clicks fall through this window to whatever is underneath, so it can never swallow a macro's click.</summary>
    public static void PassThrough(nint h, bool on)
    {
        int ex = GetWindowLong(h, -20);
        if (on) { SetWindowLong(h, -20, ex | 0x80000 | 0x20); SetLayeredWindowAttributes(h, 0, 255, 2 /*LWA_ALPHA*/); }
        else SetWindowLong(h, -20, ex & ~0x20);                            // stays layered at full opacity: looks the same
    }

    /// <summary>Move and resize a window (physical pixels) without activating it or changing its stacking.</summary>
    public static void MoveResize(nint h, ScreenRect r) => SetWindowPos(h, 0, r.X, r.Y, r.Width, r.Height, 0x10 | 0x04);   // NOACTIVATE | NOZORDER

    /// <summary>Place at the given physical rectangle now and again whenever WPF re-scales the window for DPI.</summary>
    public static void Pin(Window w, ScreenRect r)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SourceInitialized += (_, _) => Place(w, r);
        w.Loaded += (_, _) => Place(w, r);
        w.DpiChanged += (_, _) => Place(w, r);
    }
}
