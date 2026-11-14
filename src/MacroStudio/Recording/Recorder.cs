using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Recording;

/// <summary>Hook thread timestamps and enqueues; a consumer task builds events. Pause time is removed from the timeline.</summary>
public sealed class Recorder : IDisposable
{
    InputHooks? _hooks;
    RecordLog? _log;
    Thread? _sampler;
    volatile bool _sampling;
    int _gc2At;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);
    Channel<RawInput>? _channel;
    Task? _consumer;
    /// <summary>
    /// Recorded events in append-only chunks: a Store never copies older events (List.Add growth used to move megabytes under
    /// <see cref="_gate"/>), and a snapshot only captures (chunk arrays, count, epoch) under the lock and materializes its list outside it,
    /// so the live view's copy can never block the consumer.
    /// </summary>
    const int ChunkShift = 13, ChunkSize = 1 << ChunkShift, ChunkMask = ChunkSize - 1;
    List<MacroEvent[]> _chunks = new();
    readonly object _gate = new();
    long _t0, _pauseStart, _pausedTicks;
    volatile bool _paused;
    int _count;

    /// <summary>Hotkey virtual-keys that control the app and must not end up in the macro.</summary>
    public HashSet<int> IgnoredKeys { get; } = new();

    /// <summary>Main key of a hotkey -> the modifier bits it needs (Alt 1, Ctrl 2, Shift 4, Win 8). Pressing the combination removes the modifier presses that led up to it.</summary>
    public Dictionary<int, int> HotkeyMods { get; } = new();

    /// <summary>Where the diagnostic log of the last (or current) recording is.</summary>
    public string? LogPath => _log?.Path;

    /// <summary>Set while the program in front runs with higher privileges than this app: Windows then hides its keyboard and mouse input from us.</summary>
    public volatile string? Warning;

    /// <summary>Extra facts the caller wants in the log header (hotkeys, options).</summary>
    public void Note(string s) => _log?.Line(s);

    internal static int StopWaitMs = 5000;

    /// <summary>How much raw input the recording queue may hold before input is counted as dropped: a stalled consumer then cannot grow memory without bound (the backup poller still catches held keys and button states).</summary>
    internal static int ChannelCapacity = 10_000;

    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public int Count => Volatile.Read(ref _count);
    public double ElapsedMs
    {
        get
        {
            if (!IsRecording) return 0;
            long now = _paused ? _pauseStart : Stopwatch.GetTimestamp();
            return (now - _t0 - Interlocked.Read(ref _pausedTicks)) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>Input this says to drop (e.g. clicks on the app's own window while it stays on top).</summary>
    public Func<RawInput, bool>? Ignore { get; set; }

    /// <summary>
    /// The consistent point-in-time state a snapshot copies from: capture is allocation-free and is the only part under <see cref="_gate"/>.
    /// Slots below <c>Total</c> are never rewritten (TakeBack swaps in a rebuilt list), so the caller may read them after the lock is released.
    /// </summary>
    (List<MacroEvent[]> Chunks, int Total, int Epoch) Capture()
    {
        lock (_gate) return (_chunks, _count, _epoch);
    }

    static List<MacroEvent> ReadRange(List<MacroEvent[]> chunks, int from, int to)
    {
        var list = new List<MacroEvent>(to - from);
        for (int i = from; i < to; i++) list.Add(chunks[i >> ChunkShift][i & ChunkMask]);
        return list;
    }

    /// <summary>A copy of what has been recorded so far, safe to read while recording.</summary>
    public List<MacroEvent> Snapshot()
    {
        var c = Capture();
        return ReadRange(c.Chunks, 0, c.Total);
    }

    int _epoch;

    /// <summary>
    /// Only the events stored after the first <paramref name="have"/>: a cost of the new events, not of the whole recording.
    /// <paramref name="epoch"/> is the caller's remembered value; when an earlier event was taken back out (a hotkey's modifier press) it no longer matches,
    /// <paramref name="full"/> is true and the list holds everything so the caller can replace what it has.
    /// </summary>
    public List<MacroEvent> SnapshotFrom(int have, ref int epoch, out bool full)
    {
        var c = Capture();
        full = epoch != c.Epoch || have > c.Total || have < 0;
        epoch = c.Epoch;
        return ReadRange(c.Chunks, full ? 0 : have, c.Total);
    }

    /// <summary>Removes an already stored event (a hotkey's modifier press); live views holding the old list must then reload.</summary>
    internal void TakeBack(int at)
    {
        lock (_gate)
        {
            int n = _count;
            if (at < 0 || at >= n) return;
            var flat = new MacroEvent[n - 1];
            int w = 0;
            for (int i = 0; i < n; i++) if (i != at) flat[w++] = _chunks[i >> ChunkShift][i & ChunkMask];
            _chunks = new List<MacroEvent[]>((flat.Length >> ChunkShift) + 1);
            for (int c = 0; c * ChunkSize < flat.Length; c++)
            {
                var chunk = new MacroEvent[ChunkSize];
                Array.Copy(flat, c * ChunkSize, chunk, 0, Math.Min(ChunkSize, flat.Length - c * ChunkSize));
                _chunks.Add(chunk);
            }
            Volatile.Write(ref _count, flat.Length);
            _epoch++;
        }
    }

    /// <summary>Finds the last stored event matching <paramref name="match"/> (the hotkey take-back's modifier press).</summary>
    bool TryFindLast(Func<MacroEvent, bool> match, out int at, out MacroEvent e)
    {
        lock (_gate)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                e = _chunks[i >> ChunkShift][i & ChunkMask];
                if (match(e)) { at = i; return true; }
            }
        }
        at = -1; e = default; return false;
    }

    /// <summary>Appends one event; the caller holds <see cref="_gate"/>. Id and Count both come from the same slot so they stay contiguous.</summary>
    void Append(in MacroEvent e)
    {
        int at = _count;
        if ((at & ChunkMask) == 0) _chunks.Add(new MacroEvent[ChunkSize]);
        _chunks[at >> ChunkShift][at & ChunkMask] = e;
        Volatile.Write(ref _count, at + 1);
    }

    /// <summary>Stores one captured input (called by the consumer in <see cref="ConsumeAsync"/>; tests may also feed it directly).</summary>
    internal void Store(in RawInput r)
    {
        lock (_gate)
            Append(new MacroEvent(r.Ticks * 1000.0 / Stopwatch.Frequency, r.Kind, r.Vk, r.Scan, r.Ext, r.X, r.Y, r.Button, r.Delta, _count + 1));
    }

    /// <summary>Waits for the consumer, but never for good: false means it is stuck and the caller carries on.</summary>
    internal static bool JoinConsumer(Task consumer, int ms) { try { return consumer.Wait(ms); } catch { return true; } }

    public void Start()
    {
        if (IsRecording) return;
        _chunks = new List<MacroEvent[]>();
        _count = 0; _pausedTicks = 0; _paused = false;
        var (cx, cy) = Screens.CursorPos();
        lock (_gate) Append(new MacroEvent(0, EventKind.MouseMove, X: cx, Y: cy, Id: 1));   // known start position for playback
        _t0 = Stopwatch.GetTimestamp();
        _channel = Channel.CreateBounded<RawInput>(new BoundedChannelOptions(ChannelCapacity) { SingleReader = true, SingleWriter = false });
        var writer = _channel.Writer;
        var reader = _channel.Reader;
        Warning = null;
        _log = new RecordLog(); var log = _log;
        WriteHeader(log);
        _hooks = new InputHooks(r =>
        {
            if (_paused) { log.Count("dropped: while paused"); return; }
            if (Ignore?.Invoke(r) == true) { log.Count("dropped: over this app's own window (always-on-top)"); return; }
            if (!writer.TryWrite(r with { Ticks = r.Ticks - _t0 - Volatile.Read(ref _pausedTicks) })) // pause-adjusted at capture time
                log.Event("dropped: channel full (consumer stalled)", $"{r.Kind} vk={r.Vk}");
        }) { PollFallback = true, Log = log };
        _consumer = Task.Run(() => ConsumeAsync(reader, log));
        _hooks.Start();
        log.Line("hooks installed, recording started");
        IsRecording = true;
        StartSampler(log);
    }

    /// <summary>
    /// Builds events out of raw inputs: drops the app's hotkeys and the key / button releases whose press was never seen.
    /// Internal seam (not a public API): tests feed a channel directly and so cover the hotkey / drop logic without OS hooks.
    /// </summary>
    internal async Task ConsumeAsync(ChannelReader<RawInput> reader, RecordLog? log)
    {
        var heldKeys = new HashSet<int>();
        var heldBtns = new HashSet<MouseBtn>();
        var swallowed = new HashSet<int>();   // hotkey presses we dropped: their repeats and release go too
        int HeldMods() => (heldKeys.Any(v => v is 0x12 or 0xA4 or 0xA5) ? Native.HotkeySettings.Alt : 0) | (heldKeys.Any(v => v is 0x11 or 0xA2 or 0xA3) ? Native.HotkeySettings.Ctrl : 0)
            | (heldKeys.Any(v => v is 0x10 or 0xA0 or 0xA1) ? Native.HotkeySettings.Shift : 0) | (heldKeys.Any(v => v is 0x5B or 0x5C) ? Native.HotkeySettings.Win : 0);
        await foreach (var r in reader.ReadAllAsync())
        {
            // Ctrl+Shift+Alt+R to stop (or pause): its modifier presses were just recorded, but they belong to the hotkey, not to the macro.
            // Take them back out; their releases are then dropped by the "release without a press" rule below.
            // Only the exact combination is the app's hotkey: a bare A or D (Ctrl+Shift+A is the hotkey) is the user's game input and is recorded.
            bool hotkeyPress = r.Kind == EventKind.KeyDown && HotkeyMods.TryGetValue(r.Vk, out int need) && (need == 0 || need == HeldMods());
            if (hotkeyPress && HotkeyMods[r.Vk] is int hm && hm != 0)
                foreach (int mv in Native.HotkeySettings.VksOf(hm))
                    if (heldKeys.Remove(mv))
                    {
                        log?.Event("removed: modifier press that led into an app hotkey", $"vk=0x{mv:X} before 0x{r.Vk:X}");
                        bool recent = TryFindLast(x => x.Kind == EventKind.KeyDown && x.Vk == mv, out int at, out var e)
                            && r.Ticks * 1000.0 / Stopwatch.Frequency - e.T <= 3000;
                        if (recent) TakeBack(at);
                    }
            double lateMs = (Stopwatch.GetTimestamp() - _t0 - Volatile.Read(ref _pausedTicks) - r.Ticks) * 1000.0 / Stopwatch.Frequency;
            log?.Max("event waited in queue before it was stored (ms)", lateMs);
            if (lateMs > 50) log?.Event("slow: event stored > 50 ms after it happened", $"{r.Kind} vk={r.Vk} waited {lateMs:F0} ms");
            if (hotkeyPress || (r.Kind is EventKind.KeyDown or EventKind.KeyUp && swallowed.Contains(r.Vk)))
            {
                log?.Event("dropped: app hotkey key", $"{r.Kind} vk=0x{r.Vk:X}");
                if (hotkeyPress) swallowed.Add(r.Vk); else if (r.Kind == EventKind.KeyUp) swallowed.Remove(r.Vk);
                continue;
            }
            // A release whose press happened before recording (e.g. the click on the Record button) is noise.
            if (r.Kind == EventKind.KeyDown) heldKeys.Add(r.Vk);
            else if (r.Kind == EventKind.KeyUp && !heldKeys.Remove(r.Vk)) { log?.Event("dropped: key release without a press seen", $"vk=0x{r.Vk:X} at {r.Ticks * 1000.0 / Stopwatch.Frequency:F0} ms"); continue; }
            else if (r.Kind == EventKind.ButtonDown) heldBtns.Add(r.Button);
            else if (r.Kind == EventKind.ButtonUp && !heldBtns.Remove(r.Button)) { log?.Event("dropped: button release without a press seen", $"{r.Button} at {r.Ticks * 1000.0 / Stopwatch.Frequency:F0} ms"); continue; }
            log?.Count(RecordLog.StoredKey[(int)r.Kind]);
            Store(r);
        }
    }

    void WriteHeader(RecordLog log)
    {
        log.Section("Machine");
        NtQueryTimerResolution(out _, out _, out uint cur);
        log.Raw($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Windows {Environment.OSVersion.Version}  {(Environment.Is64BitProcess ? "x64" : "x86")}  cpus {Environment.ProcessorCount}");
        log.Raw($"  remote desktop session: {GetSystemMetrics(0x1000) != 0}   this app elevated (admin): {IsAdmin()}");
        log.Raw($"  system timer resolution: {cur / 10000.0:F2} ms   priority: {Process.GetCurrentProcess().PriorityClass}");
        try { var d = Screens.Current(); log.Raw($"  desktop: virtual {d.Virtual.Width}x{d.Virtual.Height} at {d.Virtual.X},{d.Virtual.Y}"); } catch { }
        _gc2At = GC.CollectionCount(2);
        log.Section("Timeline of this recording");
    }

    static bool IsAdmin()
    {
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>Every 100 ms: who has the foreground (a protected or admin program there explains missing input), how late this thread woke, how long the UI thread takes to answer.</summary>
    void StartSampler(RecordLog log)
    {
        _sampling = true;
        _sampler = new Thread(() =>
        {
            string last = ""; long prev = Stopwatch.GetTimestamp();
            while (_sampling)
            {
                Thread.Sleep(100);
                long now = Stopwatch.GetTimestamp();
                double late = (now - prev) * 1000.0 / Stopwatch.Frequency - 100; prev = now;
                log.Max("sampler thread woke late (ms) = whole-machine stall", late);
                if (late > 150) log.Event("stall: this app was not scheduled for > 150 ms", $"{late:F0} ms late");
                try
                {
                    var h = GetForegroundWindow(); GetWindowThreadProcessId(h, out uint pid);
                    var sb = new StringBuilder(120); GetWindowText(h, sb, 120);
                    string proc = "?", access = "";
                    try { var p = Process.GetProcessById((int)pid); proc = p.ProcessName; try { _ = p.MainModule; } catch { access = "  [cannot be inspected: elevated/protected/anti-cheat]"; } } catch { }
                    string cur = $"{proc} (pid {pid}) \"{sb}\"{access}";
                    if (cur != last) { log.Line("foreground: " + cur); last = cur; }
                    Warning = access != "" && !IsAdmin() ? $"\"{proc}\" runs with higher privileges: Windows hides its input from this app. Use File > Restart as administrator." : null;
                }
                catch { }
                var ui = System.Windows.Application.Current?.Dispatcher;
                if (ui != null)
                {
                    long t = Stopwatch.GetTimestamp();
                    ui.BeginInvoke(() => log.Max("UI thread answered after (ms)", (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency));
                }
            }
        }) { IsBackground = true, Name = "RecordSampler" };
        _sampler.Start();
    }

    void WriteSummary(RecordLog log, Macro m)
    {
        log.Line("recording stopped");
        var ev = m.Events;
        double total = ev.Count > 0 ? ev[^1].T : 0;
        log.Section("Result");
        log.Raw($"  {ev.Count} events over {total / 1000:F1} s   (keys down {ev.Count(e => e.Kind == EventKind.KeyDown)}, button down {ev.Count(e => e.Kind == EventKind.ButtonDown)}, moves {ev.Count(e => e.Kind == EventKind.MouseMove)}, look {ev.Count(e => e.Kind == EventKind.RelMove)}, wheel {ev.Count(e => e.Kind is EventKind.Wheel or EventKind.HWheel)})");
        log.Raw($"  gen-2 garbage collections during the recording: {GC.CollectionCount(2) - _gc2At}");
        var gaps = new List<(double At, double Len)>();
        for (int i = 1; i < ev.Count; i++) if (ev[i].T - ev[i - 1].T > 250) gaps.Add((ev[i - 1].T, ev[i].T - ev[i - 1].T));
        log.Section($"Gaps with no input at all longer than 250 ms: {gaps.Count}");
        foreach (var g in gaps.Take(60)) log.Raw($"  from {g.At / 1000:F2} s for {g.Len:F0} ms");
        log.Section("Events per second (0 = nothing recorded)");
        var sb = new StringBuilder("  ");
        for (int s = 0; s <= (int)(total / 1000); s++) sb.Append($"{s}s:{ev.Count(e => (int)(e.T / 1000) == s)}  ");
        log.Raw(sb.ToString());
        log.WriteCounters();
        log.Section("How to read this");
        log.Raw("  hook: = what Windows' input hooks delivered. poller: = what the backup check had to add because the hooks did not report it.");
        log.Raw("  Many 'poller: caught' lines = the hooks are losing input (anti-cheat, remote desktop, elevated program in the foreground).");
        log.Raw("  'foreground:' lines show which program had focus; a gap right after one marked elevated/protected points at it.");
        log.Raw("  'stall'/'slow' lines and large worst values = the machine or this app was too busy to record in time.");
    }

    public void Pause()
    {
        if (!IsRecording || _paused) return;
        _pauseStart = Stopwatch.GetTimestamp();
        _paused = true;
    }

    public void Resume()
    {
        if (!IsRecording || !_paused) return;
        Interlocked.Add(ref _pausedTicks, Stopwatch.GetTimestamp() - _pauseStart);
        _paused = false;
    }

    public Macro Stop(string name)
    {
        if (!IsRecording) throw new InvalidOperationException("Not recording");
        _hooks!.Dispose();
        _channel!.Writer.Complete();
        bool stuck = !JoinConsumer(_consumer!, StopWaitMs);
        if (stuck) _log?.Line($"WARNING: the recording consumer did not finish within {StopWaitMs} ms; saving what was stored so far");
        IsRecording = false;
        _hooks = null;
        _sampling = false; _sampler?.Join(1000);
        var macro = new Macro { Name = name, Desktop = Screens.Current(), Env = Screens.CurrentEnv(), Events = Snapshot() };
        try { WriteSummary(_log!, macro); } catch (Exception ex) { _log?.Line("summary failed: " + ex.Message); }
        _log?.Save();
        return macro;
    }

    public void Dispose()
    {
        if (IsRecording) Stop("discarded");
    }
}
