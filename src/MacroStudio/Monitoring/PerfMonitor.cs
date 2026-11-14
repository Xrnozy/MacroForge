using System.Diagnostics;

namespace MacroStudio.Monitoring;

/// <summary>
/// This process's CPU and memory, sampled cheaply, plus a bounded history (one point per 10 s, at most 24 h) used to notice slow
/// problems in multi-day sessions: memory that keeps growing, or CPU use that keeps creeping up.
/// </summary>
public sealed class PerfMonitor
{
    public const int Capacity = 8640;            // 24 h at one point per 10 s
    const double PointEveryMs = 10_000;

    readonly Process _p = Process.GetCurrentProcess();
    readonly List<(DateTime T, double Cpu, double Ram)> _history = new();
    TimeSpan _lastCpu;
    DateTime _lastSample = DateTime.MinValue, _lastPoint = DateTime.MinValue;
    double _cpuAcc, _cpuN;

    /// <summary>Share of one core used by this process over the last sample interval.</summary>
    public double CpuPercent { get; private set; }
    public double RamMb { get; private set; }
    public double ManagedMb => GC.GetTotalMemory(false) / 1048576.0;
    public int HistoryCount => _history.Count;

    /// <summary>Call about once a second.</summary>
    public void Sample(DateTime now)
    {
        _p.Refresh();
        var cpu = _p.TotalProcessorTime;
        if (_lastSample != DateTime.MinValue)
        {
            double wall = (now - _lastSample).TotalMilliseconds;
            if (wall > 50) CpuPercent = (cpu - _lastCpu).TotalMilliseconds / wall * 100;
        }
        _lastCpu = cpu; _lastSample = now;
        RamMb = _p.WorkingSet64 / 1048576.0;
        Record(now, CpuPercent, RamMb);
    }

    /// <summary>Feed a reading (also used by tests). Readings are averaged into one stored point per 10 s.</summary>
    public void Record(DateTime now, double cpu, double ram)
    {
        _cpuAcc += cpu; _cpuN++;
        if (_lastPoint != DateTime.MinValue && (now - _lastPoint).TotalMilliseconds < PointEveryMs) return;
        _history.Add((now, _cpuAcc / _cpuN, ram));
        _cpuAcc = _cpuN = 0; _lastPoint = now;
        if (_history.Count > Capacity) _history.RemoveRange(0, _history.Count - Capacity);
    }

    /// <summary>
    /// A warning when the newest quarter of the history is clearly worse than the oldest quarter (needs about 6 minutes of data):
    /// memory up by 150 MB and 25%, or CPU more than doubled and above 10%. Null when all is steady.
    /// </summary>
    public string? Warning()
    {
        int n = _history.Count;
        if (n < 36) return null;
        int q = n / 4;
        double ramFirst = 0, ramLast = 0, cpuFirst = 0, cpuLast = 0;   // plain loops: no LINQ enumerators on a diagnostics call
        for (int i = 0; i < q; i++) { ramFirst += _history[i].Ram; cpuFirst += _history[i].Cpu; }
        for (int i = n - q; i < n; i++) { ramLast += _history[i].Ram; cpuLast += _history[i].Cpu; }
        ramFirst /= q; cpuFirst /= q; ramLast /= q; cpuLast /= q;
        double minutes = (_history[^1].T - _history[0].T).TotalMinutes;
        if (ramLast - ramFirst > 150 && ramLast > ramFirst * 1.25) return $"memory is growing: {ramFirst:0} → {ramLast:0} MB over {minutes:0} min";
        if (cpuLast > 10 && cpuLast > cpuFirst * 2) return $"CPU use has doubled: {cpuFirst:0}% → {cpuLast:0}% over {minutes:0} min";
        return null;
    }
}
