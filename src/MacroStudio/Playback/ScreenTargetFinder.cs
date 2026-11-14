using MacroStudio.Vision;

namespace MacroStudio.Playback;

/// <summary>
/// Finder backed by the shared <see cref="TargetWatcher"/>: the watcher thread does the screen work (tracked targets cost
/// almost nothing); this only reads its latest answer and tells it which target the macro is waiting for.
/// </summary>
public sealed class ScreenTargetFinder : ITargetFinder, IDisposable
{
    const double FreshMs = 800;              // a hit older than this is not trusted (watcher stalled or target gone)
    const double PrioritiseEveryMs = 400;    // the boost/wanted window is 1500 ms: refreshing within it keeps the target first

    readonly TargetWatcher _watcher;
    readonly Dictionary<string, Target> _targets;
    readonly IDisposable _watch;
    readonly Func<DateTime> _now;
    readonly Dictionary<string, DateTime> _prioAt = new();   // target id -> when it was last prioritised (called on the playback thread only)

    internal int PrioritiseCalls;            // tests count these: every Prioritise wakes the watcher for a frame grab

    public ScreenTargetFinder(TargetWatcher watcher, IReadOnlyList<Target> targets, Func<DateTime>? now = null)
    {
        _watcher = watcher;
        _now = now ?? (() => DateTime.UtcNow);
        _targets = targets.ToDictionary(t => t.Id);
        _watch = watcher.Watch(targets);
    }

    public string? NameOf(string id) => _targets.GetValueOrDefault(id)?.Name;
    public double ThresholdOf(string id) => _targets.GetValueOrDefault(id)?.Threshold ?? 0.85;

    public Sighting? Find(string id)
    {
        // A wait loop asks dozens of times a second, but every Prioritise sets the wake event and makes the watcher tick
        // (a frame grab). Once per PrioritiseEveryMs keeps the boost window alive without churning captures.
        if (!_prioAt.TryGetValue(id, out var at) || (_now() - at).TotalMilliseconds >= PrioritiseEveryMs)
        {
            _prioAt[id] = _now();
            try { _watcher.Prioritise(id); PrioritiseCalls++; }
            catch (ObjectDisposedException) { return null; }    // the watcher is gone mid-step: the target counts as not seen
        }
        var s = _watcher.Get(id);
        if (s?.Hit is not { } hit || (_now() - s.SeenUtc).TotalMilliseconds > FreshMs) return null;
        return new Sighting(hit, s.Seq, s.SeenUtc, s.Ambiguous);
    }

    public void Expect(string id, int x, int y) { try { _watcher.Expect(id, x, y); } catch (ObjectDisposedException) { } }

    public void Dispose() => _watch.Dispose();
}
