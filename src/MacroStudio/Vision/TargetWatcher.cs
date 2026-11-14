using System.Collections.Concurrent;
using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Native;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>Latest result for one target. <see cref="Hit"/> is in virtual-desktop pixels.</summary>
/// <param name="Seq">Increases every time the watcher confirms the target (even if it did not move): two different values = two separate observations.</param>
/// <param name="LostUtc">When the target was last seen disappearing, null while it is visible.</param>
/// <param name="Ambiguous">Another match looked about as good and nothing told them apart: the reported one may be the wrong button.</param>
public sealed record WatchState(string Id, Detection? Hit, DateTime SeenUtc, string Note, long Seq = 0, DateTime? LostUtc = null, bool Ambiguous = false);

/// <summary>One entry of a target's position history. Lost = it disappeared at this time.</summary>
public readonly record struct HistoryItem(DateTime Utc, Rect Rect, double Score, string Method, bool Lost);

public sealed class WatchStats
{
    public double WorkMs, DelayMs;
    public int Tracked, Searching;
    public long Captures, Samples, Pixels, LightScans, DeepScans, Unchanged;
    public bool Boosted;

    /// <summary>Share of one core the watcher thread is busy, estimated from the last tick and the delay chosen after it.</summary>
    public double Duty => WorkMs + DelayMs <= 0 ? 0 : WorkMs / (WorkMs + DelayMs);
}

/// <summary>
/// Watches many targets for the price of a few. One shared frame per tick; targets already found are re-confirmed in a small
/// window (and skipped when those pixels did not change); only targets that are not found are searched, and only on monitors
/// whose pixels changed; expensive stages (scale sweep, part voting, features) are spread out one per tick; and the thread
/// sleeps so its CPU share stays under a cap (lower when idle, higher while a macro is waiting for a target).
/// </summary>
public sealed class TargetWatcher : IDisposable
{
    const double Duty = 0.12, BoostDuty = 0.6;     // CPU share caps: idle watching vs. a macro waiting for a target
    const double Base = 100, BoostBase = 15;       // fastest tick spacing, ms (10 Hz normally, 66 Hz while a macro waits)
    const double MaxIdle = 1000;                   // slowest spacing when nothing changes, ms
    const double DeepGap0 = 3000;                  // starting (and floor) spacing of expensive stages, ms; grows while a target stays absent

    sealed class Rt
    {
        public Target T = null!;
        public Detection? Last;
        public DateTime Seen, Prioritised, LastDeep;
        public long LastScan;
        public Mat? Crop;
        public long[] LightSeen = [];      // monitor version at this target's last light scan
        public long[,] DeepSeen = new long[0, 0];
        public int DeepCursor;
        public double DeepGapMs = DeepGap0; // minimum spacing of expensive steps; grows while the target stays absent
        public WatchState? Pub;             // last state handed to Get(): republish only when something it carries changed
        public List<double>? Scales;        // cached ExtraScales: rebuilt when the last good scale or the target's DPI changes
        public bool Dead;                   // released between the tick's snapshot and its work: skip it entirely
        public double LightGapMs;           // same idea for the cheap whole-frame search
        public long Seq;
        public DateTime? LostAt;
        public readonly Queue<HistoryItem> History = new();   // bounded: the last 32 moves / losses
        public bool Ambiguous;              // see WatchState.Ambiguous
        public (double, double, bool, double, double) Sig;   // the settings that affect what counts as a match, to notice edits
        public bool EverSeen;               // found at least once, so a later absence is a loss, not "never there"
        public double LastScale = 1;        // scale of the last good match: tried first next time
        public bool LightDone;              // the cheap search ran and failed: only then are the expensive stages worth trying
        public DateTime NextLight;
        public string Note = "searching";
        public IEnumerator<List<Detection>?>? Job;   // the expensive stage in progress: it runs a slice per tick on its own copy of the screen
        public FrameContext? JobCtx;
        public long JobVer;                          // all monitor versions summed when the job was created: any change makes it stale
        public bool JobMayBeStale;                   // already restarted once for new content: let this job finish anyway
        public double SliceMs = 60;                  // recent cost of the slowest slice: a tick only starts another when it still fits
        public bool Deferred;                        // the last tick had no room for a slice: this one runs it regardless
        public bool DropPending;                     // someone on another thread reset the search: the worker drops the job on its next step
        public string TextSig = "";                  // D8: the target text last scanned for — editing it re-arms the OCR search
    }

    readonly TargetStore _store;
    readonly Func<ScreenRect, Mat> _capture;
    readonly Func<ScreenRect, int, Mat> _sample;
    readonly DesktopInfo _desk;
    readonly int _dpi;
    readonly Detector _det = new();
    readonly object _lock = new();
    readonly Dictionary<string, Rt> _rt = new();
    readonly Dictionary<string, int> _demand = new();
    readonly ConcurrentDictionary<string, WatchState> _state = new();
    readonly long[] _monVersion;
    readonly Mat?[] _monSig;
    readonly Mat?[] _monDiff, _monBin;           // per-monitor scratch for the change test, reused every tick
    readonly AutoResetEvent _wake = new(false);
    readonly bool _threaded;
    readonly Func<DateTime> _now;
    readonly IReadOnlyList<int> _monitorDpis;
    readonly List<Rt> _trash = new();              // released targets: their buffers belong to the worker thread, freed at the next tick
    Thread? _thread;
    volatile bool _stop;
    int _tornDown;                               // buffers are freed once, by whichever side gets there (worker exit or Dispose after join)
    long _boostUntilTicks;                       // written by whoever calls Prioritise, read by the worker: atomic ticks
    DateTime _lastRaise;
    double _idleMs = Base;
    readonly List<Rt> _searching = new();        // scratch for the per-tick ordering of targets still being searched

    public WatchStats Stats { get; } = new();

    /// <summary>Raised on the watcher thread when a target was found, moved or lost (and about twice a second otherwise, for the stats).</summary>
    public event Action? Updated;

    /// <param name="capture">Screen grab for a desktop rectangle (default: GDI). Injectable for tests.</param>
    /// <param name="threaded">False in tests: the caller drives <see cref="Tick"/> itself.</param>
    public TargetWatcher(TargetStore store, Func<ScreenRect, Mat>? capture = null, DesktopInfo? desktop = null, int? dpi = null, bool threaded = true, Func<DateTime>? clock = null, IReadOnlyList<int>? monitorDpis = null)
    {
        _store = store;
        _capture = capture ?? (r => ScreenCapture.Capture(r));
        // real screen: GDI scales while copying (cheap); injected capture (tests): derive the sample from it
        _sample = capture == null ? ScreenCapture.CaptureSample : (r, d) => { using var full = _capture(r); var s = new Mat(); Cv2.Resize(full, s, new Size(Math.Max(1, full.Width / d), Math.Max(1, full.Height / d)), 0, 0, InterpolationFlags.Nearest); return s; };
        _desk = desktop ?? Screens.Current();
        _dpi = dpi ?? Screens.CurrentEnv().Dpi;
        _threaded = threaded;
        _now = clock ?? (() => DateTime.UtcNow);
        _monitorDpis = monitorDpis ?? (capture == null ? Screens.MonitorDpis() : []);
        _monVersion = new long[_desk.Monitors.Count];
        _monSig = new Mat?[_desk.Monitors.Count];
        _monDiff = new Mat?[_desk.Monitors.Count];
        _monBin = new Mat?[_desk.Monitors.Count];
    }

    // ---------- demand ----------

    /// <summary>Start watching these targets until the returned handle is disposed (reference counted, so several users can share).</summary>
    public IDisposable Watch(IEnumerable<Target> targets)
    {
        var list = targets.ToList();
        lock (_lock)
            foreach (var t in list)
            {
                _demand[t.Id] = _demand.GetValueOrDefault(t.Id) + 1;
                if (!_rt.TryGetValue(t.Id, out var r))
                {
                    int m = _desk.Monitors.Count;
                    _rt[t.Id] = new Rt { T = t, Sig = Sig(t), LightSeen = Enumerable.Repeat(-1L, m).ToArray(), DeepSeen = Fill(m, Detector.DeepStages) };
                }
                else
                {
                    var sig = Sig(t);
                    if (sig != r.Sig) { r.Sig = sig; ResetSearch(r); } // settings edited: what was searched under the old ones proves nothing
                    r.T = t;
                }
            }
        if (_threaded) EnsureThread();
        Wake();
        return new Handle(this, list.Select(t => t.Id).ToList());
    }

    /// <summary>What affects matching, including the scale the capture DPI implies: a change proves old searches nothing.</summary>
    static (double, double, bool, double, double) Sig(Target t) => (t.Threshold, t.ColorTolerance, t.Occlusion, t.MinVisible, t.CaptureDpi);

    /// <summary>Forget what was already searched, so the next tick searches again under the current settings.</summary>
    static void ResetSearch(Rt r)
    {
        Array.Fill(r.LightSeen, -1L);
        for (int m = 0; m < r.DeepSeen.GetLength(0); m++) for (int s = 0; s < r.DeepSeen.GetLength(1); s++) r.DeepSeen[m, s] = -1;
        r.LightGapMs = 0; r.NextLight = DateTime.MinValue; r.LightDone = false; r.DeepGapMs = DeepGap0; r.LastDeep = DateTime.MinValue;
        r.Scales = null;
        r.DropPending = true;                    // the worker may be inside a slice of this job right now: it drops it itself
    }

    /// <summary>Free a target's buffers. Only ever called on the worker thread (or after it has stopped).</summary>
    static void FreeRt(Rt r) { r.Crop?.Dispose(); r.Crop = null; DropJob(r); }

    static void DropJob(Rt r) { r.Job?.Dispose(); r.JobCtx?.Dispose(); r.Job = null; r.JobCtx = null; r.JobMayBeStale = false; }

    static long[,] Fill(int a, int b)
    {
        var x = new long[a, b];
        for (int i = 0; i < a; i++) for (int j = 0; j < b; j++) x[i, j] = -1;
        return x;
    }

    sealed class Handle(TargetWatcher w, List<string> ids) : IDisposable
    {
        int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) w.Release(ids); }
    }

    void Release(List<string> ids)
    {
        lock (_lock)
            foreach (var id in ids)
                if (_demand.TryGetValue(id, out var n) && --n <= 0)
                {
                    _demand.Remove(id);
                    _expect.TryRemove(id, out _);                  // a recorded position belongs to a watch session, not to the target
                    // Dead marks it for the tick already in flight; its buffers are freed by the worker, which may be inside
                    // a slice of that very job — disposing them here would pull the Mat from under the detector.
                    if (_rt.Remove(id, out var r)) { r.Dead = true; _trash.Add(r); }
                    _state.TryRemove(id, out _);
                }
                else if (n > 0) _demand[id] = n;
        Wake();                                                  // the next tick frees the released targets' buffers
    }

    /// <summary>Among matches that all pass the threshold, one this much worse than the best still wins when it is nearer to where the step was recorded (a copy of the button on another monitor must not win by a few points).</summary>
    const double NearBand = 0.15;

    readonly ConcurrentDictionary<string, (int X, int Y)> _expect = new();

    /// <summary>
    /// The step about to click this target was recorded at (x, y). When several look-alikes are on screen the one nearest that spot wins,
    /// and a tracked match far from it is dropped so the whole screen is searched again instead of clicking the previous step's button.
    /// </summary>
    public void Expect(string id, int x, int y)
    {
        _expect[id] = (x, y);
        lock (_lock)
        {
            if (!_rt.TryGetValue(id, out var r) || r.Last is not { } l) return;
            if (Math.Sqrt(Math.Pow(l.Center.X - x, 2) + Math.Pow(l.Center.Y - y, 2)) <= Math.Max(150, 3.0 * Math.Max(l.Rect.Width, l.Rect.Height))) return;
            // The old crop is abandoned, not disposed: the worker may be copying into it this instant (GC's finalizer frees it).
            r.Last = null; Interlocked.Exchange(ref r.Crop, null); r.LastScan = 0; r.LightGapMs = 0; r.NextLight = DateTime.MinValue; r.LightDone = false;
            Array.Fill(r.LightSeen, -1L);
            _state.TryRemove(id, out _);                      // the macro must not read the old, far match while the search runs
        }
        Wake();
    }

    /// <summary>A macro is waiting on this target: scan it first and run faster for a moment.</summary>
    public void Prioritise(string id)
    {
        var now = _now();
        lock (_lock) if (_rt.TryGetValue(id, out var r)) r.Prioritised = now;
        Interlocked.Exchange(ref _boostUntilTicks, now.AddMilliseconds(1500).Ticks);
        Stats.Boosted = true;                    // effective at once: the next tick must not wait for a stale read
        Wake();
    }

    /// <summary>Nudge the watcher thread awake; a disposed watcher simply stays asleep.</summary>
    void Wake() { try { _wake.Set(); } catch (ObjectDisposedException) { } }

    public WatchState? Get(string id) => _state.TryGetValue(id, out var s) ? s : null;

    /// <summary>The last positions (and disappearances) of a target, oldest first. Bounded to 32 entries per target.</summary>
    public IReadOnlyList<HistoryItem> History(string id)
    {
        lock (_lock)
        {
            if (!_rt.TryGetValue(id, out var r)) return [];
            lock (r.History) return r.History.ToArray();   // the watcher thread appends under the same lock
        }
    }

    // ---------- loop ----------

    void EnsureThread()
    {
        if (_thread != null) return;
        lock (_lock)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TargetWatcher", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    void Loop()
    {
        try
        {
            while (!_stop)
            {
                bool any; lock (_lock) any = _rt.Count > 0 || _trash.Count > 0;
                if (!any) { try { _wake.WaitOne(); } catch (ObjectDisposedException) { return; } continue; }
                double delay;
                try { delay = Tick(); }
                catch { delay = 500; } // a capture hiccup (secure desktop, display change) must not kill the watcher
                try { _wake.WaitOne((int)Math.Max(1, delay)); }
                catch (ObjectDisposedException) { return; } // Dispose gave up waiting for this thread under load
            }
        }
        finally { Teardown(); }   // the buffers this thread was using are its own to free
    }

    /// <summary>One scheduling step. Returns how long to sleep before the next one, ms.</summary>
    public double Tick()
    {
        int prev = -1;
        try { prev = Cv2.GetNumThreads(); } catch { }
        try { return TickCore(); }
        finally { if (prev >= 0) SetThreads(prev); }   // the thread cap is for this tick's work only: the rest of the process keeps its own count
    }

    double TickCore()
    {
        var sw = Stopwatch.StartNew();
        List<Rt> rts; List<Rt>? trash = null;
        lock (_lock) { rts = _rt.Values.ToList(); if (_trash.Count > 0) { trash = new List<Rt>(_trash); _trash.Clear(); } }
        if (trash != null) foreach (var r in trash) FreeRt(r);   // released since the last tick: free on this thread, safe from slices
        if (rts.Count == 0) return 250;

        var now = _now();
        foreach (var rt in rts) try { Prepared.Of(_store.Image(rt.T.Id)).ColorTolerance = rt.T.ColorTolerance; } catch { /* image missing: reported by the step itself */ }
        bool boosted = now.Ticks < Interlocked.Read(ref _boostUntilTicks);
        bool urgent = rts.Any(r => r.EverSeen && r.Last == null && r.LostAt is { } la && (now - la).TotalMilliseconds < 3000); // it was here a moment ago: find it again quickly
        bool fast = boosted || urgent;
        Stats.Boosted = boosted;
        // Burst speed only while a macro is waiting; one thread otherwise: OpenCV's worker pool spin-waits after every parallel
        // call, which costs more CPU than it saves on these small windows. The previous count is restored when the tick ends.
        SetThreads(boosted ? Math.Clamp(Environment.ProcessorCount / 2, 2, 4) : 1);
        bool changed = false, worked = false;
        using var frames = new Frames(this);

        // 1. targets that were found: confirm in a small window (usually well under a millisecond)
        foreach (var r in rts)
        {
            if (r.Last == null || r.Dead) continue;
            changed |= Guard(r, () => VerifyTracked(r, frames, ref worked));
        }

        // 2. targets that are not found: cheap whole-frame search, most-wanted first, within a time budget
        _searching.Clear();
        foreach (var r in rts) if (r.Last == null && !r.Dead) _searching.Add(r);
        _searching.Sort(CompareScan);
        double budget = fast ? 120 : 45;
        int scanned = 0;
        foreach (var r in _searching)
        {
            if (scanned > 0 && sw.Elapsed.TotalMilliseconds > budget) break;
            bool wanted = boosted && (now - r.Prioritised).TotalMilliseconds < 1500;     // a macro is waiting on this one: no waiting
            if (!wanted && now < r.NextLight) continue;
            bool ran = false;
            changed |= Guard(r, () => ScanLight(r, frames, ref ran));
            if (!ran) continue;
            scanned++; worked = true;
            if (r.Last == null) { r.LightDone = true; r.LightGapMs = Math.Min(Math.Max(100, r.LightGapMs * 1.5), 2000); r.NextLight = now.AddMilliseconds(r.LightGapMs); r.DeepGapMs = Math.Min(r.DeepGapMs, DeepGap0); } // searched in vain: look less and less often; pixels the cheap search cannot explain deserve the expensive stages at the starting cadence, not the grown one
        }

        // 3. one expensive step (scale sweep / part voting / features) per tick, for the target that waited longest.
        //    No time budget here on purpose: on a screen that keeps changing the cheap search alone would use it up and these stages
        //    would never run. The CPU cap below stretches the delay after an expensive tick instead.
        _searching.Sort(CompareDeep);
        foreach (var r in _searching)
        {
            if (r.Last != null || !r.LightDone || r.Dead) continue;
            if (r.Job == null && (now - r.LastDeep).TotalMilliseconds < (fast ? 300 : r.DeepGapMs)) continue;   // a stage already under way just carries on
            if (sw.Elapsed.TotalMilliseconds + r.SliceMs > TickBudgetMs && !r.Deferred) { r.Deferred = true; break; }   // the cheap work already used this tick: next one
            bool ran = false;
            changed |= Guard(r, () => DeepStep(r, frames, sw, ref ran));
            if (ran) { worked = true; break; }
        }

        Publish(rts);
        double work = sw.Elapsed.TotalMilliseconds;
        double baseMs = fast ? BoostBase : Base;
        _idleMs = fast || worked || changed ? baseMs : Math.Min(Math.Max(_idleMs, baseMs) * 1.5, MaxIdle); // nothing new and nobody waiting: back off (never while a macro waits)
        double duty = fast ? BoostDuty : Duty;
        double delay = Math.Max(_idleMs, work * (1 / duty - 1));
        Stats.WorkMs = work; Stats.DelayMs = delay;
        if (changed || (now - _lastRaise).TotalMilliseconds > 500) { _lastRaise = now; if (!_stop) Updated?.Invoke(); }
        return delay;
    }

    /// <summary>Searching order: the target a macro is waiting for first, then the one not scanned for longest.</summary>
    static int CompareScan(Rt a, Rt b) { int c = b.Prioritised.CompareTo(a.Prioritised); return c != 0 ? c : a.LastScan.CompareTo(b.LastScan); }

    /// <summary>Deep-stage order: the target a macro is waiting for first, then the one whose last expensive stage is oldest.</summary>
    static int CompareDeep(Rt a, Rt b) { int c = b.Prioritised.CompareTo(a.Prioritised); return c != 0 ? c : a.LastDeep.CompareTo(b.LastDeep); }

    int _threads = -1;
    void SetThreads(int n)
    {
        if (n == _threads) return;
        _threads = n;
        try { Cv2.SetNumThreads(n); } catch { }
    }

    bool Guard(Rt r, Func<bool> step)
    {
        try { return step(); }
        catch (Exception ex) { r.Note = "error: " + ex.Message; return false; } // e.g. the target image was deleted
    }

    // ---------- steps ----------

    double BaseScale(Rt r) => (double)_dpi / Math.Max(1, r.T.CaptureDpi);

    /// <summary>Sizes worth trying early: where it was last matched, and what each monitor's own scaling would make it.</summary>
    List<double> ExtraScales(Rt r) => r.Scales ??= BuildScales(r);

    List<double> BuildScales(Rt r)
    {
        var l = new List<double>();
        if (Math.Abs(r.LastScale - 1) > 0.02) l.Add(Math.Round(r.LastScale, 2));
        foreach (var d in _monitorDpis) { var s = Math.Round((double)d / Math.Max(1, r.T.CaptureDpi), 2); if (!l.Contains(s)) l.Add(s); }
        return l;
    }

    bool VerifyTracked(Rt r, Frames frames, ref bool worked)
    {
        if (!string.IsNullOrWhiteSpace(r.T.Text)) return VerifyTrackedText(r, frames, ref worked);   // D8: text target — confirm by reading, not by pixels
        if (r.Last is not { } last) return false;   // Expect() may have dropped it between the tick's snapshot and now
        var img = _store.Image(r.T.Id);
        int pad = last.Method == "template" ? 16 : Math.Max(last.Rect.Width, last.Rect.Height) / 2 + 28;
        var want = Inflate(last.Rect, pad);
        var ctx = frames.Region(want);

        // identical pixels as last time: the answer cannot have changed
        var local = new Rect(want.X - ctx.Origin.X, want.Y - ctx.Origin.Y, want.Width, want.Height).Intersect(new Rect(0, 0, ctx.Width, ctx.Height));
        using (var crop = new Mat(ctx.Bgr, local))
        {
            var c = r.Crop;                               // Expect() can null the field on another thread: read it once
            if (c != null && c.Size() == crop.Size())
            {
                if (Cv2.Norm(c, crop, NormTypes.L1) == 0) { r.Seen = _now(); r.Seq++; Stats.Unchanged++; return false; }
                crop.CopyTo(c);                           // changed: keep the buffer, it is already the right size
            }
            else { c?.Dispose(); r.Crop = crop.Clone(); }  // c is ours even if Expect() already nulled the field
        }
        worked = true;

        var v = _det.Verify(ctx, img, last.Shift(-ctx.Origin.X, -ctx.Origin.Y), r.T.Threshold, r.T.MinVisible, r.T.Occlusion);
        if (v is { } ok) return SetHit(r, ok.Shift(ctx.Origin.X, ctx.Origin.Y), r.Ambiguous);

        // it moved a little: look in a window around the last position
        var ctx2 = frames.Region(Inflate(last.Rect, 200));
        var found = _det.Light(ctx2, img, r.T.Threshold, BaseScale(r), null, out _, ExtraScales(r));
        // Only a match as good as the last one is trusted here. A weaker one nearby may be a look-alike the target was dragged past:
        // fall through to the full search, which picks the best match on the whole screen.
        if (found.Count > 0 && found[0].Score >= last.Score - 0.04) return SetHit(r, found[0].Shift(ctx2.Origin.X, ctx2.Origin.Y), found.Count > 1 && found[1].Score >= found[0].Score - 0.02);

        // lost: back to searching, immediately
        MarkLost(r); r.Crop?.Dispose(); r.Crop = null; r.LastScan = 0; r.LightGapMs = 0; r.NextLight = DateTime.MinValue; r.LightDone = false;
        Array.Fill(r.LightSeen, -1L);
        for (int m = 0; m < r.DeepSeen.GetLength(0); m++) for (int s = 0; s < r.DeepSeen.GetLength(1); s++) r.DeepSeen[m, s] = -1;
        return true;
    }

    bool ScanLight(Rt r, Frames frames, ref bool ran)
    {
        if (!string.IsNullOrWhiteSpace(r.T.Text)) return ScanText(r, frames, ref ran);   // D8: text target — search by reading, not by pixels
        var img = _store.Image(r.T.Id);
        r.LastScan = _now().Ticks;
        var perMonitor = new List<(FrameContext Ctx, List<Detection> Hits)>();
        for (int m = 0; m < _desk.Monitors.Count; m++)
        {
            long ver = frames.Version(m);                                        // cheap sample
            if (r.LightSeen[m] == ver) { Stats.Unchanged++; continue; }           // looks the same as when it was last searched: no full capture
            var ctx = frames.Monitor(m);
            r.LightSeen[m] = ver;
            ran = true; Stats.LightScans++;
            var hits = _det.Light(ctx, img, r.T.Threshold, BaseScale(r), null, out _, ExtraScales(r));
            if (hits.Count > 0) perMonitor.Add((ctx, hits));
        }
        if (perMonitor.Count == 0) return false;

        // Candidates from every monitor together: a look-alike on one screen must not beat the real thing on another.
        // With several, the saved surroundings decide (when there are any); otherwise the best score does.
        bool useContext = perMonitor.Sum(p => p.Hits.Count) > 1 && r.T.ContextPad > 0 && _store.Context(r.T.Id) is not null;
        var cands = new List<(Detection D, double Ring)>();
        foreach (var (ctx, hits) in perMonitor)
        {
            var ranked = useContext ? _det.RankWithRing(ctx, hits, _store.Context(r.T.Id)!, r.T.ContextPad) : hits.Select(h => (h, -1.0)).ToList();
            cands.AddRange(ranked.Select(x => (x.Item1.Shift(ctx.Origin.X, ctx.Origin.Y), x.Item2)));
        }
        var order = cands.OrderByDescending(c => Math.Round(c.Ring, 1)).ThenByDescending(c => c.D.Score).ToList();
        var top = order[0];
        bool nearest = false;
        // look-alikes that score the same: the one closest to where this step was recorded (else where the target was captured) is the real one
        bool recorded = _expect.TryGetValue(r.T.Id, out var ex);
        (int X, int Y)? at = recorded ? ex : r.T.CaptureRect.Width > 0 ? (r.T.CaptureRect.X + r.T.CaptureRect.Width / 2, r.T.CaptureRect.Y + r.T.CaptureRect.Height / 2) : null;
        if (at is { } p && order.Count > 1)
        {
            var tied = order.Where(c => c.D.Score >= top.D.Score - NearBand && (!useContext || Math.Round(c.Ring, 1) == Math.Round(top.Ring, 1))).ToList();
            if (tied.Count > 1) { top = tied.MinBy(c => Math.Pow(c.D.Center.X - p.X, 2) + Math.Pow(c.D.Center.Y - p.Y, 2)); nearest = recorded; }
        }
        return SetHit(r, top.D, !nearest && Ambiguous(order, useContext));
    }

    /// <summary>
    /// True when another candidate looks about as good as the chosen one (score within 0.02) and the surroundings did not settle it:
    /// the chosen one may be the wrong button.
    /// </summary>
    static bool Ambiguous(List<(Detection D, double Ring)> ordered, bool contextUsed)
    {
        if (ordered.Count < 2) return false;
        var top = ordered[0];
        foreach (var other in ordered.Skip(1))
        {
            if (other.D.Score < top.D.Score - 0.02) continue;
            if (contextUsed && Math.Round(top.Ring, 1) > Math.Round(other.Ring, 1)) continue; // surroundings clearly favour the chosen one
            return true;
        }
        return false;
    }

    /// <summary>A tick starts another slice of the running expensive stage only while its elapsed time plus that slice's expected cost stays within this.</summary>
    const double TickBudgetMs = 100;

    /// <summary>
    /// Expensive stages run as a series of slices (one scale, one seed patch, one band of the frame) on a private copy of the screen,
    /// a few per tick, so no tick holds up the macro for long. The stage's result is used when its last slice ends.
    /// </summary>
    bool DeepStep(Rt r, Frames frames, Stopwatch sw, ref bool ran)
    {
        if (r.DropPending) { r.DropPending = false; DropJob(r); }   // a caller reset the search: drop the job here, on the worker thread
        // The screen moved on under a running stage: the clone it is scanning is stale (its answer describes pixels that no
        // longer exist), and the fresh content elsewhere may be the target coming back. Restart it once on the new screen —
        // a job that already restarted is allowed to finish, so a screen in constant motion still ends every stage.
        if (r.Job != null && !r.JobMayBeStale && VerSum(frames) != r.JobVer) { DropJob(r); r.JobMayBeStale = true; }
        if (!string.IsNullOrWhiteSpace(r.T.Text)) return false;   // D8: text target — OCR has no expensive stage; the light scan is the whole job
        if (r.Job == null)
        {
            int stages = r.T.Occlusion ? Detector.DeepStages : 1;
            int monitors = _desk.Monitors.Count, total = monitors * stages;
            for (int i = 0; i < total && r.Job == null; i++)
            {
                int idx = (r.DeepCursor + i) % total, m = idx / stages, s = idx % stages;
                long ver = frames.Version(m);
                if (r.DeepSeen[m, s] == ver) continue; // already ran this stage on this exact screen content
                var shot = frames.Monitor(m);
                r.JobCtx = new FrameContext(shot.Bgr.Clone(), shot.Origin, owns: true);
                r.DeepSeen[m, s] = ver;
                r.JobVer = VerSum(frames);
                r.DeepCursor = idx + 1;
                Stats.DeepScans++;
                r.Job = _det.DeepSteps(r.JobCtx, _store.Image(r.T.Id), r.T.Threshold, BaseScale(r), s, r.T.MinVisible).GetEnumerator();
            }
            if (r.Job == null) return false;
        }
        ran = true;
        List<Detection>? res;
        r.Deferred = false;
        do
        {
            double t0 = sw.Elapsed.TotalMilliseconds;
            res = r.Job.MoveNext() ? r.Job.Current : [];
            r.SliceMs = Math.Max(sw.Elapsed.TotalMilliseconds - t0, r.SliceMs * 0.9);
        } while (res == null && sw.Elapsed.TotalMilliseconds + r.SliceMs <= TickBudgetMs);
        if (res == null) return false;                                    // more slices next tick
        var origin = r.JobCtx!.Origin;
        bool movedOn = VerSum(frames) != r.JobVer;                        // the screen changed during (or under) this stage
        DropJob(r); r.LastDeep = _now();
        if (res.Count > 0) return SetHit(r, res[0].Shift(origin.X, origin.Y));
        r.DeepGapMs = movedOn ? Math.Min(r.DeepGapMs, DeepGap0)           // fresh content waits: stay on the starting cadence
                            : Math.Min(r.DeepGapMs * 1.3, 15000);         // still nowhere and nothing new: look less and less often
        return false;
    }

    /// <summary>All monitor change-counters summed — one number that bumps whenever any part of the desktop changed.</summary>
    long VerSum(Frames f)
    {
        long s = 0;
        for (int m = 0; m < _desk.Monitors.Count; m++) s += f.Version(m);
        return s;
    }

    bool SetHit(Rt r, Detection hit, bool ambiguous = false)
    {
        r.DropPending = false;
        DropJob(r);
        bool moved = r.Last is not { } l || l.Rect != hit.Rect || l.Method != hit.Method;
        r.Last = hit; r.Seen = _now(); r.DeepGapMs = DeepGap0; r.LightGapMs = 0; r.NextLight = DateTime.MinValue; r.LightDone = false;
        r.Seq++; r.LostAt = null; r.EverSeen = true; r.LastScale = hit.Scale; r.Ambiguous = ambiguous; r.Scales = null;
        r.Note = hit.Method switch { "template" => $"found {hit.Score:0.00}", "patches" => $"partly hidden, {hit.Visible:P0} visible", "shape" => $"found by shape {hit.Score:0.00} (colour ignored)", "text" => $"found text ({hit.Score:0.00})", _ => "found by features" };
        if (moved) AddHistory(r, new HistoryItem(r.Seen, hit.Rect, hit.Score, hit.Method, false));
        return moved;
    }

    /// <summary>Forget the position, remember that (and when) the target disappeared, and go back to searching.</summary>
    void MarkLost(Rt r)
    {
        var last = r.Last;
        r.Last = null; r.Note = "searching"; r.LostAt = _now();
        if (last is { } l) AddHistory(r, new HistoryItem(r.LostAt.Value, l.Rect, 0, l.Method, true));
    }

    static void AddHistory(Rt r, HistoryItem h)
    {
        lock (r.History) { r.History.Enqueue(h); while (r.History.Count > 32) r.History.Dequeue(); }
    }

    void Publish(List<Rt> rts)
    {
        int tracked = 0;
        foreach (var r in rts)
        {
            if (r.Dead) continue;                    // released between the snapshot and now: its state must not come back
            if (r.Last != null) tracked++;
            var p = r.Pub;
            if (p != null && p.Seq == r.Seq && p.Hit == r.Last && p.SeenUtc == r.Seen && p.LostUtc == r.LostAt && p.Ambiguous == r.Ambiguous && p.Note == r.Note)
                continue;                            // identical to what readers already see: no new record, no dictionary write
            _state[r.T.Id] = r.Pub = new WatchState(r.T.Id, r.Last, r.Seen, r.Note, r.Seq, r.LostAt, r.Ambiguous);
        }
        Stats.Tracked = tracked; Stats.Searching = rts.Count - tracked;
    }

    static Rect Inflate(Rect r, int pad) => new(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad);

    // ---------- text targets (D8: additive section; a Target with Text set is located by OCR, not pixels) ----------
    // A text target is matched by reading the screen through TextMatch (Windows OCR via TextRecognizer) instead of the
    // pixel template. The same monitor versioning the pixel search uses gates OCR, so a static screen costs no OCR at all,
    // and one pass is shared by every text target in a tick (TextMatch caches words on the captured Mat).
    // Semantic when OCR is off (no engine, or MACRO_NO_OCR): searching reports not-found with note "text recognition
    // unavailable" and retries on the normal idle cadence — no hang, no spin, no pixel fallback (the saved pixels are the
    // old label's look, which is exactly what the text mode is meant to survive losing).

    /// <summary>Whole-monitor OCR search: the text-target counterpart of <see cref="ScanLight"/>.</summary>
    bool ScanText(Rt r, Frames frames, ref bool ran)
    {
        if (r.TextSig != r.T.Text) { r.TextSig = r.T.Text; Array.Fill(r.LightSeen, -1L); r.LightDone = false; r.LightGapMs = 0; r.NextLight = DateTime.MinValue; }   // the text was edited: everything previously read proves nothing
        r.LastScan = _now().Ticks;
        if (!TextRecognizer.Available) { r.Note = "text recognition unavailable"; return false; }   // not counted as a scan: the idle backoff paces the retry
        for (int m = 0; m < _desk.Monitors.Count; m++)
        {
            long ver = frames.Version(m);
            if (r.LightSeen[m] == ver) { Stats.Unchanged++; continue; }   // same screen as last time: the words cannot have changed
            var ctx = frames.Monitor(m);
            r.LightSeen[m] = ver;
            ran = true; Stats.LightScans++;
            var hits = TextMatch.Find(ctx, r.T.Text, r.T.Threshold);
            if (hits.Count == 0) continue;
            var top = hits[0];
            bool nearest = false;
            // the label shows up more than once: like the pixel path, the run nearest the recorded point (else where it was captured) wins
            bool recorded = _expect.TryGetValue(r.T.Id, out var ex);
            (int X, int Y)? at = recorded ? ex : r.T.CaptureRect.Width > 0 ? (r.T.CaptureRect.X + r.T.CaptureRect.Width / 2, r.T.CaptureRect.Y + r.T.CaptureRect.Height / 2) : null;
            if (at is { } p && hits.Count > 1)
            {
                var tied = hits.Where(h => h.Score >= top.Score - NearBand).ToList();
                if (tied.Count > 1) { top = tied.MinBy(h => Math.Pow(h.Center.X + ctx.Origin.X - p.X, 2) + Math.Pow(h.Center.Y + ctx.Origin.Y - p.Y, 2)); nearest = recorded; }
            }
            return SetHit(r, top.Shift(ctx.Origin.X, ctx.Origin.Y), !nearest && hits.Any(h => !h.Equals(top) && h.Score >= top.Score - 0.02));
        }
        return false;
    }

    /// <summary>Re-confirm a tracked text target: identical pixels mean identical words (OCR skipped); else the window around the last hit is read again.</summary>
    bool VerifyTrackedText(Rt r, Frames frames, ref bool worked)
    {
        var last = r.Last!.Value;
        if (!TextRecognizer.Available) { r.Note = "text recognition unavailable (keeping last position)"; return false; }   // cannot confirm: keep the last hit rather than lose a good target to an engine hiccup
        int pad = Math.Max(last.Rect.Width, last.Rect.Height) / 2 + 40;
        var want = Inflate(last.Rect, pad);
        var ctx = frames.Region(want);
        var local = new Rect(want.X - ctx.Origin.X, want.Y - ctx.Origin.Y, want.Width, want.Height).Intersect(new Rect(0, 0, ctx.Width, ctx.Height));
        using (var crop = new Mat(ctx.Bgr, local))
        {
            if (r.Crop != null && r.Crop.Size() == crop.Size() && Cv2.Norm(r.Crop, crop, NormTypes.L1) == 0) { r.Seen = _now(); r.Seq++; Stats.Unchanged++; return false; }
            r.Crop?.Dispose(); r.Crop = crop.Clone();
        }
        worked = true;
        var hits = TextMatch.Find(ctx, r.T.Text, r.T.Threshold);
        if (hits.Count > 0)
        {
            // the run nearest the last position is this target; a second run that reads about the same means the label is on screen twice
            var top = hits.MinBy(h => Math.Pow(h.Center.X + ctx.Origin.X - last.Center.X, 2) + Math.Pow(h.Center.Y + ctx.Origin.Y - last.Center.Y, 2));
            return SetHit(r, top.Shift(ctx.Origin.X, ctx.Origin.Y), hits.Any(h => !h.Equals(top) && h.Score >= top.Score - 0.02));
        }
        // gone from its window: back to searching at once — the monitor scan below re-finds it if it merely moved out of the window
        MarkLost(r); r.Crop?.Dispose(); r.Crop = null; r.LastScan = 0; r.LightGapMs = 0; r.NextLight = DateTime.MinValue; r.LightDone = false;
        Array.Fill(r.LightSeen, -1L);
        for (int m = 0; m < r.DeepSeen.GetLength(0); m++) for (int s = 0; s < r.DeepSeen.GetLength(1); s++) r.DeepSeen[m, s] = -1;
        return true;
    }

    // ---------- shared frames ----------

    /// <summary>Captures made during one tick. Every target in the tick reads the same frames; nothing is grabbed twice.</summary>
    sealed class Frames(TargetWatcher w) : IDisposable
    {
        readonly List<FrameContext> _all = new();
        readonly FrameContext?[] _mon = new FrameContext?[w._desk.Monitors.Count];

        readonly bool[] _sampled = new bool[w._desk.Monitors.Count];

        /// <summary>Change counter of a monitor, from a cheap 1/8 sample taken at most once per tick.</summary>
        public long Version(int i)
        {
            if (!_sampled[i])
            {
                _sampled[i] = true;
                using var s = w._sample(w._desk.Monitors[i], 8);
                w.Stats.Samples++;
                w.UpdateVersion(i, s);
            }
            return w._monVersion[i];
        }

        /// <summary>The full monitor image: only captured when a search actually needs it.</summary>
        public FrameContext Monitor(int i)
        {
            if (_mon[i] is { } c) return c;
            c = Grab(w._desk.Monitors[i]);
            _mon[i] = c;
            return c;
        }

        /// <summary>A frame covering this desktop rectangle: an already captured one if possible, else just that rectangle.</summary>
        public FrameContext Region(Rect want)
        {
            var v = w._desk.Virtual;
            var r = want.Intersect(new Rect(v.X, v.Y, v.Width, v.Height));
            foreach (var c in _all)
                if (r.X >= c.Origin.X && r.Y >= c.Origin.Y && r.Right <= c.Origin.X + c.Width && r.Bottom <= c.Origin.Y + c.Height) return c;
            return Grab(new ScreenRect(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
        }

        FrameContext Grab(ScreenRect r)
        {
            var m = w._capture(r);
            w.Stats.Captures++; w.Stats.Pixels += (long)r.Width * r.Height;
            var ctx = new FrameContext(m, new Point(r.X, r.Y), owns: true);
            _all.Add(ctx);
            return ctx;
        }

        public void Dispose() { foreach (var c in _all) c.Dispose(); }
    }

    /// <summary>
    /// Cheap "did this monitor change?" test: the 1/8 sample compared with the last one that counted as changed.
    /// A new button covers several samples, so it is noticed; a static screen costs one small sample and ~0.3 ms of matching.
    /// The diff is per channel, so a change that only alters one (a blue icon on a blue-grey desktop) still counts.
    /// </summary>
    void UpdateVersion(int i, Mat small)
    {
        var old = _monSig[i];
        bool changed = old == null || old.Size() != small.Size();
        if (!changed)
        {
            var diff = _monDiff[i] ??= new Mat(); Cv2.Absdiff(small, old!, diff);
            var bin = _monBin[i] ??= new Mat();
            using var flat = diff.Reshape(1);                     // channels side by side, one byte per channel value
            Cv2.Threshold(flat, bin, 24, 255, ThresholdTypes.Binary);
            changed = Cv2.CountNonZero(bin) >= 2;
        }
        if (changed)
        {
            _monVersion[i]++;
            if (_monSig[i] is { } sig) small.CopyTo(sig); else _monSig[i] = small.Clone();
        }
    }

    /// <summary>Free every buffer the worker owns. Runs on the worker thread when its loop ends, or here once the worker has provably stopped — never both.</summary>
    void Teardown()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0) return;
        lock (_lock)
        {
            foreach (var r in _rt.Values) { r.Dead = true; FreeRt(r); }
            _rt.Clear();
            foreach (var r in _trash) FreeRt(r);
            _trash.Clear();
        }
        foreach (var s in _monSig) s?.Dispose();
        foreach (var m in _monDiff) m?.Dispose();
        foreach (var m in _monBin) m?.Dispose();
    }

    public void Dispose()
    {
        _stop = true;
        Wake();
        if (_thread is { } t && !t.Join(3000))
            return;      // still inside a slice: leave it. It sees _stop, ends its loop and frees everything itself — disposing here would pull a Mat from under the detector.
        Teardown();
        _wake.Dispose();
    }
}
