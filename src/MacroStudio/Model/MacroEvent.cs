using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace MacroStudio.Model;

/// <summary>
/// TargetClick: wait for a registered visual target, then click it. WaitTarget / WaitGone: wait until it is visible / has disappeared.
/// Fields used by these steps: Target (id), X/Y = pixel offset from the click anchor, Button, Delta = timeout ms (0 = 10 s),
/// Discord: post the text kept in Target to the Discord webhook (settings, never stored in the macro); Extended = attach a screenshot.
/// Extended = skip instead of stopping when it times out, Step = adaptive options (relative anchor, confidence, retries, fallback, verification).
/// </summary>
public enum EventKind { KeyDown, KeyUp, MouseMove, ButtonDown, ButtonUp, Wheel, HWheel, TargetClick, WaitTarget, WaitGone, Discord, RelMove, When }
public enum MouseBtn { Left, Right, Middle, X1, X2 }

/// <summary>One recorded input. T is absolute ms since recording start; delays are derived (T - previous T).</summary>
/// <remarks>RelMove: X/Y are a relative mouse movement in device counts (camera look while a game hides the cursor), not a position. Wheel Delta is raw wheel units (120 = one notch). Delta &gt; 0 = up / right.</remarks>
public readonly record struct MacroEvent(
    double T,
    EventKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Vk = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Scan = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Extended = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int X = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Y = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] MouseBtn Button = MouseBtn.Left,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Delta = 0,
    [property: JsonIgnore] int Id = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StepOptions? Step = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowOptions? Flow = null);

public static class EventInfo
{
    // The predicates run per event in every scan over the array (analysis, path index, grid) — keep them inlineable.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKey(this MacroEvent e) => e.Kind is EventKind.KeyDown or EventKind.KeyUp;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsButton(this MacroEvent e) => e.Kind is EventKind.ButtonDown or EventKind.ButtonUp;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWheel(this MacroEvent e) => e.Kind is EventKind.Wheel or EventKind.HWheel;
    // HasPos is enumerated positively (the five positional kinds): a new EventKind must be added here if it carries a position.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasPos(this MacroEvent e) => e.Kind is EventKind.MouseMove or EventKind.ButtonDown or EventKind.ButtonUp or EventKind.Wheel or EventKind.HWheel;
    /// <summary>A step drawn as a marker on the timeline: a target step or a Discord message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMarker(this MacroEvent e) => e.IsTarget() || e.Kind is EventKind.Discord or EventKind.When;
    /// <summary>A When block: a condition with an optional connected macro.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhen(this MacroEvent e) => e.Kind == EventKind.When;
    /// <summary>Any step that refers to a registered visual target.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTarget(this MacroEvent e) => e.Kind is EventKind.TargetClick or EventKind.WaitTarget or EventKind.WaitGone;

    /// <summary>Resolves a target id to its display name (set by the app; falls back to the id).</summary>
    public static Func<string, string?>? TargetNameOf { get; set; }
    public static string TargetLabel(string? id) => id == null ? "(none)" : TargetNameOf?.Invoke(id) ?? $"{id} (missing)";
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDown(this MacroEvent e) => e.Kind is EventKind.KeyDown or EventKind.ButtonDown;

    // vk space is tiny; the interop lookup + ToString would otherwise allocate a fresh string per row render
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> KeyNames = new();

    public static string KeyName(int vk) => KeyNames.GetOrAdd(vk, static v =>
    {
        var k = KeyInterop.KeyFromVirtualKey(v);
        return k == Key.None ? $"VK 0x{v:X2}" : k.ToString();
    });

    public static string Name(this MacroEvent e) => e.Kind switch
    {
        EventKind.KeyDown or EventKind.KeyUp => KeyName(e.Vk),
        EventKind.ButtonDown or EventKind.ButtonUp => e.Button.ToString(),
        EventKind.TargetClick or EventKind.WaitTarget or EventKind.WaitGone => EventInfo.TargetLabel(e.Target),
        EventKind.When => EventInfo.TargetLabel(e.Target),
        EventKind.Discord => (e.Target ?? "").Length > 30 ? e.Target![..30] + "…" : e.Target ?? "",
        EventKind.Wheel => e.Delta >= 0 ? "Up" : "Down",
        EventKind.HWheel => e.Delta >= 0 ? "Right" : "Left",
        _ => "",
    };

    public static string KindLabel(this MacroEvent e) => e.Kind switch
    {
        EventKind.KeyDown => "Key ▼",
        EventKind.KeyUp => "Key ▲",
        EventKind.MouseMove => "Move",
        EventKind.RelMove => "Look",
        EventKind.TargetClick => "Click target",
        EventKind.WaitTarget => "Wait visible",
        EventKind.WaitGone => "Wait gone",
        EventKind.Discord => "Discord",
        EventKind.When => "When",
        EventKind.ButtonDown => "Button ▼",
        EventKind.ButtonUp => "Button ▲",
        EventKind.Wheel => "Scroll V",
        _ => "Scroll H",
    };

    /// <summary>Resolves a library macro id to its name (set by the app).</summary>
    public static Func<string, string?>? MacroNameOf { get; set; }

    /// <summary>"When 'Popup' appears -> run 'Rejoin'" in plain words.</summary>
    public static string WhenText(this MacroEvent e)
    {
        var f = e.Flow ?? new FlowOptions();
        string t = $"'{EventInfo.TargetLabel(e.Target)}'";
        string cond = f.Condition switch
        {
            WhenCondition.Appears => $"{t} appears",
            WhenCondition.Disappears => $"{t} disappears",
            _ => $"{t} does not appear within {f.WaitMs / 1000.0:0.#} s",
        };
        string act = f.Do switch
        {
            FlowDo.Restart => "restart the main macro",
            FlowDo.Continue => "continue",
            _ => f.Macro == null ? "run (no macro chosen)" : $"run '{MacroNameOf?.Invoke(f.Macro) ?? "missing macro"}'" + (f.After == FlowAfter.Restart ? ", then restart" : ""),
        };
        return $"When {cond} → {(f.ClickFirst ? "click it, " : "")}{act}{(f.Always ? " (watching all the time)" : "")}";
    }

    public static string Seconds(double ms) => (ms / 1000).ToString("0.000") + "s";

    /// <summary>One-line description used by hover labels.</summary>
    public static string Describe(this MacroEvent e) => e.Kind switch
    {
        EventKind.MouseMove => $"Move to ({e.X}, {e.Y})",
        EventKind.RelMove => $"Mouse look by ({e.X}, {e.Y}) counts (cursor hidden by the game; played back as relative movement)",
        EventKind.TargetClick => $"Click target '{e.Name()}' ({e.Button} at {e.Step?.AnchorText ?? "50% / 50%"}{(e.X != 0 || e.Y != 0 ? $" +{e.X},{e.Y}px" : "")}, wait up to {(e.Delta > 0 ? e.Delta : 10000) / 1000.0:0.#} s" +
            $"{(e.Step is { Retries: > 0 } s ? $", {s.Retries} retries" : "")}{(e.Extended || e.Step?.OnFail == FailAction.Skip ? ", skip if missing" : "")})",
        EventKind.WaitTarget => $"Wait until '{e.Name()}' is visible (up to {(e.Delta > 0 ? e.Delta : 10000) / 1000.0:0.#} s{(e.Extended ? ", then continue" : "")})",
        EventKind.WaitGone => $"Wait until '{e.Name()}' has disappeared (up to {(e.Delta > 0 ? e.Delta : 10000) / 1000.0:0.#} s{(e.Extended ? ", then continue" : "")})",
        EventKind.When => e.WhenText(),
        EventKind.Discord => $"Send Discord message{(e.Extended ? " + screenshot" : "")}: {e.Target}",
        EventKind.KeyDown or EventKind.KeyUp => $"{e.KindLabel()} {e.Name()}",
        EventKind.ButtonDown or EventKind.ButtonUp => $"{e.KindLabel()} {e.Name()} at ({e.X}, {e.Y})",
        _ => $"{e.KindLabel()} {e.Name()} {Math.Abs(e.Delta)} ({Math.Abs(e.Delta) / 120.0:0.##} notch) at ({e.X}, {e.Y})",
    };
}
