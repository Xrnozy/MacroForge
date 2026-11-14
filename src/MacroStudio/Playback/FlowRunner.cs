using MacroStudio.Model;

namespace MacroStudio.Playback;

/// <summary>What the player does after a When block has acted.</summary>
public enum FlowStep { Continue, Restart, Stop }

/// <summary>Thrown from inside a waiting step when a watching When block says the main macro must start over / stop.</summary>
public sealed class FlowRestartException : Exception;
public sealed class FlowStopException : Exception;

/// <summary>Which macros are running right now, outermost first ("Aotr farm › Main menu › Aotr Menu"). Shared by a runner and the runners of its connected macros.</summary>
public sealed class FlowTrail
{
    readonly List<string> _names = new();
    volatile string _text = "";
    public string Text => _text;
    public void Push(string name) { lock (_names) { _names.Add(name); _text = string.Join(" › ", _names); } }
    public void Pop() { lock (_names) { if (_names.Count > 0) _names.RemoveAt(_names.Count - 1); _text = string.Join(" › ", _names); } }
    public void Clear() { lock (_names) { _names.Clear(); _text = ""; } }
}

/// <summary>
/// Everything the runners of ONE playback share (the main macro's runner and the runner of every connected macro): block counters, the nested-run
/// cap, the armed watchers, the click-phase token and the pending-restart flag. Only touched on the playback thread (the UI reads just <see cref="FlowTrail.Text"/>).
/// </summary>
public sealed class FlowRunState
{
    /// <summary>Run-wide cap on connected-macro executions (stops runaway loops that MaxRuns = 0 would allow).</summary>
    public const int MaxNestedRuns = 10_000;
    internal readonly Dictionary<(string Macro, int Index), int> Runs = new();   // block identity -> times it acted this run (a main-macro Restart does NOT reset it)
    internal readonly Dictionary<string, Sighting> LastSeen = new();             // target id -> last sighting ANY runner made: a watcher may be polled by a nested runner but its owner does the click
    internal readonly List<FlowRunner.Watcher> Watchers = new();
    internal int NestedRuns, Acting;
    internal double NextWatch;
    internal bool RestartPending, Halted;
    internal Player? Root;
}

/// <summary>The player's link to the When blocks.</summary>
public interface IFlowHost
{
    /// <summary>A When step is due. Checked here: evaluates and acts. Watching: arms it. Returns what to do next.</summary>
    FlowStep OnWhen(in MacroEvent e, int index);
    /// <summary>Called often (between steps, while waiting, inside waiting steps): lets armed watchers act. Cheap when nothing is due.</summary>
    FlowStep Poll();
    /// <summary>The main player is starting over: armed watchers are dropped (their When steps arm them again); counters are kept.</summary>
    void Restarting() { }
    /// <summary>The player running this host has finished (also after an error or stop): nothing may stay armed.</summary>
    void Ended() { }
    /// <summary>A new iteration of the main macro's Loops begins (not a Restart).</summary>
    void LoopStarted() { }
}

/// <summary>
/// Runs the When blocks of one macro. Conditions are read with the same target finder the click steps use. When a condition is met it
/// (optionally) clicks first, then runs the connected library macro in its own chain, restarts the main macro, or carries on.
/// </summary>
public sealed class FlowRunner : IFlowHost
{
    const int PollMs = 40, WatchEveryMs = 250, GoneDebounceMs = 200, CooldownMs = 2000, MaxDepth = 6;

    readonly ITargetFinder _finder;
    readonly IClock _clock;
    readonly Func<bool> _rawStop;
    readonly IInputSink _main;                                   // the main chain: optional clicks go through it, like any target click
    readonly Func<string, MacroEvent[]?> _load;                  // library macro id -> its events
    readonly Func<MacroEvent[], Func<int>, (IInputSink Play, IInputSink Click)> _chainFor;   // a full playback chain for another macro's events (and the sink its optional click goes through)
    readonly Func<double> _speed;
    readonly ExecutionLog? _log;
    readonly int _depth;
    readonly FlowRunState _state;
    readonly string? _macroName;                                 // shown in the log so a line says which macro its When block lives in
    readonly string _macroKey;                                   // id of the macro this runner belongs to ("" = the main macro): half of a block's identity
    public FlowTrail Trail { get; }

    // Semantics (one run = one playback of the main macro):
    //  * Do=Restart / After=Restart anywhere restarts the MAIN macro from its first event: a nested runner sets RestartPending and stops its player,
    //    every level above stops too (held keys released per level), the root runner turns it into FlowStep.Restart.
    //  * Continue returns to the parent right after its When step; a watcher that fired during a wait returns into the interrupted step (Player shifts its timing).
    //  * Stop (or a cap) stops everything. MaxRuns counts per block (macro id + event index) across the whole run, restarts included.
    //  * Watchers of every level are polled by whichever runner polls (nested players too); a watcher never re-enters itself and never fires while a click is in progress.
    internal sealed class Watcher(FlowRunner owner, MacroEvent e, int index)
    {
        public FlowRunner Owner = owner; public MacroEvent E = e; public int Index = index;
        public readonly FlowOptions F = e.Flow ?? new FlowOptions();   // read at every scan: resolved once, not per look
        public bool SeenVisible;            // Disappears: it must have been seen before it can disappear
        public double GoneSince = -1;       // Appears: since when it has not been seen (a new appearance only counts after that); Disappears: the debounce clock
        public double AbsentSince = -1;     // NotFound: since when it has not been seen
        public bool Fired;                  // Appears / NotFound: already acted for this appearance / absence
        public bool Running;                // its own action is in progress: never re-enters itself
        public double MuteUntil;            // after acting, give the screen time to change
    }

    bool Stopped() => _state.Halted || _rawStop();

    public FlowRunner(ITargetFinder finder, IClock clock, Func<bool> stop, IInputSink main, Func<string, MacroEvent[]?> load,
        Func<MacroEvent[], Func<int>, (IInputSink Play, IInputSink Click)> chainFor, Func<double>? speed = null, ExecutionLog? log = null, int depth = 0, FlowTrail? trail = null,
        FlowRunState? state = null, string macroKey = "", string? macroName = null)
    {
        Trail = trail ?? new FlowTrail();
        _state = state ?? new FlowRunState(); _macroKey = macroKey; _macroName = macroName;
        _finder = finder; _clock = clock; _rawStop = stop; _main = main; _load = load; _chainFor = chainFor; _speed = speed ?? (() => 1); _log = log; _depth = depth;
    }

    // ---------- a When step is due ----------

    public FlowStep OnWhen(in MacroEvent e, int index)
    {
        var f = e.Flow ?? new FlowOptions();
        string id = e.Target ?? "";
        string name = _finder.NameOf(id) ?? id;
        if (e.Target == null || _finder.NameOf(id) == null) { Log(index, name, LogResult.Failed, "the target is not in the library (deleted or from another PC); the block does nothing"); return FlowStep.Continue; }
        if (f.Always)
        {
            _state.Watchers.RemoveAll(w => w.Owner == this && w.Index == index);
            var s0 = _finder.Find(id);
            _state.Watchers.Add(new Watcher(this, e, index) { SeenVisible = s0 != null });
            if (s0 is { } armed) _state.LastSeen[id] = armed;      // a Disappears click-first needs a position even when it is never seen again
            Log(index, name, LogResult.Ok, $"now watching for '{name}' {Cond(f)} until the macro ends");
            return FlowStep.Continue;
        }
        Log(index, name, LogResult.Ok, $"waiting for '{name}' {Cond(f)} ({(f.KeepWaiting && f.Condition != WhenCondition.NotFound ? "no time limit" : $"up to {f.WaitMs / 1000.0:0.#} s")})");
        bool met = Evaluate(id, f, e, out string how);
        if (Stopped()) return FlowStep.Stop;
        if (!met) { Log(index, name, LogResult.Warn, $"{how}: nothing was run, carrying on. If the screen just needs longer to load, raise 'Wait up to' on this block"); return FlowStep.Continue; }
        return Act(e, index, name, how);
    }

    string Cond(FlowOptions f) => f.Condition switch { WhenCondition.Appears => "to appear", WhenCondition.Disappears => "to disappear", _ => $"to be missing for {f.WaitMs / 1000.0:0.#} s" };

    /// <summary>Checked-here evaluation: waits up to <see cref="FlowOptions.WaitMs"/>.</summary>
    bool Evaluate(string id, FlowOptions f, MacroEvent e, out string how)
    {
        double deadline = f.KeepWaiting && f.Condition != WhenCondition.NotFound ? double.MaxValue : _clock.NowMs + Math.Max(0, f.WaitMs), goneSince = -1;
        while (true)
        {
            if (_state.Root is { PausedForNested: true } && !Stopped()) { double t = _clock.NowMs; _clock.Sleep(10); if (deadline != double.MaxValue) deadline += _clock.NowMs - t; goneSince = -1; continue; }   // time spent paused is not waiting time
            var s = _finder.Find(id);
            if (s is { } seen) _state.LastSeen[id] = seen;
            switch (f.Condition)
            {
                case WhenCondition.Appears:
                    if (s != null) { how = "it is visible"; return true; }
                    break;
                case WhenCondition.Disappears:
                    if (s == null) { goneSince = goneSince < 0 ? _clock.NowMs : goneSince; if (_clock.NowMs - goneSince >= GoneDebounceMs) { how = "it is gone"; return true; } }
                    else goneSince = -1;
                    break;
                case WhenCondition.NotFound:
                    if (s != null) { how = "it is visible"; return false; }
                    break;
            }
            if (_clock.NowMs >= deadline || Stopped())
            {
                bool absent = f.Condition == WhenCondition.NotFound;       // never seen for the whole wait = the condition holds
                how = absent ? $"it did not appear within {f.WaitMs / 1000.0:0.#} s" : f.Condition == WhenCondition.Appears ? $"it did not appear within {f.WaitMs / 1000.0:0.#} s" : $"it was still visible after {f.WaitMs / 1000.0:0.#} s";
                return absent && !Stopped();
            }
            if (_state.Watchers.Count > 0)      // armed watchers (this macro's parents too) keep looking while this block waits
            {
                double before = _clock.NowMs;
                var st = Poll();
                if (st == FlowStep.Restart) throw new FlowRestartException();
                if (st == FlowStep.Stop) throw new FlowStopException();
                double spent = _clock.NowMs - before;
                if (spent > 50 && deadline != double.MaxValue) deadline += spent;
            }
            _clock.Sleep(PollMs);
        }
    }

    // ---------- watching ----------

    public FlowStep Poll()
    {
        var st = _state;
        if (st.Halted) return FlowStep.Stop;          // a cap stopped the run: executors' stop closures do not see it, their Idle hook does
        if (st.Watchers.Count == 0 || st.Acting > 0) return FlowStep.Continue;
        if (Stopped()) return FlowStep.Stop;          // a waiting step's Idle hook must be able to break the wait, not just skip the scan
        double now = _clock.NowMs;
        if (now < st.NextWatch) return FlowStep.Continue;
        st.NextWatch = now + WatchEveryMs;
        for (int i = 0; i < st.Watchers.Count; i++)
        {
            var w = st.Watchers[i];
            if (w.Running || now < w.MuteUntil) continue;
            var f = w.F;
            string id = w.E.Target!;
            var s = _finder.Find(id);
            if (s is { } seen) _state.LastSeen[id] = seen;
            bool fire = false; string how = "";
            switch (f.Condition)
            {
                case WhenCondition.Appears:
                    if (s != null) { w.GoneSince = -1; if (!w.Fired) { fire = true; w.Fired = true; how = "it is visible"; } }      // once per appearance: it must be gone for a moment before it counts as new
                    else { if (w.GoneSince < 0) w.GoneSince = now; else if (now - w.GoneSince >= GoneDebounceMs) w.Fired = false; }
                    break;
                case WhenCondition.Disappears:
                    if (s != null) { w.SeenVisible = true; w.GoneSince = -1; }
                    else if (w.SeenVisible)      // the same debounce as a waiting step: one missed frame is not a disappearance
                    {
                        if (w.GoneSince < 0) w.GoneSince = now;
                        else if (now - w.GoneSince >= GoneDebounceMs) { fire = true; w.SeenVisible = false; w.GoneSince = -1; how = "it is gone"; }
                    }
                    break;
                case WhenCondition.NotFound:
                    if (s != null) { w.AbsentSince = -1; w.Fired = false; }
                    else { if (w.AbsentSince < 0) w.AbsentSince = now; if (!w.Fired && now - w.AbsentSince >= f.WaitMs) { fire = true; w.Fired = true; how = $"it was missing for {f.WaitMs / 1000.0:0.#} s"; } }
                    break;
            }
            if (!fire) continue;
            string name = _finder.NameOf(id) ?? id;
            w.Running = true;
            FlowStep step;
            try { step = w.Owner.Act(w.E, w.Index, name, how); }
            finally { w.Running = false; }
            w.MuteUntil = _clock.NowMs + CooldownMs;
            st.NextWatch = _clock.NowMs + WatchEveryMs;
            return _depth > 0 ? Up(step) : step;     // one block at a time; the next one is looked at on the next poll
        }
        return FlowStep.Continue;
    }

    /// <summary>A restart asked from inside a connected macro must reach the main macro: below the root it becomes "stop this level, restart pending".</summary>
    FlowStep Up(FlowStep step)
    {
        if (step != FlowStep.Restart) return step;
        if (_depth > 0) { _state.RestartPending = true; return FlowStep.Stop; }
        _state.RestartPending = false;
        return FlowStep.Restart;
    }

    /// <summary>Called by <see cref="Player.Flow"/>: the main player, whose pause / step state nested players follow.</summary>
    public void AttachRoot(Player p) { if (_depth == 0) _state.Root = p; }

    /// <summary>A lifetime cap would kill a farm that loops for hours: the nested-run count starts over with every loop iteration (a Restart keeps it).</summary>
    public void LoopStarted() { if (_depth == 0) _state.NestedRuns = 0; }

    public void Restarting() { _state.Watchers.Clear(); _state.Acting = 0; _state.RestartPending = false; _state.NextWatch = 0; }

    public void Ended()
    {
        _state.Watchers.RemoveAll(w => w.Owner == this);
        if (_depth == 0) { _state.Watchers.Clear(); _state.Acting = 0; _state.RestartPending = false; Trail.Clear(); }
    }

    // ---------- acting ----------

    FlowStep Act(MacroEvent e, int index, string name, string how)
    {
        var f = e.Flow ?? new FlowOptions();
        var key = (_macroKey, index);
        int n = _state.Runs.GetValueOrDefault(key) + 1;
        if (f.MaxRuns > 0 && n > f.MaxRuns) { Log(index, name, LogResult.Warn, $"reached its limit of {f.MaxRuns} times: not acting again"); return FlowStep.Continue; }
        _state.Runs[key] = n;
        Log(index, name, LogResult.Ok, $"{how}: acting (time {n})");
        if (f.ClickFirst && f.Condition != WhenCondition.NotFound)
        {
            _state.Acting++;                      // no watcher may act while this click is being made
            try { ClickFirst(e, f, name, index); } finally { _state.Acting--; }
        }
        if (Stopped()) return FlowStep.Stop;
        switch (f.Do)
        {
            case FlowDo.Restart: Log(index, name, LogResult.Ok, "restarting the main macro from the beginning"); return Up(FlowStep.Restart);
            case FlowDo.Continue: return FlowStep.Continue;
        }
        if (f.Macro == null) { Log(index, name, LogResult.Warn, "no connected macro chosen: carrying on"); return FlowStep.Continue; }
        RunMacro(f.Macro, index, name);
        if (_state.RestartPending) return Up(FlowStep.Restart);       // something inside asked for a restart of the main macro
        if (Stopped()) return FlowStep.Stop;
        if (f.After == FlowAfter.Restart) { Log(index, name, LogResult.Ok, "restarting the main macro from the beginning"); return Up(FlowStep.Restart); }
        return FlowStep.Continue;
    }

    void ClickFirst(MacroEvent e, FlowOptions f, string name, int index)
    {
        var o = e.Step ?? new StepOptions();
        if (f.Condition == WhenCondition.Appears)
        {
            // an ordinary target click, so every part of the click-position and movement settings applies unchanged
            var click = e with { Kind = EventKind.TargetClick, Delta = 3000, Extended = true, Step = o with { OnFail = FailAction.Skip }, Flow = null };
            _main.Send(in click);
            return;
        }
        if (!_state.LastSeen.TryGetValue(e.Target!, out var last)) { Log(index, name, LogResult.Warn, "no position to click: it was never seen"); return; }
        var p = ClickPoint.Compute(last.Hit, o, e.X, e.Y);     // where it was the last time it was seen
        _main.Send(new MacroEvent(0, EventKind.MouseMove, X: p.X, Y: p.Y));
        var down = new MacroEvent(0, EventKind.ButtonDown, Button: e.Button, X: p.X, Y: p.Y);
        _main.Send(in down);
        try { _clock.Sleep(60); }
        finally { var up = down with { Kind = EventKind.ButtonUp }; _main.Send(in up); }   // never leave the button held, whatever the sink does
    }

    void RunMacro(string macroId, int index, string name)
    {
        string label = EventInfo.MacroNameOf?.Invoke(macroId) ?? macroId;
        if (_depth >= MaxDepth) { Log(index, name, LogResult.Failed, $"'{label}' is nested too deeply (a macro that runs itself?)"); return; }
        if (_state.NestedRuns >= FlowRunState.MaxNestedRuns)
        {
            _state.Halted = true;
            Log(index, name, LogResult.Failed, $"stopped: more than {FlowRunState.MaxNestedRuns:N0} connected macros ran in this playback (a loop that never ends?)");
            return;
        }
        var events = _load(macroId);
        if (events == null) { Log(index, name, LogResult.Failed, $"the connected macro '{label}' was not found in the library"); return; }
        if (events.Length == 0) return;
        _state.NestedRuns++;
        Log(index, name, LogResult.Ok, $"running '{label}'");
        Player? p = null; FlowRunner? child = null; IInputSink? play = null, click = null;
        bool pushed = false;
        try
        {
            (play, click) = _chainFor(events, () => p?.CurrentIndex ?? 0);
            child = new FlowRunner(_finder, _clock, _rawStop, click, _load, _chainFor, _speed, _log, _depth + 1, Trail, _state, macroId, label);   // its own clicks go through its own chain
            var root = _state.Root;
            p = new Player(play, _clock) { Log = _log, Flow = child, ExternalStop = Stopped, Speed = _speed() };
            if (click is StepExecutor se && se.Idle == null)    // a click of this macro that waits for its target keeps the watchers of every level looking
            {
                var c = child;
                se.Idle = () => { var s = c.Poll(); if (s == FlowStep.Restart) throw new FlowRestartException(); if (s == FlowStep.Stop) throw new FlowStopException(); };
            }
            if (root != null) p.ExternalPause = () => root.PausedForNested;   // pausing the main player pauses this one too; a stepped When block runs it freely
            Trail.Push(label); pushed = true;
            p.Run(events, 0, events.Length, 1);          // returns only when this macro, and every macro its own When blocks ran, has finished
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log(index, name, LogResult.Failed, $"'{label}' could not run: {ex.Message}"); return; }
        finally
        {
            if (pushed) Trail.Pop();
            child?.Ended();                              // nothing armed inside it outlives it
            if (play is IDisposable d1 && !ReferenceEquals(play, _main)) try { d1.Dispose(); } catch { }
            if (click is IDisposable d2 && !ReferenceEquals(click, play) && !ReferenceEquals(click, _main)) try { d2.Dispose(); } catch { }
        }
        if (p.Error != null) Log(index, name, LogResult.Failed, $"'{label}' stopped with an error: {p.Error.Message}");
        else if (_state.RestartPending) Log(index, name, LogResult.Ok, $"'{label}' ended early: the main macro is restarting");
        else Log(index, name, LogResult.Ok, $"'{label}' finished");
    }

    void Log(int index, string name, LogResult r, string message) => _log?.Add(index, _macroName == null ? $"when '{name}'" : $"{_macroName}: when '{name}'", r, message);
}
