using System.Windows;
using System.Windows.Interop;
using static MacroStudio.Native.NativeMethods;

namespace MacroStudio.Native;

/// <summary>System-wide hotkeys that work while another app has focus.</summary>
public sealed class HotKeys : IDisposable
{
    readonly nint _hwnd;
    readonly HwndSource? _src;
    readonly Dictionary<int, Action> _actions = new();

    public HotKeys(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _src = _hwnd != 0 ? HwndSource.FromHwnd(_hwnd) : null;
        _src?.AddHook(Hook);
    }

    /// <summary>Returns false when another program already owns the key, or when there is no message hook to hear it with.</summary>
    public bool Register(int id, int mods, int vk, Action action)
    {
        // without the window hook WM_HOTKEY would never arrive: the key would register but stay dead and block other apps
        if (_hwnd == 0 || _src == null) return false;
        if (!RegisterHotKey(_hwnd, id, (uint)mods | 0x4000 /*MOD_NOREPEAT*/, (uint)vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void Unregister(int id) { if (_actions.Remove(id)) UnregisterHotKey(_hwnd, id); }

    /// <summary>All hotkeys off (while the app is waiting for a key to assign, so pressing one does not run it).</summary>
    public void UnregisterAll() { foreach (var id in _actions.Keys.ToList()) Unregister(id); }

    nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var a)) { a(); handled = true; }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();   // a second Dispose must not try to unregister again
        _src?.RemoveHook(Hook);
    }
}
