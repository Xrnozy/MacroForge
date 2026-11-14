using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MacroStudio.Views;

/// <summary>Tiny modal text prompt (WPF has no built-in InputBox).</summary>
public static class Prompt
{
    /// <summary>Pick one item from a list. Returns its key, or null when cancelled.</summary>
    public static string? Choose(Window owner, string title, string message, IReadOnlyList<(string Key, string Text)> items)
    {
        var box = new ComboBox { ItemsSource = items.Select(i => i.Text).ToList(), SelectedIndex = items.Count > 0 ? 0 : -1, MinWidth = 280, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var win = new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(16), MaxWidth = 460, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, box, buttons } },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        return win.ShowDialog() == true && box.SelectedIndex >= 0 ? items[box.SelectedIndex].Key : null;
    }

    /// <summary>Waits for a key press together with any modifier keys held (Ctrl+Shift+Alt+R) and returns them; Esc or closing the window returns null.</summary>
    public static (int Mods, int Vk)? PressCombo(Window owner, string message)
    {
        (int, int)? result = null;
        var preview = new TextBlock { Text = "…", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Right };
        var win = new Window
        {
            Title = "Hotkey", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(18), MinWidth = 300, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 360 }, preview, cancel } },
        };
        static int ModsNow() => (int)Keyboard.Modifiers;   // Alt 1, Control 2, Shift 4, Windows 8: the same bits Windows uses for hotkeys
        win.PreviewKeyUp += (_, _) => preview.Text = ModsNow() == 0 ? "…" : Native.HotkeySettings.Display(new Native.Combo(ModsNow(), 0x7E)).Replace("+F15", "+…");
        win.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            { preview.Text = Native.HotkeySettings.Display(new Native.Combo(ModsNow(), 0x7E)).Replace("+F15", "+…"); return; }   // waiting for the main key
            if (key == Key.Escape) { win.DialogResult = false; return; }
            int vk = KeyInterop.VirtualKeyFromKey(key);
            result = (ModsNow(), vk);
            win.DialogResult = true;
        };
        return win.ShowDialog() == true ? result : null;
    }

    public static string? Ask(Window owner, string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12), MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 3, 18, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var win = new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(16), Children = { box, buttons } },
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
