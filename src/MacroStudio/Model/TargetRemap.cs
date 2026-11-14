namespace MacroStudio.Model;

/// <summary>Which visual targets a step points at, and re-pointing a step at other targets (when steps are copied into another recording).</summary>
public static class TargetRemap
{
    /// <summary>The target ids a step uses: its own target, the target to verify against, and the recovery target.</summary>
    public static IEnumerable<string> Used(MacroEvent e)
    {
        if ((e.IsTarget() || e.IsWhen()) && e.Target != null) yield return e.Target;
        if (e.Step is { } s)
        {
            if (s.VerifyTarget != null) yield return s.VerifyTarget;
            if (s.RecoveryTarget != null) yield return s.RecoveryTarget;
        }
    }

    public static MacroEvent Apply(MacroEvent e, IReadOnlyDictionary<string, string> map)
    {
        if ((e.IsTarget() || e.IsWhen()) && e.Target != null && map.TryGetValue(e.Target, out var n)) e = e with { Target = n };
        if (e.Step is { } st)
        {
            string? Map(string? id) => id != null && map.TryGetValue(id, out var m) ? m : id;
            var verify = Map(st.VerifyTarget); var recovery = Map(st.RecoveryTarget);
            if (verify != st.VerifyTarget || recovery != st.RecoveryTarget)
                e = e with { Step = st with { VerifyTarget = verify, RecoveryTarget = recovery } };
        }
        return e;
    }
}
