using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>
/// Which recorded mouse moves are the approach to an adaptive click, and how far along that approach each one is.
/// Computed from the recording without changing it. A move belongs to the next adaptive click when only mouse moves lie between them
/// (and not more than <see cref="LookbackMs"/> earlier), so keyboard input, scrolls or other clicks end an approach.
/// </summary>
public sealed class TrajectoryPlan
{
    public const double LookbackMs = 4000;

    public readonly record struct Approach(int Click, double Progress);

    readonly Dictionary<int, Approach> _byMove = new();
    readonly Dictionary<int, (int X, int Y)> _original = new();
    readonly Dictionary<int, (double DurationMs, double Length)> _info = new();
    readonly Dictionary<int, (double[] Len, double[] Ms, double[] Progress)> _steps = new();

    readonly HashSet<int> _optimized = new();

    /// <summary>The approach to this click is replaced by a direct movement: its recorded moves are not replayed.</summary>
    public bool HasApproach(int click) => _original.ContainsKey(click);

    public bool IsOptimized(int click) => _optimized.Contains(click);

    public int ApproachMoves => _byMove.Count;

    public static TrajectoryPlan Build(IReadOnlyList<MacroEvent> ev)
    {
        var plan = new TrajectoryPlan();
        for (int j = 0; j < ev.Count; j++)
        {
            if (ev[j].Kind != EventKind.TargetClick) continue;
            var st = ev[j].Step ?? StepOptions.Legacy(ev[j].Extended);        // a click without settings is planned like any other
            if (st.Move is not (MoveMode.FollowPath or MoveMode.Optimized)) continue;
            int first = j;
            // Optimized drops the whole run of moves before the click (however long the user wandered); FollowPath bends only the last few seconds
            bool direct = st.Move == MoveMode.Optimized;
            while (first - 1 >= 0 && ev[first - 1].Kind == EventKind.MouseMove && (direct || ev[j].T - ev[first - 1].T <= LookbackMs)) first--;
            if (first == j) continue;                                           // no recorded approach: the click will glide instead

            // where the user was heading when they recorded the click: the position stored with the step (converted clicks), else where the path ended
            var step = st;
            plan._original[j] = step.FallbackX != 0 || step.FallbackY != 0 ? (step.FallbackX, step.FallbackY) : (ev[j - 1].X, ev[j - 1].Y);

            // progress along the path by distance travelled (not time), so a pause in the recording does not stall the bend
            double total = 0;
            for (int k = first + 1; k < j; k++) total += Dist(ev[k - 1], ev[k]);
            plan._info[j] = (ev[j - 1].T - ev[first].T, total);
            var len = new double[j - first]; var ms = new double[j - first]; var prog = new double[j - first];
            double run = 0;
            for (int k = first; k < j; k++)
            {
                if (k > first) run += Dist(ev[k - 1], ev[k]);
                plan._byMove[k] = new Approach(j, total <= 0 ? 1 : run / total);
                prog[k - first] = total <= 0 ? 1 : run / total;
                if (k > first) { len[k - first] = Dist(ev[k - 1], ev[k]); ms[k - first] = ev[k].T - ev[k - 1].T; }
            }
            plan._steps[j] = (len, ms, prog);
            if (st.Move == MoveMode.Optimized) plan._optimized.Add(j);
        }
        return plan;
    }

    /// <summary>
    /// How far (px) the approach may be bent towards a target without any single step of the path exceeding <paramref name="maxSpeed"/> px/ms.
    /// Step by step: a step covers its recorded distance plus (change in eased bend share) × bend, and must fit in that step's time.
    /// Where the user themselves moved faster than the limit, their own peak (+25%) is the limit, so the bend never adds to an already fast flick.
    /// </summary>
    public double MaxBend(int click, double playbackSpeed, double maxSpeed)
    {
        var (len, ms, prog) = _steps[click];
        if (len.Length < 2) return 0;                                     // a single recorded point: nothing to spread a bend over
        double speed = Math.Max(0.1, playbackSpeed), peak = 0;
        for (int k = 1; k < len.Length; k++) if (ms[k] > 0) peak = Math.Max(peak, len[k] / (ms[k] / speed));
        double v = Math.Max(maxSpeed, 1.25 * peak), best = double.MaxValue;
        for (int k = 1; k < len.Length; k++)
        {
            double dw = Ease(prog[k]) - Ease(prog[k - 1]);
            if (dw <= 1e-9) continue;
            double room = v * (ms[k] / speed) - len[k];
            best = Math.Min(best, room <= 0 ? 0 : room / dw);
        }
        return best == double.MaxValue ? 0 : best;
    }

    static double Ease(double s) { s = Math.Clamp(s, 0, 1); return s * s * (3 - 2 * s); }

    public bool TryGet(int moveIndex, out Approach a) => _byMove.TryGetValue(moveIndex, out a);

    /// <summary>The recorded click position (raw recording coordinates) this approach was heading to.</summary>
    public (int X, int Y) OriginalOf(int click) => _original[click];

    /// <summary>How long the recorded approach lasted and how long the path was, in raw recording units (ms, px).</summary>
    public (double DurationMs, double Length) InfoOf(int click) => _info[click];

    static double Dist(MacroEvent a, MacroEvent b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}
