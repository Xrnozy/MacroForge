namespace MacroStudio.Playback;

public enum LogResult { Ok, Warn, Retry, Skipped, Fallback, Failed }

/// <param name="Index">Position of the event in the macro (0-based), -1 when not tied to one.</param>
public sealed record LogEntry(DateTime Utc, int Index, string What, LogResult Result, string Message, double? Confidence = null, int? X = null, int? Y = null, double ElapsedMs = 0);

/// <summary>
/// What playback did, step by step. A fixed-size ring: a macro running for days keeps only the most recent
/// <see cref="Capacity"/> entries, so the log can never grow without bound.
/// </summary>
public sealed class ExecutionLog
{
    public const int Capacity = 2000;
    readonly LogEntry?[] _ring;
    readonly object _lock;
    readonly ExecutionLog? _root;      // a scoped view writes into the root's ring
    readonly string? _source;
    long _count;

    public ExecutionLog() { _ring = new LogEntry?[Capacity]; _lock = new object(); }
    ExecutionLog(ExecutionLog root, string source) { _root = root; _source = source; _ring = root._ring; _lock = root._lock; }

    /// <summary>A view of this log whose entries say which macro they come from ("[Player selection] click"). Same ring, same counters.</summary>
    public ExecutionLog Scoped(string macroName) => new((_root ?? this), macroName);

    /// <summary>Total entries ever added (also tells a viewer whether anything is new since it last looked).</summary>
    public long Total { get { var r = _root ?? this; lock (_lock) return r._count; } }

    public void Add(LogEntry e)
    {
        var r = _root ?? this;
        if (_source != null) e = e with { What = $"[{_source}] {e.What}" };
        lock (_lock) r._ring[r._count++ % Capacity] = e;
    }

    public void Add(int index, string what, LogResult result, string message, double? confidence = null, int? x = null, int? y = null, double elapsedMs = 0) =>
        Add(new LogEntry(DateTime.UtcNow, index, what, result, message, confidence, x, y, elapsedMs));

    /// <summary>The most recent entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot(int max = Capacity)
    {
        var r = _root ?? this;
        lock (_lock)
        {
            int n = (int)Math.Min(Math.Min(r._count, Capacity), max);
            var list = new List<LogEntry>(n);
            for (long i = r._count - n; i < r._count; i++) list.Add(r._ring[i % Capacity]!);
            return list;
        }
    }

    public int Count(LogResult res) { var r = _root ?? this; lock (_lock) { int n = 0; foreach (var e in r._ring) if (e?.Result == res) n++; return n; } }

    public void Clear() { var r = _root ?? this; lock (_lock) { Array.Clear(r._ring); r._count = 0; } }
}
