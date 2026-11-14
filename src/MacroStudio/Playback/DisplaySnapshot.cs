using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Playback;

/// <summary>The screen setup at one moment: virtual desktop, every monitor's rectangle, and the system DPI.</summary>
public sealed record DisplaySnapshot(ScreenRect Virtual, IReadOnlyList<ScreenRect> Monitors, int Dpi)
{
    public static DisplaySnapshot Current()
    {
        var d = Screens.Current();
        return new DisplaySnapshot(d.Virtual, d.Monitors, Screens.CurrentEnv().Dpi);
    }

    /// <summary>What changed compared with <paramref name="then"/>, or null when it is the same setup (resolution, monitors, layout, scaling).</summary>
    public string? DifferenceFrom(DisplaySnapshot then)
    {
        if (Dpi != then.Dpi) return $"display scaling changed ({then.Dpi} → {Dpi} dpi)";
        if (Monitors.Count != then.Monitors.Count) return $"the number of monitors changed ({then.Monitors.Count} → {Monitors.Count})";
        if (Virtual != then.Virtual || !Monitors.SequenceEqual(then.Monitors))
            return $"the screen layout or resolution changed ({then.Virtual.Width}×{then.Virtual.Height} → {Virtual.Width}×{Virtual.Height})";
        return null;
    }
}
