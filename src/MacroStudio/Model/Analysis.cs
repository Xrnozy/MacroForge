namespace MacroStudio.Model;

/// <summary>Derived facts about an immutable event array: Down/Up pairing, timeline rows, problems. Indexed by array index.</summary>
public sealed class Analysis
{
    public int[] Partner = [];          // paired Up/Down index, -1 if none
    public int[] Row = [];              // timeline sub-row for key/button pairs
    public int KeyRows = 1, ButtonRows = 1;
    public Dictionary<int, string> Issues = new();

    /// <summary>Set by the app so target-click steps can be checked against the library.</summary>
    public static Func<string, bool>? TargetExists { get; set; }
    /// <summary>Set by the app: does the library hold a macro with this id?</summary>
    public static Func<string, bool>? MacroExists { get; set; }
    /// <summary>Set by the app: the id of the macro being analysed, so a block that runs its own macro is flagged.</summary>
    public static Func<string?>? CurrentMacroId { get; set; }

    static void WhenIssue(Analysis a, int i, in MacroEvent e)
    {
        var f = e.Flow ?? new FlowOptions();
        if (e.Target == null) a.Issues[i] = "Choose which visual target this block watches";
        else if (TargetExists?.Invoke(e.Target) == false) a.Issues[i] = "Target is not in the library";
        else if (f.Do == FlowDo.RunMacro && f.Macro == null) a.Issues[i] = "Drop a macro here, or choose another action";
        else if (f.Do == FlowDo.RunMacro && f.Macro != null && MacroExists?.Invoke(f.Macro) == false) a.Issues[i] = "The connected macro is not in the library";
        else if (f.Macro != null && f.Macro == CurrentMacroId?.Invoke()) a.Issues[i] = "This block runs the macro it is in";
        else if (f.Condition == WhenCondition.NotFound && f.ClickFirst) a.Issues[i] = "Cannot click a target that does not appear";
    }

    public static Analysis Of(MacroEvent[] ev, DesktopInfo? desktop)
    {
        var a = new Analysis { Partner = new int[ev.Length], Row = new int[ev.Length] };
        Array.Fill(a.Partner, -1);
        var heldKeys = new Dictionary<int, int>();
        var heldBtns = new Dictionary<MouseBtn, int>();
        var busyKey = new List<bool>();
        var busyBtn = new List<bool>();
        var targetExists = TargetExists;                 // static-property reads hoisted out of the loop
        bool hasVirt = desktop != null;
        var virt = desktop?.Virtual ?? default;          // one field load instead of a record-property chain per event

        static int Take(List<bool> busy)
        {
            int r = busy.IndexOf(false);
            if (r < 0) { busy.Add(true); return busy.Count - 1; }
            busy[r] = true;
            return r;
        }

        for (int i = 0; i < ev.Length; i++)
        {
            ref readonly var e = ref ev[i];              // field reads in place: no 72-byte struct copy per event
            var kind = e.Kind;

            if (i > 0 && e.T < ev[i - 1].T) a.Issues[i] = "Time goes backwards";
            // HasPos() inlined: the kinds that carry a screen position, checked without call chains
            else if (hasVirt && kind is EventKind.MouseMove or EventKind.ButtonDown or EventKind.ButtonUp or EventKind.Wheel or EventKind.HWheel
                     && !(e.X >= virt.X && e.X < virt.X + virt.Width && e.Y >= virt.Y && e.Y < virt.Y + virt.Height))
                a.Issues[i] = "Outside recorded desktop";

            switch (kind)
            {
                case EventKind.TargetClick:
                    if (e.Target == null || targetExists?.Invoke(e.Target) == false) a.Issues[i] = "Target is not in the library";
                    break;
                case EventKind.When:
                    WhenIssue(a, i, in e);
                    break;
                case EventKind.KeyDown:
                    if (heldKeys.TryAdd(e.Vk, i)) a.Row[i] = Take(busyKey);   // repeated downs are auto-repeat
                    break;
                case EventKind.KeyUp:
                    if (heldKeys.Remove(e.Vk, out int kd)) { a.Partner[i] = kd; a.Partner[kd] = i; a.Row[i] = a.Row[kd]; busyKey[a.Row[kd]] = false; }
                    else a.Issues[i] = "Release without press";
                    break;
                case EventKind.ButtonDown:
                    if (heldBtns.TryAdd(e.Button, i)) a.Row[i] = Take(busyBtn);
                    break;
                case EventKind.ButtonUp:
                    if (heldBtns.Remove(e.Button, out int bd)) { a.Partner[i] = bd; a.Partner[bd] = i; a.Row[i] = a.Row[bd]; busyBtn[a.Row[bd]] = false; }
                    else a.Issues[i] = "Release without press";
                    break;
            }
        }
        foreach (var i in heldKeys.Values) a.Issues[i] = "Never released";
        foreach (var i in heldBtns.Values) a.Issues[i] = "Never released";
        a.KeyRows = Math.Max(1, busyKey.Count);
        a.ButtonRows = Math.Max(1, busyBtn.Count);
        return a;
    }
}
