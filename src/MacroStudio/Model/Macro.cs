namespace MacroStudio.Model;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

public sealed record DesktopInfo(ScreenRect Virtual, List<ScreenRect> Monitors);

/// <summary>Windows settings that change how identical input behaves: system DPI scale, scroll lines per notch, pointer speed (1-20).</summary>
public sealed record EnvInfo(int Dpi, int WheelLines, int MouseSpeed);

public sealed class Macro
{
    public int Version { get; set; } = 1;
    /// <summary>Identifies this recording in the private Discord settings. Not a secret; the webhook itself is never stored in a recording.</summary>
    public string? Id { get; set; }
    public string Name { get; set; } = "Untitled";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DesktopInfo? Desktop { get; set; }
    public EnvInfo? Env { get; set; }
    /// <summary>Library macros filed under this one (it is their folder). Organisation only: a macro runs another through a When block.</summary>
    public List<string> Subs { get; set; } = new();
    public List<MacroEvent> Events { get; set; } = new();
}
