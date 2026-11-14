namespace MacroStudio.Model;

/// <summary>What a When block waits for.</summary>
public enum WhenCondition { Appears, Disappears, NotFound }

/// <summary>What happens when the condition is met. Continue = resume the main macro where it was interrupted.</summary>
public enum FlowDo { RunMacro, Restart, Continue }

/// <summary>What happens after the connected macro has run.</summary>
public enum FlowAfter { Continue, Restart }

/// <summary>
/// Settings of a When step: "when [Target] appears / disappears / does not appear, (optionally click it first, then) run the connected macro,
/// restart the main macro or carry on". Click position reuses the step's <see cref="StepOptions"/> (anchor, offset in X/Y).
/// </summary>
public sealed record FlowOptions
{
    public WhenCondition Condition { get; init; }
    /// <summary>Watch from this step to the end of the run instead of checking only here.</summary>
    public bool Always { get; init; }
    /// <summary>Checked here: how long to wait for the condition (0 = look once). Also the limit of "does not appear".</summary>
    public int WaitMs { get; init; } = 5000;
    /// <summary>Checked here: no time limit. The macro stays on this step until the condition is met (or it is stopped), instead of carrying on after <see cref="WaitMs"/>.</summary>
    public bool KeepWaiting { get; init; }
    public bool ClickFirst { get; init; }
    public FlowDo Do { get; init; } = FlowDo.RunMacro;
    /// <summary>The connected macro: <see cref="Macro.Id"/> of a library recording.</summary>
    public string? Macro { get; init; }
    public FlowAfter After { get; init; }
    /// <summary>Safety: how often this block may act in one run (0 = no limit).</summary>
    public int MaxRuns { get; init; } = 10;
}
