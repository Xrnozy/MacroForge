using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace MacroStudio.Recording;

/// <summary>
/// A plain-text diagnostic log of ONE recording: the machine, what each capture path saw, everything that was dropped and why, slow spots,
/// gaps and the foreground window. Written to %APPDATA%\MacroStudio\logs. Counters are exact; detail lines are capped per category.
/// </summary>
public sealed class RecordLog
{
    const int DetailCap = 60;
    readonly object _l = new();
    readonly StringBuilder _sb = new();
    readonly long _t0 = Stopwatch.GetTimestamp();
    readonly ConcurrentDictionary<string, long> _count = new();
    readonly ConcurrentDictionary<string, int> _shown = new();
    readonly ConcurrentDictionary<string, double> _max = new();

    public string Path { get; }
    public double Now => (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroStudio");   // the same folder as the recordings

    public RecordLog()
    {
        Directory.CreateDirectory(Folder);
        Path = System.IO.Path.Combine(Folder, $"record-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        try { foreach (var old in new DirectoryInfo(Folder).GetFiles("record-*.log").OrderByDescending(f => f.LastWriteTime).Skip(20)) old.Delete(); } catch { }
    }

    public void Line(string s) { lock (_l) _sb.Append($"[{Now,10:F1} ms] {s}\n"); }

    /// <summary>Counter keys per event kind, built once so the hook and consumer paths never build strings per event.</summary>
    public static readonly string[] HookMouseKey = Enum.GetNames<Model.EventKind>().Select(n => "hook: mouse " + n).ToArray();
    public static readonly string[] StoredKey = Enum.GetNames<Model.EventKind>().Select(n => "stored: " + n).ToArray();

    public void Count(string key, long n = 1) => _count.AddOrUpdate(key, static (_, a) => a, static (_, v, a) => v + a, n);

    /// <summary>Count it, and write the first few occurrences with their detail.</summary>
    public void Event(string key, string detail)
    {
        Count(key);
        if (_shown.AddOrUpdate(key, 1, (_, v) => v + 1) <= DetailCap) Line($"{key}: {detail}");
    }

    public void Max(string key, double v)
    {
        if (_max.TryGetValue(key, out var m) && v <= m) return; // the common case: no lock
        lock (_l) if (!_max.TryGetValue(key, out m) || v > m) _max[key] = v;
    }

    public void Section(string title) { lock (_l) _sb.Append($"\n== {title} ==\n"); }
    public void Raw(string s) { lock (_l) _sb.Append(s).Append('\n'); }

    public void WriteCounters()
    {
        Section("Counters (exact)");
        foreach (var kv in _count.OrderBy(k => k.Key)) Raw($"  {kv.Key,-52} {kv.Value}");
        Section("Worst values");
        lock (_l) foreach (var kv in _max.OrderBy(k => k.Key)) _sb.Append($"  {kv.Key,-52} {kv.Value:F1}\n");
    }

    public void Save()
    {
        try { lock (_l) File.WriteAllText(Path, _sb.ToString()); } catch { }
    }
}
