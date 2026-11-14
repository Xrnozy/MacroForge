namespace MacroStudio.Views;

/// <summary>What a drag carries: a library macro (by its id) onto a When block, or the When chip into the event list.</summary>
public static class MacroDrag
{
    public const string Format = "MacroStudio.MacroId";
    /// <summary>Set when the macro is dragged out of a folder in the library list: the folder macro's path.</summary>
    public const string FromFolder = "MacroStudio.FromFolder";
    /// <summary>All the macro ids being dragged out of the library list (string[]), and for each the folder it came from ("" = top level).</summary>
    public const string Many = "MacroStudio.ManyIds", Parents = "MacroStudio.ParentPaths";
    public const string WhenChip = "MacroStudio.WhenChip";
}
