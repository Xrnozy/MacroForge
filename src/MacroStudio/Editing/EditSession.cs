using System.Runtime.InteropServices;
using MacroStudio.Model;
using MacroStudio.Storage;

namespace MacroStudio.Editing;

/// <summary>
/// The one shared model all views bind to: an immutable-per-version event array, selection, playhead and undo/redo.
/// Every edit replaces the array (old arrays go on the undo stack), so views never see a half-edited list.
///
/// Threading: every mutation of the event array runs inside <see cref="_gate"/>, so a published <see cref="Events"/> array is always
/// complete (sorted, unique ids) and undo/redo/Version stay consistent under concurrent Append/Load. Lazy derived data
/// (analysis, position index, id map) is built for one exact array and published with it as one reference, so a racing reader
/// can hold a stale-but-consistent pair, never a map of the wrong version. Selection, playhead and the metadata properties
/// remain UI-thread-owned, exactly as before; <see cref="IsBreakpoint"/> has its own lock for the playback thread.
/// </summary>
public sealed class EditSession
{
    readonly object _gate = new();
    volatile MacroEvent[] _ev = [];
    List<MacroEvent>? _tail;   // appended events not yet merged into _ev: Append is O(tail), the merge is paid once by the next reader
    int _nextId = 1;
    readonly List<MacroEvent[]> _undo = new(), _redo = new();
    string? _lastCoalesce;
    volatile int _version;
    volatile AnCache? _an;
    volatile PosCache? _pos;
    volatile IdCache? _ids;

    /// <summary>Derived data plus the exact event array it was built from: one immutable reference, so readers get a consistent pair.</summary>
    sealed record AnCache(MacroEvent[] Ev, DesktopInfo? Desk, Analysis A);
    sealed record PosCache(MacroEvent[] Ev, int[] Pos);
    sealed record IdCache(MacroEvent[] Ev, Dictionary<int, int> Map);

    public string? Id { get; set; }
    /// <summary>Macros filed under this one in the library (kept so saving the open macro never drops them).</summary>
    public List<string> Subs { get; set; } = new();
    public string Name { get; set; } = "Untitled";
    public DateTimeOffset Created { get; private set; } = DateTimeOffset.Now;
    public DesktopInfo? Desktop { get; private set; }
    public EnvInfo? Env { get; private set; }
    public string? Path { get; set; }
    public bool Dirty { get; set; }
    public int Version => _version;

    /// <summary>The current event array, merging an appended tail into it first. Published arrays stay immutable. Call holding <see cref="_gate"/>.</summary>
    MacroEvent[] Ev()
    {
        if (_tail is { Count: > 0 } t)
        {
            var next = new MacroEvent[_ev.Length + t.Count];
            Array.Copy(_ev, next, _ev.Length);
            CollectionsMarshal.AsSpan(t).CopyTo(next.AsSpan(_ev.Length));
            _ev = next; _tail = null;
        }
        return _ev;
    }

    public MacroEvent[] Events { get { lock (_gate) return Ev(); } }
    public Analysis Analysis { get { lock (_gate) return AnOf(Ev(), Desktop).A; } }
    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }

    public HashSet<int> Selected { get; } = new();
    /// <summary>When blocks (by event id) that are opened like a folder in the event list. Display state only: never saved, never undone.</summary>
    public HashSet<int> Expanded { get; } = new();
    public int Primary { get; private set; }
    public double PlayheadMs { get; private set; }

    public event Action? EventsChanged, SelectionChanged, PlayheadChanged, BreakpointsChanged;

    // ---- breakpoints (playback pauses before these events) ----
    readonly HashSet<int> _breakpoints = new();
    public bool IsBreakpoint(int id) { lock (_breakpoints) return _breakpoints.Contains(id); }
    public int BreakpointCount { get { lock (_breakpoints) return _breakpoints.Count; } }
    public void ToggleBreakpoint(int id)
    {
        lock (_breakpoints) if (!_breakpoints.Remove(id)) _breakpoints.Add(id);
        BreakpointsChanged?.Invoke();
    }

    // ---- load / save ----

    public void Load(Macro m, string? path)
    {
        lock (_gate)
        {
            var list = m.Events;
            if (path == null && Path == null && TryAppend(m)) return;
            bool idsOk = list.All(e => e.Id > 0) && list.Select(e => e.Id).Distinct().Count() == list.Count;
            var arr = Sorted(idsOk ? list.ToArray() : list.Select((e, i) => e with { Id = i + 1 }).ToArray());
            _tail = null;   // a full load replaces everything, including an unmerged tail
            _ev = arr;
            _nextId = arr.Length == 0 ? 1 : arr.Max(e => e.Id) + 1;
            Id = m.Id; Subs = m.Subs.ToList(); Name = m.Name; Created = m.Created; Desktop = m.Desktop; Env = m.Env; Path = path; Dirty = false;
            _undo.Clear(); _redo.Clear(); _lastCoalesce = null;
            Selected.Clear(); Expanded.Clear(); Primary = 0; PlayheadMs = 0;
            lock (_breakpoints) _breakpoints.Clear();
            Changed();
            SelectionChanged?.Invoke();
            PlayheadChanged?.Invoke();
        }
    }

    /// <summary>
    /// The live-recording tick: the snapshot is the current events plus a few new ones. An in-place-identical head is accepted with
    /// no copy at all; a head that only matches after re-sorting (equal timestamps in a different order) is verified on a reused
    /// scratch, still without publishing a new array when nothing changed. Only the grown tail is ever copied into a fresh array.
    /// Returns false (nothing changed) when the snapshot is anything else; Load then takes the full path, which is always right.
    /// Call holding <see cref="_gate"/>.
    /// </summary>
    bool TryAppend(Macro m)
    {
        var list = m.Events; var cur = Ev(); int n = cur.Length;
        if (n == 0 || list.Count < n) return false;
        var snap = CollectionsMarshal.AsSpan(list);
        // the head must equal the current array, in place (recorded events never change): the cheapest possible identity check
        int i = 0;
        for (; i < n; i++)
        {
            ref readonly var s = ref cur[i]; ref readonly var e = ref snap[i];
            int id = e.Id == 0 ? i + 1 : e.Id;   // a snapshot may carry no ids: Load numbers them by position
            if (id != s.Id || e.T != s.T || e.Kind != s.Kind || e.X != s.X || e.Y != s.Y || e.Vk != s.Vk || e.Delta != s.Delta) break;
        }
        if (i < n) return TryAppendSorted(m, snap, cur, n);   // the snapshot may still match once sorted, like the full path would
        int nid = _nextId;
        bool evChanged = false;
        if (list.Count > n)
        {
            double maxT = cur[n - 1].T;          // the published array is always sorted: a new event older than the end belongs inside the head
            var next = new MacroEvent[list.Count];
            Array.Copy(cur, next, n);
            for (i = n; i < next.Length; i++)
            {
                var e = snap[i];
                if (e.Id == 0) e = e with { Id = nid++ };
                else { if (e.Id < nid) return false; nid = e.Id + 1; }
                if (e.T < maxT) return false;    // would sort into the head and change old events: the full path handles that
                next[i] = e;
            }
            // stable insertion sort of the tail only: the head was verified identical to the current array, which is sorted; bail when the tail is not nearly sorted
            int moved = 0;
            for (i = n + 1; i < next.Length; i++)
            {
                if (next[i - 1].T <= next[i].T) continue;
                var x = next[i]; int j = i;
                for (; j > n && next[j - 1].T > x.T; j--) { next[j] = next[j - 1]; if (++moved > 10_000) return false; }
                next[j] = x;
            }
            _ev = next; _nextId = nid; evChanged = true;
        }
        TakeMeta(m, evChanged);
        return true;
    }

    MacroEvent[]? _scratch;   // sort-verify buffer: never published, so it is reused instead of allocated per tick

    /// <summary>
    /// TryAppend for a snapshot whose head is out of order (equal timestamps swapped, a re-sorted recording): stamp ids, run the
    /// same nearly-sorted insertion sort the full path uses, and compare the result to the current array. When it only re-orders,
    /// nothing is published; when it grew, the fresh array becomes the event list — either way the semantics match the old code.
    /// </summary>
    bool TryAppendSorted(Macro m, Span<MacroEvent> snap, MacroEvent[] cur, int n)
    {
        int count = snap.Length;
        bool grown = count > n;
        var buf = grown ? new MacroEvent[count] : _scratch is { } sc && sc.Length >= count ? sc : _scratch = new MacroEvent[count];
        int nid = _nextId;
        for (int i = 0; i < count; i++)
        {
            var e = snap[i];
            if (e.Id == 0) e = e with { Id = i < n ? i + 1 : nid++ };
            else if (i >= n) { if (e.Id < nid) return false; nid = e.Id + 1; }
            buf[i] = e;
        }
        // stable insertion sort: a snapshot is nearly sorted, so this is one pass; bail when it is not
        int moved = 0;
        for (int i = 1; i < count; i++)
        {
            if (buf[i - 1].T <= buf[i].T) continue;
            var x = buf[i]; int j = i;
            for (; j > 0 && buf[j - 1].T > x.T; j--) { buf[j] = buf[j - 1]; if (++moved > 10_000) return false; }
            buf[j] = x;
        }
        for (int i = 0; i < n; i++)
        {
            ref readonly var s = ref cur[i]; ref readonly var e = ref buf[i];
            if (s.Id != e.Id || s.T != e.T || s.Kind != e.Kind || s.X != e.X || s.Y != e.Y || s.Vk != e.Vk || s.Delta != e.Delta) return false;
        }
        if (grown) { _ev = buf; _nextId = nid; }
        TakeMeta(m, grown);
        return true;
    }

    void TakeMeta(Macro m, bool evChanged)   // call holding _gate: snapshot accepted, refresh metadata and signal one version
    {
        Id = m.Id; Name = m.Name; Created = m.Created; Desktop = m.Desktop; Env = m.Env; Dirty = false;
        _undo.Clear(); _redo.Clear(); _lastCoalesce = null;
        Changed(evChanged);   // when _ev kept its reference the ref-keyed caches are still valid: keep them warm
    }

    /// <summary>
    /// The live-recording tick with only the new events: validates and queues the tail in O(tail) — the one block copy into a fresh
    /// array happens lazily in <see cref="Ev"/>, amortised over however many appends land before the next read.
    /// False (nothing changed) when the session is empty or the tail does not continue it in order; the caller then Loads the full snapshot.
    /// </summary>
    public bool Append(IReadOnlyList<MacroEvent> tail)
    {
        lock (_gate)
        {
            int total = _ev.Length + (_tail?.Count ?? 0);
            if (total == 0) return false;
            if (tail.Count == 0) return true;
            double last = _tail is { Count: > 0 } p ? p[^1].T : _ev[^1].T;
            int nid = _nextId;
            var add = new List<MacroEvent>(tail.Count);
            for (int i = 0; i < tail.Count; i++)
            {
                var e = tail[i];
                if (e.Id == 0) e = e with { Id = nid++ };
                else { if (e.Id < nid) return false; nid = e.Id + 1; }
                if (e.T < last) return false;
                last = e.T; add.Add(e);
            }
            (_tail ??= new List<MacroEvent>(add.Count)).AddRange(add);
            _nextId = nid; Dirty = false;
            _undo.Clear(); _redo.Clear(); _lastCoalesce = null;
            Changed();
            return true;
        }
    }

    public Macro ToMacro() { lock (_gate) { var ev = Ev(); return new() { Id = Id, Subs = Subs.ToList(), Name = Name, Created = Created, Desktop = Desktop, Env = Env, Events = ev.ToList() }; } }

    // ---- lookups ----

    /// <summary>Derived data for one exact event array: built into a local, then published as a single reference — never half-filled.</summary>
    AnCache AnOf(MacroEvent[] ev, DesktopInfo? desk)
    {
        var c = _an;
        if (c == null || !ReferenceEquals(c.Ev, ev) || !ReferenceEquals(c.Desk, desk)) _an = c = new(ev, desk, Analysis.Of(ev, desk));
        return c;
    }

    PosCache PosOf(MacroEvent[] ev)
    {
        var c = _pos;
        if (c == null || !ReferenceEquals(c.Ev, ev))
        {
            var r = new int[ev.Length]; int n = 0;
            for (int i = 0; i < ev.Length; i++) if (ev[i].HasPos()) r[n++] = i;
            _pos = c = new(ev, n == r.Length ? r : r[..n]);
        }
        return c;
    }

    IdCache Ids(MacroEvent[] ev)
    {
        var c = _ids;
        if (c == null || !ReferenceEquals(c.Ev, ev))
        {
            var map = new Dictionary<int, int>(ev.Length);
            for (int i = 0; i < ev.Length; i++) map[ev[i].Id] = i;
            _ids = c = new(ev, map);
        }
        return c;
    }

    public int IndexOfId(int id) { lock (_gate) return Ids(Ev()).Map.TryGetValue(id, out var ix) ? ix : -1; }

    /// <summary>First index with T &gt;= t.</summary>
    public int LowerBound(double t)
    {
        lock (_gate)
        {
            var ev = Ev();
            int lo = 0, hi = ev.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (ev[mid].T < t) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }

    /// <summary>Indices of events that carry a screen position (everything except keys).</summary>
    public int[] PosIndex { get { lock (_gate) return PosOf(Ev()).Pos; } }

    public double Duration { get { lock (_gate) { var ev = Ev(); return ev.Length == 0 ? 0 : ev[^1].T; } } }

    /// <summary>Cursor position at time t, interpolated between the surrounding positional events.</summary>
    public (double X, double Y)? PositionAt(double t)
    {
        lock (_gate)
        {
            var ev = Ev(); var p = PosOf(ev).Pos;
            if (p.Length == 0) return null;
            int lo = 0, hi = p.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (ev[p[mid]].T <= t) lo = mid + 1; else hi = mid; }
            if (lo == 0) return (ev[p[0]].X, ev[p[0]].Y);
            var a = ev[p[lo - 1]];
            if (lo == p.Length) return (a.X, a.Y);
            var b = ev[p[lo]];
            double f = b.T == a.T ? 1 : (t - a.T) / (b.T - a.T);
            return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }
    }

    /// <summary>Index of the last event at or before the playhead, -1 if none.</summary>
    public int PlayheadIndex => LowerBound(PlayheadMs + 1e-9) - 1;

    // ---- selection / playhead (UI-thread-owned, like the Selected/Expanded sets themselves) ----

    public void Select(IEnumerable<int> ids, int primary = 0)
    {
        var set = ids.ToHashSet();
        if (primary == 0 || !set.Contains(primary)) primary = set.Count == 0 ? 0 : set.OrderBy(IndexOfId).First(); // earliest event leads
        if (set.SetEquals(Selected) && primary == Primary) return;
        Selected.Clear(); Selected.UnionWith(set); Primary = primary;
        SelectionChanged?.Invoke();
        if (PrimaryEvent is { } pe) SetPlayhead(pe.T); // selecting an event locates it in every view
    }

    public void SelectOne(int id) => Select([id], id);
    public void ClearSelection() => Select([]);

    public void Toggle(int id)
    {
        var s = Selected.ToHashSet();
        if (!s.Remove(id)) s.Add(id);
        Select(s, s.Contains(id) ? id : 0);
    }

    public MacroEvent? PrimaryEvent
    {
        get { lock (_gate) { var ev = Ev(); return Ids(ev).Map.TryGetValue(Primary, out var i) ? ev[i] : null; } }
    }

    public void SetPlayhead(double ms)
    {
        ms = Math.Max(0, ms);
        if (ms == PlayheadMs) return;
        PlayheadMs = ms;
        PlayheadChanged?.Invoke();
    }

    // ---- core mutation (all of this runs inside _gate) ----

    static MacroEvent[] Sorted(MacroEvent[] ev)
    {
        bool sorted = true;
        for (int i = 1; i < ev.Length && sorted; i++) sorted = ev[i - 1].T <= ev[i].T;
        if (sorted) return ev;
        return ev.Select((e, i) => (e, i)).OrderBy(x => x.e.T).ThenBy(x => x.i).Select(x => x.e).ToArray(); // stable
    }

    void Changed(bool invalidateCaches = true)   // call holding _gate; the caches are keyed on the exact array, so they can stay warm when it did
    {
        _version++;
        if (invalidateCaches) { _an = null; _pos = null; _ids = null; }
        EventsChanged?.Invoke();
    }

    /// <summary>Replace the event array. Same non-null coalesce key as the previous call merges into one undo step (drags). Call holding _gate.</summary>
    void Apply(MacroEvent[] next, string? coalesce = null)
    {
        next = Sorted(next);
        if (coalesce == null || coalesce != _lastCoalesce)
        {
            _undo.Add(Ev());
            long total = 0; int keep = 0; // newest steps first, until ~500k events of history (at least 2 steps, at most 100)
            for (int i = _undo.Count - 1; i >= 0 && keep < 100 && (keep < 2 || total + _undo[i].Length <= 500_000); i--) { total += _undo[i].Length; keep++; }
            if (_undo.Count > keep) _undo.RemoveRange(0, _undo.Count - keep);
            _redo.Clear();
        }
        _lastCoalesce = coalesce;
        Swap(next);
    }

    void Swap(MacroEvent[] next)   // call holding _gate; callers flush Ev() first, so nothing is still pending
    {
        _tail = null;
        _ev = next;
        Dirty = true;
        Changed();
        if (Selected.RemoveWhere(id => IndexOfId(id) < 0) > 0)
        {
            if (IndexOfId(Primary) < 0) Primary = Selected.Count == 0 ? 0 : Selected.First();
            SelectionChanged?.Invoke();
        }
    }

    public void EndCoalesce() { lock (_gate) _lastCoalesce = null; }

    /// <summary>Re-run checks and redraw after something outside the event list changed (e.g. the target library).</summary>
    public void Revalidate() { lock (_gate) Changed(); }

    public void Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return;
            _redo.Add(Ev());
            var prev = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            _lastCoalesce = null;
            Swap(prev);
        }
    }

    public void Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0) return;
            _undo.Add(Ev());
            var next = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
            _lastCoalesce = null;
            Swap(next);
        }
    }

    // ---- edit operations ----

    /// <summary>Ids plus their Down/Up partners, so a key press or click is edited as one action.</summary>
    public HashSet<int> WithPartners(IEnumerable<int> ids)
    {
        lock (_gate)
        {
            var ev = Ev(); var a = AnOf(ev, Desktop); var map = Ids(ev).Map;
            var set = new HashSet<int>();
            foreach (var id in ids)
            {
                if (!map.TryGetValue(id, out int i)) continue;
                set.Add(id);
                if (a.A.Partner[i] >= 0) set.Add(ev[a.A.Partner[i]].Id);
            }
            return set;
        }
    }

    public void Modify(IEnumerable<int> ids, Func<MacroEvent, MacroEvent> f, string? coalesce = null)
    {
        lock (_gate)
        {
            var set = ids as HashSet<int> ?? ids.ToHashSet();
            var next = (MacroEvent[])Ev().Clone();
            bool any = false;
            for (int i = 0; i < next.Length; i++)
                if (set.Contains(next[i].Id))
                {
                    var e = f(next[i]);
                    next[i] = e with { Id = next[i].Id, T = Math.Max(0, e.T) };
                    any = true;
                }
            if (any) Apply(next, coalesce);
        }
    }

    public void Delete(IEnumerable<int> ids)
    {
        lock (_gate)
        {
            var set = WithPartners(ids);
            if (set.Count > 0) Apply(Ev().Where(e => !set.Contains(e.Id)).ToArray());
        }
    }

    /// <summary>Insert events (relative timing kept) starting at atT; ripple pushes later events out of the way. Returns new ids.</summary>
    public List<int> InsertBlock(IReadOnlyList<MacroEvent> block, double atT, bool ripple)
    {
        lock (_gate)
        {
            if (block.Count == 0) return [];
            double min = block.Min(e => e.T), len = block.Max(e => e.T) - min;
            var added = block.Select(e => e with { T = atT + e.T - min, Id = _nextId++ }).ToArray();
            double shift = len + 10;
            // merge the sorted kept events with the (sorted) new ones in one pass; kept wins ties, like the stable sort did
            var add = Sorted(added); var cur = Ev(); var next = new MacroEvent[cur.Length + add.Length];
            int a = 0, b = 0;
            for (int k = 0; k < next.Length; k++)
            {
                var e = a < cur.Length ? cur[a] : default;
                if (ripple && e.T >= atT) e = e with { T = e.T + shift };
                if (a < cur.Length && (b >= add.Length || e.T <= add[b].T)) { next[k] = e; a++; } else next[k] = add[b++];
            }
            Apply(next);
            var ids = added.Select(e => e.Id).ToList();
            Select(ids, ids[0]);
            return ids;
        }
    }

    /// <summary>Replace events (plus their Down/Up partners) with new ones. One undo step; the new events are selected.</summary>
    public List<int> ReplaceWith(IEnumerable<int> removeIds, IReadOnlyList<MacroEvent> add)
    {
        lock (_gate)
        {
            var gone = WithPartners(removeIds);
            var added = add.Select(e => e with { Id = _nextId++ }).ToArray();
            Apply(Ev().Where(e => !gone.Contains(e.Id)).Concat(added).ToArray());
            var ids = added.Select(e => e.Id).ToList();
            if (ids.Count > 0) Select(ids, ids[0]);
            return ids;
        }
    }

    public List<int> Duplicate(IEnumerable<int> ids)
    {
        lock (_gate)
        {
            var set = WithPartners(ids);
            var block = Ev().Where(e => set.Contains(e.Id)).ToList();
            return block.Count == 0 ? [] : InsertBlock(block, block.Max(e => e.T) + 10, ripple: true);
        }
    }

    public string CopyText(IEnumerable<int> ids)
    {
        lock (_gate)
        {
            var set = WithPartners(ids);
            return MacroSerializer.SerializeEvents(Ev().Where(e => set.Contains(e.Id)));
        }
    }

    public List<int> PasteText(string text, double atT)
    {
        List<MacroEvent> block;
        try { block = MacroSerializer.DeserializeEvents(text); }
        catch { return []; } // not our clipboard payload
        return InsertBlock(block, atT, ripple: true);
    }

    /// <summary>Set the gap before an event. Ripple moves every later event too, so the rest of the macro keeps its spacing.</summary>
    public void SetDelay(int id, double delayMs, bool ripple)
    {
        lock (_gate)
        {
            var cur = Ev();
            int idx = IndexOfId(id);
            if (idx < 0) return;
            double prevT = idx > 0 ? cur[idx - 1].T : 0;
            double shift = prevT + Math.Max(0, delayMs) - cur[idx].T;
            var next = (MacroEvent[])cur.Clone();
            for (int j = idx; j < next.Length && (ripple || j == idx); j++) next[j] = next[j] with { T = next[j].T + shift };
            Apply(next);
        }
    }

    public void ShiftTime(IEnumerable<int> ids, double dt, string? coalesce = null) =>
        Modify(ids, e => e with { T = e.T + dt }, coalesce);

    /// <summary>Swap an event with its neighbour; the timing slots stay put, so only the order changes.</summary>
    public void Move(int id, int direction)
    {
        lock (_gate)
        {
            var cur = Ev();
            int i = IndexOfId(id), j = i + direction;
            if (i < 0 || j < 0 || j >= cur.Length) return;
            var next = (MacroEvent[])cur.Clone();
            next[i] = cur[j] with { T = cur[i].T };
            next[j] = cur[i] with { T = cur[j].T };
            Apply(next);
        }
    }

    /// <summary>Remove idle time before the first event.</summary>
    public void TrimLeadingIdle()
    {
        lock (_gate)
        {
            var cur = Ev();
            if (cur.Length == 0 || cur[0].T == 0) return;
            double d = cur[0].T;
            Apply(cur.Select(e => e with { T = e.T - d }).ToArray());
        }
    }

    public int NewId() { lock (_gate) return _nextId++; }
}
