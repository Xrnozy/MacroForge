using System.IO;
using System.Text.Json;
using MacroStudio.Model;

namespace MacroStudio.Native;

/// <summary>A hotkey: modifier keys held together with one key. Mods uses the Windows bits (Alt 1, Ctrl 2, Shift 4, Win 8), so it goes straight to RegisterHotKey.</summary>
public sealed record Combo(int Mods, int Vk);

/// <summary>The five global hotkeys and the key combination each one is set to. Saved in this user's AppData; a missing or broken file means the defaults.</summary>
public sealed class HotkeySettings
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static readonly (string Action, string Label, int Default)[] Actions =
    [
        ("record", "Record", 0x78), ("play", "Play", 0x79), ("pause", "Pause", 0x7A), ("stop", "Stop", 0x77), ("paths", "Paths", 0x7B),   // F9, F10, F11, F8, F12
    ];

    public Dictionary<string, Combo> Keys { get; set; } = new();

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroStudio", "hotkeys.json");

    public Combo Get(string action) => Keys.TryGetValue(action, out var c) && c != null && Allowed(c) ? c : new Combo(0, Actions.First(a => a.Action == action).Default);
    public string Name(string action) => Display(Get(action));
    public void Set(string action, Combo c) => Keys[action] = c;
    public void Reset() => Keys.Clear();
    public string? Owner(Combo c, string except) => Actions.FirstOrDefault(a => a.Action != except && Get(a.Action) == c).Label;

    /// <summary>Does any hotkey use modifier keys? Then pressing those modifiers is part of using a hotkey, not of "the user using the keyboard".</summary>
    public bool UsesModifiers => Actions.Any(a => Get(a.Action).Mods != 0);

    /// <summary>The keys the app should not count as the user's own input: every hotkey's main key, plus the modifier keys when any combination uses them.</summary>
    public HashSet<int> KeysToIgnore()
    {
        var set = Actions.Select(a => Get(a.Action).Vk).ToHashSet();
        if (UsesModifiers) foreach (var v in ModifierVks) set.Add(v);
        return set;
    }

    public static readonly int[] ModifierVks = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    public static bool IsModifier(int vk) => ModifierVks.Contains(vk);

    /// <summary>The virtual keys of the modifiers named by <paramref name="mods"/> (both left and right keys).</summary>
    public static int[] VksOf(int mods)
    {
        var l = new List<int>();
        if ((mods & Ctrl) != 0) l.AddRange([0x11, 0xA2, 0xA3]);
        if ((mods & Shift) != 0) l.AddRange([0x10, 0xA0, 0xA1]);
        if ((mods & Alt) != 0) l.AddRange([0x12, 0xA4, 0xA5]);
        if ((mods & Win) != 0) l.AddRange([0x5B, 0x5C]);
        return l.ToArray();
    }

    /// <summary>"Ctrl+Shift+Alt+R", "F9".</summary>
    public static string Display(Combo c)
    {
        var parts = new List<string>();
        if ((c.Mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((c.Mods & Shift) != 0) parts.Add("Shift");
        if ((c.Mods & Alt) != 0) parts.Add("Alt");
        if ((c.Mods & Win) != 0) parts.Add("Win");
        parts.Add(c.Vk is >= 0x30 and <= 0x39 ? ((char)c.Vk).ToString() : EventInfo.KeyName(c.Vk));
        return string.Join("+", parts);
    }

    /// <summary>
    /// With any modifier held (Ctrl, Shift, Alt, Win) any key may be a hotkey (Ctrl+Shift+Alt+R, Shift+A). With no modifier only keys other programs do
    /// not need: function keys, the navigation block, Pause / Scroll Lock / Print Screen and the number pad. A bare letter, digit, Space or Enter would be
    /// taken away from every other program.
    /// </summary>
    public static bool Allowed(Combo c) => !IsModifier(c.Vk) && c.Vk > 0 && (c.Mods != 0 || PlainAllowed(c.Vk));

    /// <summary>Shift (alone) plus a letter, digit, Space or Enter: it works, but while the app runs that capital letter can no longer be typed in other programs.</summary>
    public static bool StealsTyping(Combo c) => c.Mods == Shift && !PlainAllowed(c.Vk);

    public static bool PlainAllowed(int vk) =>
        vk is >= 0x70 and <= 0x87                      // F1..F24
        or 0x13 or 0x91 or 0x2C                         // Pause, Scroll Lock, Print Screen
        or >= 0x21 and <= 0x24 or 0x2D or 0x2E          // PageUp, PageDown, End, Home, Insert, Delete
        or >= 0x60 and <= 0x6F;                         // number pad

    public static HotkeySettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(FilePath)) ?? new();
            s.Keys ??= new();
            if (Actions.Select(a => s.Get(a.Action)).Distinct().Count() != Actions.Length) s.Keys.Clear();   // two actions on one combination: start again from the defaults
            return s;
        }
        catch { return new(); }   // also an older file that stored plain numbers
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}
