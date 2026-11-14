using System.Diagnostics;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;
using MacroStudio.Recording;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>Round 2 budgets: live-recording snapshots, backup key poller, playback clock, bounded Stop. No hooks, no SendInput.</summary>
public class PerfRound2EngineTests(ITestOutputHelper o)
{
    static RawInput Raw(long i) => new(i * 10, EventKind.MouseMove, 0, 0, false, (int)(i % 1900), (int)(i % 1000), default, 0);

    // ---- 1. recorder snapshots ----

    [Fact]
    public void Incremental_snapshots_cost_the_new_events_not_the_whole_recording()
    {
        var rec = new Recorder();
        for (int i = 0; i < 200_000; i++) rec.Store(Raw(i));
        int have = 200_000, epoch = 0; var sw = new Stopwatch(); double worst = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            for (int i = 0; i < 50; i++) rec.Store(Raw(have + i));
            sw.Restart();
            var tail = rec.SnapshotFrom(have, ref epoch, out bool full);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            Assert.False(full);
            Assert.Equal(50, tail.Count);
            Assert.Equal(have + 1, tail[0].Id);
            have += 50;
        }
        var all = Stopwatch.StartNew(); rec.Snapshot(); double whole = all.Elapsed.TotalMilliseconds;
        o.WriteLine($"incremental worst {worst:F3} ms, whole copy {whole:F3} ms");
        Assert.True(worst < 1.0, $"incremental snapshot took {worst:F3} ms");
    }

    [Fact]
    public void The_consumer_is_never_blocked_while_the_live_view_snapshots()
    {
        var rec = new Recorder();
        for (int i = 0; i < 200_000; i++) rec.Store(Raw(i));
        var stop = false; int have = 200_000, epoch = 0;
        var reader = Task.Run(() => { while (!stop) { rec.SnapshotFrom(have, ref epoch, out _); Thread.Sleep(1); } });
        double worst = double.MaxValue; var sw = new Stopwatch(); int n = 200_000;
        for (int attempt = 0; attempt < 3 && worst >= 5; attempt++)   // a busy machine can stall any thread once: the budget must hold in one of three tries
        {
            double w = 0;
            for (int i = 0; i < 20_000; i++) { sw.Restart(); rec.Store(Raw(n++)); w = Math.Max(w, sw.Elapsed.TotalMilliseconds); if (i % 100 == 0) Thread.Sleep(1); }
            worst = Math.Min(worst, w);
        }
        stop = true; reader.Wait();
        o.WriteLine($"worst Store while snapshotting {worst:F3} ms");
        Assert.True(worst < 5, $"consumer blocked {worst:F2} ms");
    }

    [Fact]
    public void A_taken_back_event_makes_the_next_snapshot_full_so_the_view_never_goes_stale()
    {
        var rec = new Recorder();
        for (int i = 0; i < 10; i++) rec.Store(Raw(i));
        int epoch = 0; rec.SnapshotFrom(0, ref epoch, out _);
        Assert.Empty(rec.SnapshotFrom(10, ref epoch, out bool full)); Assert.False(full);
        rec.TakeBack(3);
        var again = rec.SnapshotFrom(10, ref epoch, out full);
        Assert.True(full); Assert.Equal(9, again.Count);
    }

    [Fact]
    public void Session_append_adds_only_the_tail_and_keeps_ids_and_order()
    {
        var s = new EditSession();
        s.Load(new Macro { Name = "r", Events = Enumerable.Range(0, 1000).Select(i => new MacroEvent(i, EventKind.MouseMove, X: i, Id: i + 1)).ToList() }, null);
        int v = s.Version;
        Assert.True(s.Append(Enumerable.Range(1000, 5).Select(i => new MacroEvent(i, EventKind.MouseMove, X: i, Id: i + 1)).ToList()));
        Assert.Equal(1005, s.Events.Length); Assert.Equal(1005, s.Events[^1].Id); Assert.True(s.Version > v);
        Assert.False(s.Append([new MacroEvent(5, EventKind.MouseMove, Id: 2000)]));      // older than the end: caller does a full load
        Assert.Equal(1005, s.Events.Length);
        var big = new EditSession();
        big.Load(new Macro { Name = "r", Events = Enumerable.Range(0, 200_000).Select(i => new MacroEvent(i, EventKind.MouseMove, X: i, Id: i + 1)).ToList() }, null);
        var tail = Enumerable.Range(200_000, 50).Select(i => new MacroEvent(i, EventKind.MouseMove, X: i, Id: i + 1)).ToList();
        var sw = Stopwatch.StartNew(); Assert.True(big.Append(tail)); double ms = sw.Elapsed.TotalMilliseconds;
        o.WriteLine($"Append 50 onto 200k: {ms:F2} ms");
        Assert.True(ms < 10, $"append {ms:F2} ms");
    }

    // ---- 2. backup poller ----

    [Fact]
    public void Poller_reads_far_fewer_keys_yet_still_catches_every_one_within_8ms()
    {
        var down = new bool[256]; var got = new List<(int Vk, bool Down)>(); int calls = 0;
        using var h = new InputHooks(r => { lock (got) got.Add((r.Vk, r.Kind == EventKind.KeyDown)); });
        long grace = Stopwatch.Frequency / 50;
        bool Read(int vk) { calls++; return down[vk]; }
        // every key, pressed and released out of step with the polling, must be seen by the second tick after the change (8 ms at the 4 ms tick)
        int tick = 0;
        foreach (int vk in new[] { 0x41, 0x42, 0x57, 0x20, 0x87, 0xA0, 0x70, 0xFE })
        {
            down[vk] = true; int t0 = tick; got.Clear();
            while (got.Count == 0 && tick < t0 + 10) h.ScanKeys(tick++, Read, grace);
            Assert.True(got.Count == 1 && got[0] == (vk, true) && tick - t0 <= 2, $"press of 0x{vk:X} caught after {tick - t0} ticks");
            down[vk] = false; t0 = tick; got.Clear();
            while (got.Count == 0 && tick < t0 + 10) h.ScanKeys(tick++, Read, grace);
            Assert.True(got.Count == 1 && got[0] == (vk, false) && tick - t0 <= 2, $"release of 0x{vk:X} caught after {tick - t0} ticks");
        }
        calls = 0; for (int i = 0; i < 1000; i++) h.ScanKeys(tick++, Read, grace);
        o.WriteLine($"key reads per second at 250 ticks/s: {calls / 4} (was 63500)");
        Assert.True(calls <= 1000 * 254 * 0.55, $"{calls} reads in 1000 ticks");
    }

    // ---- 3. clock ----

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetThreadTimes(nint h, out long c, out long e, out long k, out long u);
    /// <summary>CPU of this thread only (the runner's other threads must not count), in ms.</summary>
    static double ThreadCpuMs() { GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u); return (k + u) / 10_000.0; }

    sealed class OldClock : IClock
    {
        public double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        public void Sleep(double ms) { if (ms > 2.5) Thread.Sleep(1); else Thread.SpinWait(20); }
    }

    static (double CpuMs, double WallMs, double MaxLate, double MedLate, double P99) Run(IClock c, int n, double step)
    {
        var cpu0 = ThreadCpuMs(); var w = Stopwatch.StartNew();
        double t0 = c.NowMs + 5; var late = new List<double>();
        for (int i = 0; i < n; i++)
        {
            double due = t0 + i * step, now;
            while ((now = c.NowMs) < due) c.Sleep(due - now);
            late.Add(now - due);
        }
        late.Sort();
        return (ThreadCpuMs() - cpu0, w.Elapsed.TotalMilliseconds, late[^1], late[late.Count / 2], late[late.Count * 99 / 100]);
    }

    [Fact]
    public void Real_clock_waits_cheaply_and_on_time()
    {
        NativeMethods.timeBeginPeriod(1);   // the player does the same while it runs
        try
        {
            Run(new RealClock(), 20, 5);
            (double CpuMs, double WallMs, double MaxLate, double MedLate, double P99) oldr = default, r = default;
            for (int attempt = 0; attempt < 3; attempt++)   // a busy machine delays any wake-up: the budget must hold in one of three tries
            {
                oldr = Run(new OldClock(), 200, 5); r = Run(new RealClock(), 200, 5);
                o.WriteLine($"200 events 5 ms apart | old: cpu {oldr.CpuMs:F0} of {oldr.WallMs:F0} ms, max late {oldr.MaxLate:F2}, p99 {oldr.P99:F2}, median {oldr.MedLate:F3} | new: cpu {r.CpuMs:F0} of {r.WallMs:F0} ms, max late {r.MaxLate:F2}, p99 {r.P99:F2}, median {r.MedLate:F3}");
                if (r.P99 < 5.0 && r.MedLate < 0.2) break;
            }
            Assert.True(r.P99 < 5.0 && r.MedLate < 0.2, $"p99 lateness {r.P99:F2} ms, median {r.MedLate:F3}");
            Assert.True(r.CpuMs < r.WallMs * 0.15, $"cpu {r.CpuMs:F0} ms of {r.WallMs:F0} ms wall");
            var long2 = Run(new RealClock(), 1, 2000);
            o.WriteLine($"2 s wait: cpu {long2.CpuMs:F0} ms");
            Assert.True(long2.CpuMs < 100, $"cpu {long2.CpuMs:F0} ms for a 2 s wait");
        }
        finally { NativeMethods.timeEndPeriod(1); }
    }

    // ---- 4. Stop ----

    [Fact]
    public void A_stuck_consumer_cannot_hang_Stop_for_good()
    {
        var never = new TaskCompletionSource().Task;
        var sw = Stopwatch.StartNew();
        Assert.False(Recorder.JoinConsumer(never, 200));
        Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);
        Assert.True(Recorder.JoinConsumer(Task.CompletedTask, 200));
        Assert.True(Recorder.StopWaitMs is >= 1000 and <= 10_000);
    }
}
