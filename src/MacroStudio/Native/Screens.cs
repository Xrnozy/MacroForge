using MacroStudio.Model;
using static MacroStudio.Native.NativeMethods;

namespace MacroStudio.Native;

public static class Screens
{
    public static DesktopInfo Current()
    {
        var virt = new ScreenRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var monitors = new List<ScreenRect>();
        EnumDisplayMonitors(0, 0, (nint _, nint _, ref RECT r, nint _) =>
        {
            monitors.Add(new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, 0);
        if (monitors.Count == 0 && virt is { Width: > 0, Height: > 0 })   // no monitor enum (some RDP/headless sessions): fall back to the virtual screen so overlays and RegionPicker still get one surface
            monitors.Add(virt);
        return new DesktopInfo(virt, monitors);
    }

    /// <summary>Effective DPI of each monitor (96 = 100%), same order as <see cref="Current"/>.Monitors. A target dragged to a monitor with another scaling changes size by this ratio.</summary>
    public static IReadOnlyList<int> MonitorDpis()
    {
        var l = new List<int>();
        EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT r, nint _) =>
        {
            l.Add(GetDpiForMonitor(h, 0, out uint x, out _) == 0 ? (int)x : 96);
            return true;
        }, 0);
        if (l.Count == 0) l.Add((int)GetDpiForSystem());   // keep it in step with Current()'s fallback monitor
        return l;
    }

    public static EnvInfo CurrentEnv()
    {
        uint lines = 3, speed = 10;
        SystemParametersInfo(0x68, 0, ref lines, 0);  // SPI_GETWHEELSCROLLLINES
        SystemParametersInfo(0x70, 0, ref speed, 0);  // SPI_GETMOUSESPEED
        return new EnvInfo((int)GetDpiForSystem(), (int)lines, (int)speed);
    }

    public static (int X, int Y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }
}
