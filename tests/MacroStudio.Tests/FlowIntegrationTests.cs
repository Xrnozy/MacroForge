using System.Diagnostics;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Storage;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>The glue around nested macros: per-run library snapshot and cache, nested targets, attributed log, and no leaks between runs.</summary>
public class FlowIntegrationTests
{
    static MacroEvent Mark(double t, int x) => new(t, EventKind.MouseMove, X: x, Y: 0);
    static MacroEvent When(double t, string? macro, string target = "pop") => new(t, EventKind.When, Target: target, Flow: new FlowOptions { Macro = macro, WaitMs = 100 }, Step: new StepOptions());
    static MacroEvent Click(double t, string target, StepOptions? o = null) => new(t, EventKind.TargetClick, Target: target, Step: o ?? new StepOptions());
    static string P(string name) => Path.Combine(Path.GetTempPath(), "ms-nonexistent", name + MacroLibrary.Ext);

    /// <summary>A fake library: id -> macro, files that do not exist on disk (reads go through the injected function).</summary>
    sealed class Fake
    {
        public Dictionary<string, Macro> Macros = new();
        public Dictionary<string, int> Reads = new();
        public ExecutionLog Log = new();
        public Fake Add(string id, string name, params MacroEvent[] evs) { Macros[id] = new Macro { Id = id, Name = name, Events = evs.ToList() }; return this; }
        public PlaybackSession Session(Func<string, bool>? unsaved = null, long maxBytes = PlaybackSession.DefaultMaxBytes)
        {
            var paths = Macros.ToDictionary(k => k.Key, k => P(k.Value.Name));
            var byPath = Macros.ToDictionary(k => P(k.Value.Name), k => k.Value);
            return new PlaybackSession(paths, Log, unsaved, p => { Reads[p] = Reads.GetValueOrDefault(p) + 1; return byPath[p]; }, maxBytes);
        }
    }

    // ---------- per-run cache ----------

    [Fact]
    public void A_macro_is_read_once_per_run_however_often_it_is_asked_for()
    {
        var f = new Fake().Add("a", "A", Mark(0, 1));
        using var s = f.Session();
        for (int i = 0; i < 100; i++) Assert.NotNull(s.Events("a"));
        Assert.Equal(1, f.Reads.Values.Sum());
        Assert.Same(s.Events("a"), s.Events("a"));
    }

    [Fact]
    public void Disposing_the_session_releases_everything()
    {
        var f = new Fake().Add("a", "A", Mark(0, 1));
        var s = f.Session(); var evs = s.Events("a")!;
        Assert.Equal("A", s.SourceOf(evs).Name);
        s.Dispose();
        Assert.Null(s.Events("a")); Assert.Null(s.NameOf("a")); Assert.Null(s.SourceOf(evs).Source);
    }

    [Fact]
    public void Events_come_back_in_time_order()
    {
        var f = new Fake().Add("a", "A", Mark(20, 3), Mark(0, 1), Mark(10, 2));
        using var s = f.Session();
        Assert.Equal([1, 2, 3], s.Events("a")!.Select(e => e.X));
    }

    // ---------- loader robustness: never throws, always says why ----------

    [Fact]
    public void Missing_macro_gives_null_and_a_plain_log_line_once()
    {
        var f = new Fake();
        using var s = f.Session();
        Assert.Null(s.Events("gone")); Assert.Null(s.Events("gone")); Assert.Null(s.Events(""));
        var l = f.Log.Snapshot().Where(e => e.What == "library").ToList();
        Assert.Single(l); Assert.Contains("not in the library", l[0].Message);
    }

    [Fact]
    public void Corrupt_or_unreadable_file_is_skipped_and_logged()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var bad = Path.Combine(dir, "Broken" + MacroLibrary.Ext); File.WriteAllText(bad, "{ this is not json \"id\":\"x\"");
            var log = new ExecutionLog();
            using var s = new PlaybackSession(new Dictionary<string, string> { ["x"] = bad }, log);
            Assert.Null(s.Events("x"));
            var e = Assert.Single(log.Snapshot());
            Assert.Equal(LogResult.Failed, e.Result); Assert.Contains("Broken", e.Message); Assert.Contains("skipped", e.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void File_deleted_after_the_snapshot_is_reported_not_thrown()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            var path = lib.UniquePath("Gone"); MacroSerializer.Save(new Macro { Id = "g", Name = "Gone", Events = [Mark(0, 1)] }, path);
            var log = new ExecutionLog();
            using var s = PlaybackSession.For(lib, log);
            File.Delete(path);
            Assert.Null(s.Events("g"));
            Assert.Contains("deleted or renamed", Assert.Single(log.Snapshot()).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Very_large_file_is_refused_without_reading_it()
    {
        var f = new Fake().Add("a", "Big", Mark(0, 1));
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, "Big" + MacroLibrary.Ext); File.WriteAllBytes(path, new byte[4096]);
            int reads = 0; var log = new ExecutionLog();
            using var s = new PlaybackSession(new Dictionary<string, string> { ["a"] = path }, log, null, p => { reads++; return new Macro(); }, maxBytes: 1000);
            Assert.Null(s.Events("a")); Assert.Equal(0, reads);
            Assert.Contains("too large", log.Snapshot()[0].Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_macro_open_with_unsaved_edits_plays_the_saved_file_and_says_so()
    {
        var f = new Fake().Add("a", "Edited", Mark(0, 1));
        using var s = f.Session(unsaved: p => p == P("Edited"));
        Assert.NotNull(s.Events("a"));
        Assert.Contains("unsaved changes", Assert.Single(f.Log.Snapshot()).Message);
    }

    [Fact]
    public void A_macro_with_no_events_gives_an_empty_array()
    {
        var f = new Fake().Add("a", "Empty");
        using var s = f.Session();
        Assert.Empty(s.Events("a")!);
    }

    [Fact]
    public void A_file_without_an_id_is_not_reachable_but_does_not_break_the_map()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            MacroSerializer.Save(new Macro { Name = "NoId", Events = [Mark(0, 1)] }, lib.UniquePath("NoId"));
            MacroSerializer.Save(new Macro { Id = "k", Name = "Has", Events = [Mark(0, 2)] }, lib.UniquePath("Has"));
            using var s = PlaybackSession.For(lib);
            Assert.NotNull(s.Events("k")); Assert.Null(s.Events("NoId")); Assert.Equal("Has", s.NameOf("k"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- nested targets ----------

    [Fact]
    public void Used_targets_cover_every_level_verify_and_recovery_targets_and_survive_cycles()
    {
        var f = new Fake()
            .Add("A", "A", Click(0, "tA"), When(10, "B"))
            .Add("B", "B", Click(0, "tB", new StepOptions { VerifyTarget = "vB", RecoveryTarget = "rB" }), When(10, "A"))   // A -> B -> A: a cycle
            .Add("C", "C", Click(0, "tC"));
        using var s = f.Session();
        var used = s.UsedTargets([Click(0, "tMain", new StepOptions { VerifyTarget = "vMain" }), When(10, "A", "wMain")]);
        Assert.Equal(new[] { "tMain", "vMain", "wMain", "pop", "tA", "tB", "vB", "rB" }.OrderBy(x => x), used.OrderBy(x => x));
        Assert.Equal(1, f.Reads[P("A")]); Assert.Equal(1, f.Reads[P("B")]); Assert.DoesNotContain(P("C"), f.Reads.Keys);
    }

    [Fact]
    public void Nested_depth_is_limited_like_the_runner()
    {
        var f = new Fake();
        for (int i = 0; i < 12; i++) f.Add($"m{i}", $"M{i}", Click(0, $"t{i}"), When(10, $"m{i + 1}"));
        using var s = f.Session();
        var used = s.UsedTargets([When(0, "m0")]);
        Assert.Equal(PlaybackSession.MaxDepth + 1, used.Count);   // "pop" + the macros reached within the limit
    }

    [Fact]
    public void Main_to_A_to_B_finds_a_target_B_inherits_from_the_mains_folder()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            // the target "shared" belongs to Main (its folder): B is filed under A which is filed under Main, and only B clicks it
            string pb = lib.UniquePath("B"), pa = lib.UniquePath("A"), pm = lib.UniquePath("Main");
            MacroSerializer.Save(new Macro { Id = "b", Name = "B", Events = [Click(0, "shared")] }, pb);
            MacroSerializer.Save(new Macro { Id = "a", Name = "A", Events = [When(0, "b")] }, pa);
            MacroSerializer.Save(new Macro { Id = "m", Name = "Main", Events = [When(0, "a")], Subs = ["a"] }, pm);
            Assert.Null(lib.AddSub(pa, "b"));
            Assert.Contains("m", lib.AncestorIds(pb)); Assert.Contains("a", lib.AncestorIds(pb));
            using var s = PlaybackSession.For(lib);
            Assert.Contains("shared", s.UsedTargets(MacroSerializer.Load(pm).Events));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- the run itself, through Player + FlowRunner, with the session as the loader ----------

    sealed class Screen(FakeClock clock) : ITargetFinder
    {
        public string? NameOf(string id) => id == "pop" ? "Popup" : null;
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) { clock.Now += 10; return id == "pop" ? new Sighting(new Detection(new Rect(760, 380, 80, 40), 0.97, 1), (long)clock.Now, DateTime.UtcNow) : null; }
    }

    static (RecordingSink Out, ExecutionLog Log, Player P) Run(PlaybackSession s, MacroEvent[] main, FakeClock? clock = null)
    {
        clock ??= new FakeClock(); var sink = new RecordingSink(clock); var log = new ExecutionLog();
        var p = new Player(sink, clock) { Log = log };
        p.Flow = new FlowRunner(new Screen(clock), clock, () => p.StopRequested, sink, id => s.Events(id), (_, _) => (sink, sink), () => 1, log);
        p.Run(main, 0, main.Length, 1);
        return (sink, log, p);
    }

    [Fact]
    public void A_macro_called_a_hundred_times_is_read_once_and_a_missing_one_never_crashes_the_run()
    {
        var f = new Fake().Add("a", "A", Mark(0, 7));
        using var s = f.Session();
        var main = Enumerable.Range(0, 100).Select(i => When(i * 5.0, "a")).Append(When(600, "nope")).Append(Mark(700, 99)).ToArray();
        // each When block is its own index: MaxRuns (10) applies per block, so every block acts once
        var (sink, log, p) = Run(s, main);
        Assert.Null(p.Error);
        Assert.Equal(100, sink.Sent.Count(x => x.E.X == 7));
        Assert.Equal(99, sink.Sent.Last().E.X);
        Assert.Equal(1, f.Reads.Values.Sum());
        Assert.Contains(log.Snapshot(), e => e.Result == LogResult.Failed && e.Message.Contains("not found in the library"));
    }

    // ---------- attributed log ----------

    [Fact]
    public void Scoped_log_names_the_macro_shares_the_ring_and_stays_bounded()
    {
        var root = new ExecutionLog(); var a = root.Scoped("Player selection"); var b = a.Scoped("Other");
        root.Add(0, "click", LogResult.Ok, "main");
        a.Add(3, "click", LogResult.Warn, "nested");
        b.Add(1, "key", LogResult.Failed, "deeper");
        var l = root.Snapshot();
        Assert.Equal(["click", "[Player selection] click", "[Other] key"], l.Select(e => e.What));   // a scope of a scope still names ITS macro, not a chain
        Assert.Equal(1, root.Count(LogResult.Warn)); Assert.Equal(1, a.Count(LogResult.Failed));
        Assert.Equal(3, a.Total);
        for (int i = 0; i < ExecutionLog.Capacity * 3; i++) a.Add(i, "x", LogResult.Ok, "m");
        Assert.Equal(ExecutionLog.Capacity, root.Snapshot().Count);
        a.Clear(); Assert.Empty(root.Snapshot()); Assert.Equal(0, root.Total);
    }

    // ---------- library: thread safety and cache growth ----------

    [Fact]
    public void Library_lookups_survive_files_appearing_and_disappearing_on_other_threads()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            for (int i = 0; i < 10; i++) MacroSerializer.Save(new Macro { Id = "id" + i, Name = "M" + i, Events = [Mark(0, i)] }, lib.UniquePath("M" + i));
            using var stop = new CancellationTokenSource(); var errors = new List<Exception>();
            var churn = Task.Run(() => { int n = 0; while (!stop.IsCancellationRequested) { var p = lib.UniquePath("Tmp"); try { MacroSerializer.Save(new Macro { Id = "t" + n++, Name = "Tmp", Events = [Mark(0, 1)] }, p); File.Delete(p); } catch { } } });
            var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                try { for (int i = 0; i < 300; i++) { lib.ListTree(new HashSet<string>()); lib.IdMap(); lib.PathOfId("id3"); using var s = PlaybackSession.For(lib); s.Events("id5"); } }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToArray();
            Task.WaitAll(readers); stop.Cancel(); churn.Wait();
            Assert.Empty(errors);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Library_caches_forget_deleted_files()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            var paths = Enumerable.Range(0, 20).Select(i => { var p = lib.UniquePath("X" + i); MacroSerializer.Save(new Macro { Id = "x" + i, Name = "X" + i }, p); return p; }).ToList();
            lib.ListTree(new HashSet<string>());
            foreach (var p in paths) File.Delete(p);
            lib.ListTree(new HashSet<string>());
            var kids = (System.Collections.IDictionary)typeof(MacroLibrary).GetField("_kids", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lib)!;
            var ids = (System.Collections.IDictionary)typeof(MacroLibrary).GetField("_ids", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lib)!;
            Assert.Empty(kids); Assert.Empty(ids);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 50 runs leave nothing behind ----------

    [Fact]
    public void Fifty_runs_leave_no_threads_handles_or_state_behind()
    {
        var f = new Fake().Add("a", "A", Mark(0, 7), When(5, "b")).Add("b", "B", Mark(0, 8));
        void One()
        {
            f.Log.Clear();
            using var s = f.Session();
            var used = s.UsedTargets([When(0, "a")]);
            var (sink, log, p) = Run(s, [Mark(0, 1), When(10, "a"), Mark(20, 2)]);
            Assert.Null(p.Error); Assert.Equal([1, 7, 8, 2], sink.Sent.Select(x => x.E.X));
            Assert.Contains("pop", used);
        }
        One(); One();                                           // warm up: JIT, thread pool
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var pr = Process.GetCurrentProcess(); pr.Refresh();
        int threads = pr.Threads.Count, handles = pr.HandleCount; long mem = GC.GetTotalMemory(true);
        for (int i = 0; i < 50; i++) One();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        pr.Refresh();
        Assert.True(pr.Threads.Count <= threads + 3, $"threads {threads} -> {pr.Threads.Count}");
        Assert.True(pr.HandleCount <= handles + 20, $"handles {handles} -> {pr.HandleCount}");
        Assert.True(GC.GetTotalMemory(true) <= mem + 2_000_000, "memory grew across runs");
    }

    [Fact]
    public void PathOfId_follows_renames_deletes_and_new_files_even_with_its_remembered_answer()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ms-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var lib = new MacroLibrary(dir);
            var a = lib.UniquePath("A"); MacroSerializer.Save(new Macro { Id = "a", Name = "A", Events = [Mark(0, 1)] }, a);
            Assert.Equal(a, lib.PathOfId("a")); Assert.Equal(a, lib.PathOfId("a"));         // second answer comes from memory
            var b = lib.Rename(a, "B"); Assert.Equal(b, lib.PathOfId("a")); Assert.Equal("B", lib.NameOfId("a"));
            var c = lib.UniquePath("C"); MacroSerializer.Save(new Macro { Id = "c", Name = "C" }, c); Assert.Equal(c, lib.PathOfId("c"));   // created right after the last scan
            MacroSerializer.Save(new Macro { Id = "z", Name = "B", Events = [Mark(0, 1)] }, b);                                   // same file, new id
            Assert.Null(lib.PathOfId("a")); Assert.Equal(b, lib.PathOfId("z"));
            lib.Delete(b); Assert.Null(lib.PathOfId("z")); Assert.Null(lib.LoadById("z"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class FlowIntegrationPlanTests
{
    [Fact]
    public void Trajectory_plan_is_built_once_per_events_array_and_released_with_the_session()
    {
        var evs = new[] { new MacroEvent(0, EventKind.MouseMove, X: 1, Y: 1) };
        var s = new PlaybackSession(new Dictionary<string, string>());
        Assert.Same(s.PlanFor(evs), s.PlanFor(evs));
        s.Dispose();
        Assert.NotNull(s.PlanFor(evs));   // still usable, just not cached
    }
}
