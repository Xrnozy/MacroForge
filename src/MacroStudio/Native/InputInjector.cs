using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroStudio.Model;
using MacroStudio.Playback;
using static MacroStudio.Native.NativeMethods;

namespace MacroStudio.Native;

public static class Coords
{
    /// <summary>
    /// Pixel -> SendInput absolute (0..65535) over the virtual desktop. Windows maps back with (size-1)/65535, so we
    /// take the ceiling: the result is exact whether Windows truncates or rounds (verified on a live 3840-wide desktop).
    /// </summary>
    public static int ToAbsolute(int coord, int origin, int size) =>
        size < 2 ? 0 : (int)(((long)(coord - origin) * 65535 + (size - 2)) / (size - 1));

    public static int FromAbsolute(int abs, int origin, int size) => (int)((long)abs * (size - 1) / 65535) + origin;
}

public sealed class InputInjector : IInputSink
{
    static readonly int INPUT_SIZE = Marshal.SizeOf<INPUT>();
    readonly ScreenRect _vd;

    public InputInjector(ScreenRect? virtualDesktop = null) => _vd = virtualDesktop ?? Screens.Current().Virtual;

    public void Send(in MacroEvent e)
    {
        var input = new INPUT[1];
        if (e.IsKey())
        {
            uint flags = e.Kind == EventKind.KeyUp ? KEYEVENTF_KEYUP : 0;
            if (e.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
            if (e.Scan != 0) // scancode path = what hardware sends; games and layouts rely on it
                input[0] = new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wScan = (ushort)e.Scan, dwFlags = flags | KEYEVENTF_SCANCODE } } };
            else
                input[0] = new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = (ushort)e.Vk, dwFlags = flags } } };
        }
        else
        {
            if (e.Kind == EventKind.RelMove)   // relative movement: what a game reads while it hides and locks the cursor
            {
                input[0] = new INPUT { type = INPUT_MOUSE, u = new INPUT_UNION { mi = new MOUSEINPUT { dx = e.X, dy = e.Y, dwFlags = MOUSEEVENTF_MOVE } } };
                if (SendInput(1, input, INPUT_SIZE) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput was rejected (target window may be elevated).");
                return;
            }
            uint flags = MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK, data = 0;
            switch (e.Kind)
            {
                case EventKind.ButtonDown or EventKind.ButtonUp:
                    bool down = e.Kind == EventKind.ButtonDown;
                    switch (e.Button)
                    {
                        case MouseBtn.Left: flags |= down ? LEFTDOWN : LEFTUP; break;
                        case MouseBtn.Right: flags |= down ? RIGHTDOWN : RIGHTUP; break;
                        case MouseBtn.Middle: flags |= down ? MIDDLEDOWN : MIDDLEUP; break;
                        default: flags |= down ? XDOWN : XUP; data = e.Button == MouseBtn.X2 ? 2u : 1u; break;
                    }
                    break;
                case EventKind.Wheel: flags |= WHEEL; data = unchecked((uint)e.Delta); break;
                case EventKind.HWheel: flags |= HWHEEL; data = unchecked((uint)e.Delta); break;
            }
            input[0] = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUT_UNION { mi = new MOUSEINPUT { dx = Coords.ToAbsolute(e.X, _vd.X, _vd.Width), dy = Coords.ToAbsolute(e.Y, _vd.Y, _vd.Height), mouseData = data, dwFlags = flags } },
            };
        }
        if (SendInput(1, input, INPUT_SIZE) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput was rejected (target window may be elevated).");
    }
}
