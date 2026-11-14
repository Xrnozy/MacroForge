using System.Collections.Concurrent;
using System.Diagnostics;
using MacroStudio.Editing;
using MacroStudio.Model;
using Xunit.Abstractions;

namespace MacroStudio.Tests;

/// <summary>
/// O5: the EditSession is read and written from several threads at once (live-recording timer vs. views vs. a playing macro).
/// Published event arrays must always be complete and self-consistent: sorted, unique ids, and lookups that never see a map
/// built for a different array. The session serializes its mutations; readers see whole snapshots.
/// </summary>
public class O5SessionThreadTests(ITestOutputHelper o)
{
    static MacroEvent Ev(int i, double? t = null) => new(t ?? i * 4.0, EventKind.MouseMove, X: i % 1900, Y: i % 1000, Id: i + 1);

    static Macro Snap(int n, int startId = 1)
    {
        var ev = Enumerable.Range(0, n).Select(i => Ev(i) with { Id = 0 }).ToList();   // a live snapshot carries no ids
        return new Macro { Name = "Recording…", Events = ev };
    }

    /// <summary>Every published Events array must be sorted with unique ids, no matter who mutates concurrently.</summary>
    static void AssertWellFormed(MacroEvent[] ev)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < ev.Length; i++)
        {
            Assert.True(i == 0 || ev[i - 1].T <= ev[i].T, $"array not sorted at {i}");
            Assert.True(ev[i].Id > 0 && seen.Add(ev[i].Id), $"bad or duplicate id at {i}: {ev[i].Id}");
        }
    }

    [Fact]
    public void Concurrent_Load_Append_and_reads_never_publish_a_torn_session()
    {
        var s = new EditSession();
        s.Load(Snap(20_000), null);
        var stop = new ManualResetEventSlim();
        var errors = new ConcurrentQueue<Exception>();

        // readers: whatever they observe must be a complete, well-formed snapshot
        var readers = Enumerable.Range(0, 3).Select(r => new Thread(() =>
        {
            try
            {
                var rnd = new Random();
                while (!stop.IsSet)
                {
                    var ev = s.Events;
                    AssertWellFormed(ev);
                    if (ev.Length > 0)
                    {
                        var e = ev[rnd.Next(ev.Length)];
                        int ix = s.IndexOfId(e.Id);
                        if (ix >= 0) Assert.Equal(e.Id, s.Events[ix].Id);   // may map into a newer array, but never out of range or wrong
                        _ = s.PosIndex; _ = s.Analysis; _ = s.Duration; _ = s.PositionAt(e.T); _ = s.ToMacro();
                    }
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToList();

        // writers: a live snapshot that keeps growing, plus tail appends and a full reload — the real shapes the UI sends
        var writers = new List<Thread>();
        for (int w = 0; w < 2; w++)
        {
            int ww = w;
            writers.Add(new Thread(() =>
            {
                try
                {
                    var baseEvents = Snap(20_000).Events;
                    for (int i = 1; i <= 150 && !stop.IsSet; i++)
                    {
                        var m = new Macro { Name = "Recording…", Events = baseEvents.Concat(Enumerable.Range(0, i).Select(k => Ev(20_000 + ww * 1000 + k) with { Id = 0 })).ToList() };
                        s.Load(m, null);                                   // TryAppend path most of the time
                        var last = s.Events[^1];
                        s.Append([new MacroEvent(last.T + 1, EventKind.MouseMove, X: ww, Id: 0)]);   // ok to be refused
                    }
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }));
        }

        readers.ForEach(t => t.Start()); writers.ForEach(t => t.Start());
        writers.ForEach(t => t.Join(30_000));
        stop.Set(); readers.ForEach(t => t.Join(30_000));
        if (!errors.IsEmpty) throw new Exception("concurrent session access failed", errors.First());
        AssertWellFormed(s.Events);
        o.WriteLine($"final: {s.Events.Length} events, version {s.Version}");
    }

    static Macro Synthetic(int n)
    {
        var ev = new List<MacroEvent>(n + 100);
        double t = 0;
        for (int i = 0; i < n; i++)
        {
            t += 4;
            int x = 100 + (i * 7) % 1700, y = 100 + (i * 13) % 900;
            ev.Add(new(t, EventKind.MouseMove, X: x, Y: y));
            if (i % 500 == 100) { ev.Add(new(t + 1, EventKind.ButtonDown, X: x, Y: y)); ev.Add(new(t + 2, EventKind.ButtonUp, X: x, Y: y)); }
            if (i % 700 == 200) { ev.Add(new(t + 1, EventKind.KeyDown, 0x41, 0x1E)); ev.Add(new(t + 3, EventKind.KeyUp, 0x41, 0x1E)); }
        }
        return new Macro { Name = "synthetic", Events = ev.Select((e, i) => e with { Id = i + 1 }).ToList(), Desktop = new DesktopInfo(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]) };
    }

    /// <summary>
    /// A real recording interleaves keys and clicks, so the raw snapshot order is not always sorted — equal timestamps come out
    /// of order. Load sorts before publishing, so the append check must verify the *sorted* snapshot, not the raw order: an
    /// unchanged snapshot must be accepted and must not republish the array (that is what made live ticks take the full path).
    /// </summary>
    [Fact]
    public void An_unsorted_but_identical_snapshot_still_takes_the_append_path()
    {
        var m = Synthetic(20_000);
        var snap = new Macro { Name = "Recording…", Desktop = m.Desktop, Events = m.Events.Select(e => e with { Id = 0 }).ToList() };
        var s = new EditSession();
        s.Load(snap, null);
        var before = s.Events;
        Assert.True(before.Zip(before.Skip(1)).All(p => p.First.T <= p.Second.T));   // published arrays are sorted
        var a1 = s.Analysis;
        for (int i = 0; i < 5; i++) s.Load(snap, null);
        Assert.Same(before, s.Events);        // verified identical: nothing new was published
        Assert.Same(a1, s.Analysis);          // and the derived-data cache stayed warm
    }

    /// <summary>The same sorted verification when the snapshot grew: the head must still equal the current events, then the tail lands in order.</summary>
    [Fact]
    public void A_grown_unsorted_snapshot_extends_through_the_sorted_path()
    {
        var m = Synthetic(20_000);
        var snap = new Macro { Name = "Recording…", Desktop = m.Desktop, Events = m.Events.Select(e => e with { Id = 0 }).ToList() };
        var s = new EditSession();
        s.Load(snap, null);
        int n = s.Events.Length;
        var tail = Enumerable.Range(0, 30).Select(k => new MacroEvent(s.Events[^1].T + k + 1, EventKind.MouseMove, X: k, Id: 0));
        s.Load(new Macro { Name = "Recording…", Desktop = m.Desktop, Events = snap.Events.Concat(tail).ToList() }, null);
        Assert.Equal(n + 30, s.Events.Length);
        Assert.True(s.Events.Zip(s.Events.Skip(1)).All(p => p.First.T <= p.Second.T));
        Assert.Equal(n + 30, s.Events.Select(e => e.Id).Distinct().Count());
    }

    /// <summary>Appends queue their tail and merge lazily: several appends cost one merge, the published array is always whole.</summary>
    [Fact]
    public void Queued_appends_materialize_once_and_stay_consistent()
    {
        var s = new EditSession();
        s.Load(Snap(1_000), null);
        Assert.True(s.Append(Enumerable.Range(1_000, 10).Select(i => new MacroEvent(i * 4.0, EventKind.MouseMove, X: i, Id: 0)).ToList()));
        Assert.True(s.Append(Enumerable.Range(1_010, 10).Select(i => new MacroEvent(i * 4.0, EventKind.MouseMove, X: i, Id: 0)).ToList()));
        var ev = s.Events;
        Assert.Equal(1_020, ev.Length);
        Assert.Equal(1_020, ev[^1].Id);
        Assert.Equal(Enumerable.Range(1, 1_020), ev.Select(e => e.Id));
        _ = s.PosIndex; _ = s.Analysis;       // derived data sees the merged array
        Assert.Same(ev, s.Events);            // merged once, stable afterwards
        Assert.True(s.Append(Enumerable.Range(1_020, 5).Select(i => new MacroEvent(i * 4.0, EventKind.MouseMove, X: i, Id: 0)).ToList()));
        Assert.Equal(1_025, s.Events.Length);
        Assert.NotSame(ev, s.Events);         // a second materialization produces a fresh array
    }

    /// <summary>The 250 ms live tick keeps a steady cadence: loading the same snapshot again must stay cheap (the head check is O(n), no copy).</summary>
    [Fact]
    public void Reloading_an_identical_snapshot_is_one_compare_not_a_copy()
    {
        var m = Snap(100_000);
        var s = new EditSession();
        s.Load(m, null);                                          // full path: numbers the events
        var t = new double[7];
        for (int i = 0; i < t.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            s.Load(m, null);
            t[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(t);
        o.WriteLine($"identical-snapshot loads (ms): {string.Join(" ", t.Select(x => x.ToString("F1")))}");
        Assert.True(t[t.Length / 2] < 25, $"identical snapshot reload took {t[t.Length / 2]:F1} ms");
        Assert.Equal(100_000, s.Events.Length);
        Assert.Equal(100_000, s.Events[^1].Id);
    }

    /// <summary>A snapshot that grew appends only the tail: ids assigned by position, order kept.</summary>
    [Fact]
    public void A_grown_snapshot_appends_the_tail()
    {
        var s = new EditSession();
        var first = Snap(1_000); s.Load(first, null);
        var grown = new Macro { Name = "Recording…", Events = first.Events.Concat(Enumerable.Range(0, 40).Select(k => Ev(1_000 + k) with { Id = 0 })).ToList() };
        Assert.True(s.Events.All(e => e.Id > 0));
        s.Load(grown, null);
        Assert.Equal(1_040, s.Events.Length);
        Assert.Equal(Enumerable.Range(1, 1_040), s.Events.Select(e => e.Id));
        Assert.True(s.Events.Zip(s.Events.Skip(1)).All(p => p.First.T <= p.Second.T));
    }

    /// <summary>An out-of-order tail still loads in order through the same path (insertion sort inside the new segment).</summary>
    [Fact]
    public void A_grown_snapshot_with_a_slightly_unsorted_tail_is_sorted()
    {
        var s = new EditSession();
        var first = Snap(1_000); s.Load(first, null);
        var tail = Enumerable.Range(0, 40).Select(k => Ev(1_000 + k) with { Id = 0 }).ToList();
        (tail[5], tail[20]) = (tail[20], tail[5]);                 // swap two events: still inside the tail, T out of order
        var grown = new Macro { Name = "Recording…", Events = first.Events.Concat(tail).ToList() };
        s.Load(grown, null);
        Assert.Equal(1_040, s.Events.Length);
        Assert.True(s.Events.Zip(s.Events.Skip(1)).All(p => p.First.T <= p.Second.T));
        Assert.Equal(1_040, s.Events.Select(e => e.Id).Distinct().Count());
    }

    /// <summary>A snapshot whose new events are older than the end (a take-back or re-sort) takes the full Load path and still lands right.</summary>
    [Fact]
    public void A_snapshot_that_inserts_into_the_head_takes_the_full_path()
    {
        var s = new EditSession();
        var first = Snap(1_000); s.Load(first, null);
        // a "new" event that belongs in the middle: TryAppend must refuse; the full path numbers everything fresh
        var grown = new Macro { Name = "Recording…", Events = first.Events.Concat([new MacroEvent(500.5, EventKind.KeyDown, 0x41)]).ToList() };
        s.Load(grown, null);
        Assert.Equal(1_001, s.Events.Length);
        Assert.True(s.Events.Zip(s.Events.Skip(1)).All(p => p.First.T <= p.Second.T));
        Assert.Equal(1_001, s.Events.Select(e => e.Id).Distinct().Count());
        var hit = s.Events.Select((e, i) => (e, i)).Single(x => x.e.Kind == EventKind.KeyDown);
        Assert.Equal(500.5, hit.e.T);
        Assert.True(hit.i == 0 || s.Events[hit.i - 1].T <= 500.5);          // it landed exactly where it belongs
        Assert.True(s.Events[hit.i + 1].T >= 500.5);
    }
}
