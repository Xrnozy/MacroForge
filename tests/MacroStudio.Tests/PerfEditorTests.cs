using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Storage;
using MacroStudio.Views;

namespace MacroStudio.Tests;

/// <summary>
/// Performance budgets for the editor, views and library. Budgets come from measurements on a dev box and have a wide margin:
/// a failure here means a real algorithmic problem (O(n) copy per edit, rescans per call), not scheduler noise.
/// Tests marked "RED" fail today on purpose; they pass once the fix named next to them lands.
/// </summary>
public class PerfEditorTests
{
    // ---- helpers ----

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

    static EditSession Session(int n) { var s = new EditSession(); s.Load(Synthetic(n), null); return s; }

    /// <summary>A mouse path that is smooth like a real recording (the path view skips sub-pixel steps).</summary>
    static EditSession SmoothSession(int n)
    {
        var sv = new List<MacroEvent>(n);
        for (int i = 0; i < n; i++) { double a = i / (double)n * Math.PI * 4; sv.Add(new(i * 4, EventKind.MouseMove, X: (int)(960 + 700 * Math.Sin(a)), Y: (int)(540 + 350 * Math.Sin(a * 1.5)), Id: i + 1)); }
        var s = new EditSession();
        s.Load(new Macro { Events = sv, Desktop = new DesktopInfo(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]) }, null);
        return s;
    }

    /// <summary>Median wall time (ms) of <paramref name="runs"/> runs after one warm-up.</summary>
    static double Median(Action a, int runs = 5)
    {
        a();
        var t = new double[runs];
        for (int i = 0; i < runs; i++) { var sw = Stopwatch.StartNew(); a(); t[i] = sw.Elapsed.TotalMilliseconds; }
        Array.Sort(t);
        return t[runs / 2];
    }

    static long Alloc(Action a)
    {
        a();
        GC.Collect(); GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static void Sta(Action a)
    {
        Exception? err = null;
        var th = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (err != null) throw new Exception("STA body failed", err);
    }

    static double Render(FrameworkElement v, double w, double h)
    {
        v.Measure(new Size(w, h)); v.Arrange(new Rect(0, 0, w, h)); v.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var sw = Stopwatch.StartNew();
        bmp.Render(v);
        return sw.Elapsed.TotalMilliseconds;
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "perfed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // Numbers land in %TEMP%\perfed.log as well as in assertion messages (handy when tuning budgets).
    static void Log(string m) => File.AppendAllText(Path.Combine(Path.GetTempPath(), "perfed.log"), m + Environment.NewLine);

    // ================= storage and load =================

    [Fact]
    public void Load_select_copy_100k_events_stay_interactive()
    {
        var m = Synthetic(100_000);
        string json = MacroSerializer.Serialize(m);
        Assert.True(json.Length < 100 * m.Events.Count, $"file too fat: {json.Length / (double)m.Events.Count:0} bytes/event");
        double parse = Median(() => MacroSerializer.Deserialize(json), 3);          // measured ~160 ms
        var s = new EditSession();
        double load = Median(() => s.Load(m, null), 3);                             // ~55 ms
        var sel = s.Events.Take(50_000).Select(e => e.Id).ToList();
        double select = Median(() => { s.ClearSelection(); s.Select(sel); }, 3);   // ~10 ms
        double copy = Median(() => s.CopyText(sel), 3);                             // ~40 ms
        string txt = s.CopyText(sel);
        double paste = Median(() => s.PasteText(txt, 1000), 3);                     // ~200 ms
        Log($"load/select/copy: parse {parse:0} load {load:0} select {select:0} copy {copy:0} paste {paste:0}");
        Assert.True(parse < 900, $"deserialize 100k: {parse:0} ms");
        Assert.True(load < 200, $"EditSession.Load 100k: {load:0} ms");
        Assert.True(select < 100, $"select 50k: {select:0} ms");
        Assert.True(copy < 300, $"copy 50k: {copy:0} ms");
        Assert.True(paste < 600, $"paste 50k: {paste:0} ms");
    }

    // Green guard (one cold run measured 430 ms, warm runs ~145 ms): per-event Serialize + WriteRawValue is allocation heavy but fine at 100k.
    [Fact]
    public void Saving_100k_events_is_not_slower_than_loading_them()
    {
        var m = Synthetic(100_000);
        string json = "";
        double ser = Median(() => json = MacroSerializer.Serialize(m), 5);           // measured ~145 ms warm
        double de = Median(() => MacroSerializer.Deserialize(json), 5);              // ~160 ms
        Log($"serialize 100k {ser:0} ms, deserialize {de:0} ms");
        Assert.True(ser < 400, $"Serialize 100k events took {ser:0} ms (deserialize {de:0} ms)");
    }

    // ================= editing =================

    [Fact]
    public void Single_event_edit_on_100k_is_one_array_copy()
    {
        var s = Session(100_000);
        int id = s.Events[50_000].Id;
        double modify = Median(() => s.Modify([id], e => e with { X = e.X + 1 }));                 // measured ~4 ms
        double drag = Median(() => s.Modify([id], e => e with { X = e.X + 1 }, "drag"));           // ~2 ms
        long alloc = Alloc(() => s.Modify([id], e => e with { X = e.X + 1 }));                     // ~7.2 MB = the clone
        double delUndo = Median(() => { s.Delete([s.Events[5].Id]); s.Undo(); });                  // ~10 ms
        double an = Median(() => Analysis.Of(s.Events, s.Desktop));                                // ~2 ms
        Log($"edit100k: modify {modify:0.0} drag {drag:0.0} alloc {alloc / 1e6:0.0} MB delete+undo {delUndo:0.0} analysis {an:0.0}");
        Assert.True(modify < 30, $"Modify {modify:0.0} ms");
        Assert.True(drag < 30, $"coalesced Modify {drag:0.0} ms");
        Assert.True(alloc < 12_000_000, $"Modify allocated {alloc / 1e6:0.0} MB for one event");
        Assert.True(delUndo < 60, $"Delete+Undo {delUndo:0.0} ms");
        Assert.True(an < 20, $"Analysis.Of {an:0.0} ms");
    }

    // RED: InsertBlock (also paste/duplicate) builds Select().Concat().ToArray() of unknown length, so the array grows by doubling through the LOH, then Sorted() scans again.
    // Fix: allocate new MacroEvent[_ev.Length + added.Length] once and copy (shift in place when rippling) instead of LINQ.
    [Fact]
    public void Inserting_one_event_into_100k_is_cheap()
    {
        var s = Session(100_000);
        var one = new[] { new MacroEvent(0, EventKind.KeyDown, 0x42) };
        double ins = Median(() => { s.InsertBlock(one, 5000, ripple: true); s.Undo(); });   // measured ~65 ms incl. undo
        Log($"insert+undo 100k: {ins:0.0} ms");
        Assert.True(ins < 35, $"InsertBlock+Undo on 100k events: {ins:0.0} ms");
    }

    // RED: the undo stack keeps up to 2,000,000 events of history (cap = 2M / length, min 5) = ~144 MB of 72-byte structs for a 100k macro, ~72 MB even at 10k.
    // Fix: cap by bytes (e.g. 500k events in total, min 2 steps) or store only the changed ranges.
    [Fact]
    public void Undo_history_memory_is_bounded()
    {
        var s = Session(100_000);
        GC.Collect(); long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 60; i++) { int id = s.Events[1000 + i].Id; s.Modify([id], e => e with { X = e.X + 1 }); }
        long grown = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(s);
        Log($"undo history 60 edits on 100k: +{grown / 1e6:0} MB");
        Assert.True(grown < 60_000_000, $"undo history holds {grown / 1e6:0} MB after 60 edits of a 100k-event macro");
    }

    // RED: the 250 ms recording timer calls EditSession.Load with the whole snapshot; at 100k events that costs ~70-90 ms (a third of the UI thread) and over 250 ms at 500k.
    // Fix: an append-only fast path when the snapshot extends the current array (skip the id check, sort check, Max and the Selected/Expanded resets).
    [Fact]
    public void Recording_live_reload_of_100k_events_fits_the_250ms_tick()
    {
        var m = Synthetic(100_000);
        var snap = new Macro { Name = "Recording...", Desktop = m.Desktop, Events = m.Events.Select(e => e with { Id = 0 }).ToList() };   // a live snapshot carries no ids
        var s = new EditSession();
        double t = Median(() => { s.Load(snap, null); _ = s.Analysis; _ = s.PosIndex; }, 7);   // measured ~90 ms
        Log($"live reload 100k: {t:0.0} ms");
        Assert.True(t < 35, $"live reload of 100k events: {t:0.0} ms per 250 ms tick");
    }

    // ================= views =================

    [Fact]
    public void Views_render_and_rebuild_100k_events_fast()
    {
        Sta(() =>
        {
            var s = SmoothSession(100_000);
            var path = new PathView(s); var tl = new TimelineView(s); var grid = new EventGridView(s) { HideMoves = true };
            double p = Median(() => Render(path, 900, 500));            // measured ~15 ms
            double t = Median(() => Render(tl, 1100, 260));             // ~11 ms
            double g = Median(() => Render(grid, 1100, 400));           // ~9 ms
            int id = s.Events[50_000].Id;
            double edit = Median(() => s.Modify([id], e => e with { X = e.X + 1 }));   // three views rebuild; the HideMoves grid Rebuild is ~5 ms
            double editDraw = Median(() => { s.Modify([id], e => e with { X = e.X + 1 }); Render(path, 900, 500); Render(tl, 1100, 260); Render(grid, 1100, 400); }, 3);   // ~80 ms
            Log($"views smooth 100k: path {p:0.0} tl {t:0.0} grid {g:0.0} edit {edit:0.0} edit+draw {editDraw:0.0}");
            Assert.True(p < 150, $"PathView {p:0} ms");
            Assert.True(t < 100, $"TimelineView {t:0} ms");
            Assert.True(g < 60, $"EventGridView {g:0} ms");
            Assert.True(edit < 60, $"edit with 3 views attached (HideMoves grid) {edit:0} ms");
            Assert.True(editDraw < 400, $"edit + redraw of all views {editDraw:0} ms");
        });
    }

    // RED: every edit/drag step calls Rebuild(), which re-lists every opened When block's connected macro and formats up to 500 child rows of text eagerly
    // (100 blocks = 70k strings, ~250 ms per edit). Fix: keep Row(Owner, Kind, ChildIndex) and format ChildCells lazily in OnRender for the ~20 visible rows.
    [Fact]
    public void Grid_with_many_open_when_blocks_rebuilds_cheaply_on_edit()
    {
        Sta(() =>
        {
            var ev = Synthetic(20_000).Events.ToList();
            for (int i = 0; i < 100; i++) ev.Insert(i * 150, new MacroEvent(ev[i * 150].T + 0.1, EventKind.When, Target: "t", Flow: new FlowOptions { Macro = "fix" }));
            var s = new EditSession();
            s.Load(new Macro { Name = "w", Events = ev.Select((e, i) => e with { Id = i + 1 }).ToList() }, null);
            var child = Synthetic(2000).Events.ToArray();
            var grid = new EventGridView(s, k => k == "fix" ? child : null);
            foreach (var e in s.Events.Where(e => e.IsWhen())) s.Expanded.Add(e.Id);
            grid.Invalidate();
            Assert.True(grid.RowCount > 60_000);
            int id = s.Events[5000].Id;
            double edit = Median(() => s.Modify([id], e => e with { X = e.X + 1 }), 5);   // measured ~250 ms
            Log($"grid 100 open When blocks, edit: {edit:0.0} ms");
            Assert.True(edit < 60, $"one edit with 100 open When blocks: {edit:0} ms (each drag step pays this)");
        });
    }

    // ================= library =================

    /// <summary>20 folder macros with 9 filed macros each = 200 files.</summary>
    static MacroLibrary BigLibrary(string dir)
    {
        for (int f = 0; f < 20; f++)
        {
            var subs = new List<string>();
            for (int k = 0; k < 9; k++)
            {
                var m = Synthetic(50); m.Id = $"s{f}_{k}"; m.Name = m.Id;
                MacroSerializer.Save(m, Path.Combine(dir, m.Id + MacroLibrary.Ext)); subs.Add(m.Id);
            }
            var fm = Synthetic(50); fm.Id = $"f{f}"; fm.Name = fm.Id; fm.Subs = subs;
            MacroSerializer.Save(fm, Path.Combine(dir, fm.Id + MacroLibrary.Ext));
        }
        return new MacroLibrary(dir);
    }

    // RED: ListTree resolves each filed macro with PathOfId, which enumerates the folder and stats every file (File.GetLastWriteTimeUtc in IdOf) per kid:
    // 180 kids x 200 files = ~36k stats even when everything is cached (measured ~2.8 s warm, 4.4 s cold for 200 macros; RefreshLibrary runs this on the UI thread after every save, move and delete).
    // Fix: build one id->path map per ListTree/Contains/AncestorIds call (one enumeration, one stat per file) and look kids up in it.
    [Fact]
    public void ListTree_of_200_macros_costs_about_one_directory_scan()
    {
        var dir = TempDir();
        try
        {
            var lib = BigLibrary(dir);
            var open = new HashSet<string>(Enumerable.Range(0, 20).Select(f => Path.Combine(dir, $"f{f}{MacroLibrary.Ext}")));
            double list = Median(() => lib.List(), 5);                     // one scan, ~30 ms here (disk/AV dependent, so the budget is relative)
            double tree = Median(() => lib.ListTree(open), 3);             // measured ~2800 ms
            Log($"library 200: List {list:0} ms, ListTree {tree:0} ms");
            Assert.True(tree < 8 * list + 100, $"ListTree {tree:0} ms vs one directory scan {list:0} ms");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Green guard: once read, the library cache answers from the stored result and never touches file contents again.
    // Every file is locked exclusively after the first call; a cache miss would fail to read it and change the result.
    [Fact]
    public void Library_caches_file_contents_after_the_first_call()
    {
        var dir = TempDir();
        var locks = new List<FileStream>();
        try
        {
            var lib = BigLibrary(dir);
            var open = new HashSet<string>(Enumerable.Range(0, 20).Select(f => Path.Combine(dir, $"f{f}{MacroLibrary.Ext}")));
            string Sig() => string.Join("|", lib.ListTree(open).Select(r => $"{r.Name}:{r.Depth}:{r.Kids}"));
            string first = Sig();
            Assert.Equal(20 + 180, first.Split('|').Length);
            foreach (var f in Directory.GetFiles(dir)) locks.Add(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None));
            Assert.Equal(first, Sig());
            Assert.NotNull(lib.PathOfId("s19_8"));
            Assert.NotNull(lib.FolderOf(Path.Combine(dir, "s7_3" + MacroLibrary.Ext)));
        }
        finally { foreach (var l in locks) l.Dispose(); Directory.Delete(dir, true); }
    }
}
