using MacroStudio.Model;
using MacroStudio.Storage;

namespace MacroStudio.Playback;

/// <summary>
/// What one playback run needs from the library, read ONCE: id -> path map, every connected macro (transitively) and the visual targets
/// they use. Nothing is re-read during the run (a macro edited or deleted meanwhile does not change a run that already started) and
/// everything is released by <see cref="Dispose"/>. Safe to call from the playback thread. Never throws: problems are logged in plain words.
/// </summary>
public sealed class PlaybackSession : IDisposable
{
    public const int MaxDepth = 6;                 // same limit as FlowRunner
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    sealed record Entry(string Name, MacroEvent[]? Events, Macro? Source);

    readonly Func<string, string?> _pathOf;
    readonly Func<string, Macro> _read;
    readonly Func<string, bool> _unsavedEdits;
    readonly ExecutionLog? _log;
    readonly long _maxBytes;
    readonly object _lock = new();
    Dictionary<string, Entry>? _cache = new();
    Dictionary<MacroEvent[], Entry>? _byEvents = new(ReferenceEqualityComparer.Instance);
    Dictionary<string, string>? _paths;
    Dictionary<MacroEvent[], TrajectoryPlan>? _plans = new(ReferenceEqualityComparer.Instance);

    /// <summary>The trajectory analysis of these events, built once per run (a macro called 1000 times is analysed once).</summary>
    public TrajectoryPlan PlanFor(MacroEvent[] events)
    {
        lock (_lock)
        {
            if (_plans == null) return TrajectoryPlan.Build(events);
            if (!_plans.TryGetValue(events, out var p)) _plans[events] = p = TrajectoryPlan.Build(events);
            return p;
        }
    }

    /// <param name="paths">id -> path snapshot (one directory scan for the whole run).</param>
    /// <param name="unsavedEdits">True for a file that is open in the editor with changes not saved yet (playback uses the saved file and says so).</param>
    public PlaybackSession(IReadOnlyDictionary<string, string> paths, ExecutionLog? log = null, Func<string, bool>? unsavedEdits = null, Func<string, Macro>? read = null, long maxBytes = DefaultMaxBytes)
    {
        _paths = new Dictionary<string, string>(paths);
        _pathOf = id => _paths != null && _paths.TryGetValue(id, out var p) ? p : null;
        _read = read ?? MacroSerializer.Load;
        _unsavedEdits = unsavedEdits ?? (_ => false);
        _log = log; _maxBytes = maxBytes;
    }

    public static PlaybackSession For(MacroLibrary lib, ExecutionLog? log = null, Func<string, bool>? unsavedEdits = null)
    {
        Dictionary<string, string> map;
        try { map = lib.IdMap(); } catch (Exception ex) { map = new(); log?.Add(-1, "library", LogResult.Failed, "could not list the macro library: " + ex.Message); }
        return new PlaybackSession(map, log, unsavedEdits);
    }

    /// <summary>The events of a connected macro, in time order; null when it cannot be used (the reason is logged once). Read from disk at most once per run.</summary>
    public MacroEvent[]? Events(string id) => Get(id)?.Events;

    /// <summary>The library name of a connected macro (its file name), or null when unknown.</summary>
    public string? NameOf(string id) => _paths is { } m && m.TryGetValue(id, out var p) ? MacroLibrary.NameOf(p) : null;   // the map never changes during a run: no lock, the UI may ask while a macro loads

    /// <summary>Where the events returned by <see cref="Events"/> were recorded (screen layout, input settings) and the macro's name.</summary>
    public (Macro? Source, string? Name) SourceOf(MacroEvent[] events)
    {
        lock (_lock) return _byEvents != null && _byEvents.TryGetValue(events, out var e) ? (e.Source, e.Name) : (null, null);
    }

    Entry? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock)
        {
            if (_cache == null) return null;                                   // run is over
            if (_cache.TryGetValue(id, out var hit)) return hit;
            var e = Load(id);
            _cache[id] = e;
            if (e.Events != null) _byEvents![e.Events] = e;
            return e;
        }
    }

    Entry Load(string id)
    {
        var path = _pathOf(id);
        if (path == null) { _log?.Add(-1, "library", LogResult.Warn, $"a connected macro (id {Short(id)}) is not in the library: it was deleted or never saved here"); return new(id, null, null); }
        string name = MacroLibrary.NameOf(path);
        try
        {
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size > _maxBytes) { _log?.Add(-1, "library", LogResult.Failed, $"connected macro '{name}' is too large to run ({size / 1048576} MB, the limit is {_maxBytes / 1048576} MB)"); return new(name, null, null); }
            var m = _read(path);
            if (m.Events == null) { _log?.Add(-1, "library", LogResult.Warn, $"connected macro '{name}' has no events"); return new(name, [], m); }
            var evs = m.Events.OrderBy(x => x.T).ToArray();
            if (_unsavedEdits(path)) _log?.Add(-1, "library", LogResult.Warn, $"connected macro '{name}' is open with unsaved changes: playback uses the saved file, not the edits");
            return new(name, evs, m);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _log?.Add(-1, "library", LogResult.Warn, $"the file of connected macro '{name}' is gone (deleted or renamed during the run)");
            return new(name, null, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Add(-1, "library", LogResult.Failed, $"could not read connected macro '{name}' ({ex.GetType().Name}: {ex.Message}); it is skipped");
            return new(name, null, null);
        }
        catch (OutOfMemoryException)
        {
            _log?.Add(-1, "library", LogResult.Failed, $"connected macro '{name}' is too large to load; it is skipped");
            return new(name, null, null);
        }
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>Every visual target the events use (clicks, When blocks, verify and recovery targets) and those of every connected macro, to <see cref="MaxDepth"/> levels. Loads each macro once.</summary>
    public HashSet<string> UsedTargets(IEnumerable<MacroEvent> events)
    {
        var used = new HashSet<string>(); var seen = new HashSet<string>();
        void Walk(IEnumerable<MacroEvent> evs, int depth)
        {
            foreach (var e in evs)
            {
                foreach (var t in TargetRemap.Used(e)) used.Add(t);
                if (e.IsWhen() && e.Flow?.Macro is { } mid && depth < MaxDepth && seen.Add(mid) && Events(mid) is { } child) Walk(child, depth + 1);
            }
        }
        Walk(events, 0);
        return used;
    }

    /// <summary>Releases the cache: nothing of this run stays reachable.</summary>
    public void Dispose()
    {
        lock (_lock) { _cache = null; _byEvents = null; _paths = null; _plans = null; }
    }
}
