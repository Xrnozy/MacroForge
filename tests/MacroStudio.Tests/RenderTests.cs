using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Views;

namespace MacroStudio.Tests;

public class RenderTests
{
    static Macro Synthetic(int moves)
    {
        var ev = new List<MacroEvent>();
        double t = 0;
        for (int i = 0; i < moves; i++)
        {
            t += 4;
            double a = i / (double)moves * Math.PI * 4;
            ev.Add(new(t, EventKind.MouseMove, X: (int)(960 + 700 * Math.Sin(a)), Y: (int)(540 + 350 * Math.Sin(a * 1.5))));
            if (i == moves / 5) { ev.Add(new(t + 1, EventKind.ButtonDown, X: ev[^1].X, Y: ev[^1].Y)); ev.Add(new(t + 90, EventKind.ButtonUp, X: ev[^1].X, Y: ev[^1].Y)); }
            if (i == moves / 3) { ev.Add(new(t + 1, EventKind.KeyDown, 0x10, 0x2A)); ev.Add(new(t + 5, EventKind.KeyDown, 0x41, 0x1E)); ev.Add(new(t + 60, EventKind.KeyUp, 0x41, 0x1E)); ev.Add(new(t + 120, EventKind.KeyUp, 0x10, 0x2A)); }
            if (i == moves / 2) { ev.Add(new(t + 1, EventKind.Wheel, X: ev[^1].X, Y: ev[^1].Y, Delta: -120)); ev.Add(new(t + 2, EventKind.Wheel, X: ev[^1].X, Y: ev[^1].Y, Delta: -360)); ev.Add(new(t + 3, EventKind.HWheel, X: ev[^1].X, Y: ev[^1].Y, Delta: 120)); }
            if (i == moves * 3 / 4) ev.Add(new(t + 1, EventKind.ButtonDown, Button: MouseBtn.Right, X: ev[^1].X, Y: ev[^1].Y)); // never released
        }
        ev = ev.OrderBy(e => e.T).Select((e, i) => e with { Id = i + 1 }).ToList();
        return new Macro { Name = "synthetic", Events = ev, Desktop = new DesktopInfo(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]) };
    }

    static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err != null) throw new Exception("render failed", err);
    }

    static double Render(FrameworkElement v, double w, double h, string? png = null)
    {
        v.Measure(new Size(w, h)); v.Arrange(new Rect(0, 0, w, h)); v.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var sw = Stopwatch.StartNew();
        bmp.Render(v);
        sw.Stop();
        if (png != null)
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(png); enc.Save(f);
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Views_render_with_selection_and_report_speed()
    {
        var outDir = Environment.GetEnvironmentVariable("RENDER_OUT");
        Sta(() =>
        {
            var s = new EditSession();
            s.Load(Synthetic(2000), null);
            var path = new PathView(s); var tl = new TimelineView(s); var grid = new EventGridView(s);
            s.Select(s.Events.Where(e => e.Kind != EventKind.MouseMove).Select(e => e.Id).ToList());
            Render(path, 900, 500, outDir == null ? null : Path.Combine(outDir, "path.png"));
            Render(tl, 1100, 260, outDir == null ? null : Path.Combine(outDir, "timeline.png"));
            Render(grid, 1100, 300, outDir == null ? null : Path.Combine(outDir, "grid.png"));
        });
    }

    [Fact]
    public void Large_recording_renders_fast_enough()
    {
        Sta(() =>
        {
            var s = new EditSession();
            s.Load(Synthetic(150_000), null);
            var path = new PathView(s); var tl = new TimelineView(s);
            Render(path, 900, 500); Render(tl, 1100, 260); // warm-up (analysis, fonts)
            double p = Render(path, 900, 500), t = Render(tl, 1100, 260);
            Console.WriteLine($"150k events: path {p:0} ms, timeline {t:0} ms");
            Assert.True(p < 400 && t < 400, $"too slow: path {p} ms, timeline {t} ms");
        });
    }

    [Fact]
    public void Inspector_builds_and_refreshes()
    {
        Sta(() =>
        {
            var s = new EditSession();
            s.Load(Synthetic(100), null);
            var ins = new InspectorPanel(s);
            foreach (var e in s.Events.Where(e => e.Kind != EventKind.MouseMove)) { s.SelectOne(e.Id); Render(ins, 300, 600); }
        });
    }

    [Fact]
    public void Grid_lists_the_connected_macro_nested_under_its_when_block()
    {
        var outDir = Environment.GetEnvironmentVariable("RENDER_OUT") ?? Path.GetTempPath();
        EventInfo.MacroNameOf = id => id == "fix" ? "Rejoin game" : null;
        try
        {
            Sta(() =>
            {
                var m = Synthetic(40);
                var ev = m.Events.ToList();
                ev.Insert(3, new MacroEvent(ev[2].T + 0.5, EventKind.When, Target: "t", Flow: new FlowOptions { Macro = "fix", After = FlowAfter.Restart, Always = true }));
                ev.Insert(8, new MacroEvent(ev[7].T + 0.5, EventKind.When, Target: "t", Flow: new FlowOptions { Do = FlowDo.Restart }));
                ev.Insert(12, new MacroEvent(ev[11].T + 0.5, EventKind.When, Target: "t", Flow: new FlowOptions()));          // no macro yet: shows the drop hint
                m.Events = ev.Select((e, i) => e with { Id = i + 1 }).ToList();
                var s = new EditSession(); s.Load(m, null);
                var child = new[] { new MacroEvent(0, EventKind.MouseMove, X: 100, Y: 100), new MacroEvent(40, EventKind.ButtonDown, X: 100, Y: 100), new MacroEvent(120, EventKind.ButtonUp, X: 100, Y: 100) };
                var grid = new EventGridView(s, id => id == "fix" ? child : null);
                foreach (var e in s.Events.Where(e => e.IsWhen())) s.Expanded.Add(e.Id);
                s.SelectOne(s.Events.First(e => e.IsWhen()).Id);
                Render(grid, 1150, 420);
                Render(grid, 1150, 420, Path.Combine(outDir, "grid-nested.png"));     // the first pass lists the rows, the second draws them
                Assert.True(grid.RowCount == s.Events.Length + 5, $"rows {grid.RowCount} for {s.Events.Length} events: the connected macro and the drop hint must be listed");
                s.Expanded.Clear(); s.Expanded.Add(s.Events.First(e => e.IsWhen()).Id);
                grid.Invalidate();
                Render(grid, 1150, 420, Path.Combine(outDir, "grid-nested2.png"));
            });
        }
        finally { EventInfo.MacroNameOf = null; }
    }

    [Fact]
    public void Inspector_shows_the_when_card()
    {
        var outDir = Environment.GetEnvironmentVariable("RENDER_OUT") ?? Path.GetTempPath();
        EventInfo.MacroNameOf = id => id == "fix" ? "Rejoin game" : null;
        try
        {
            Sta(() =>
            {
                var s = new EditSession();
                s.Load(new Macro { Events = [new MacroEvent(0, EventKind.MouseMove, X: 5, Y: 5, Id: 1), new MacroEvent(10, EventKind.When, Target: "t", Id: 2, Flow: new FlowOptions { Macro = "fix", ClickFirst = true }, Step: new StepOptions())] }, null);
                s.SelectOne(2);
                var insp = new InspectorPanel(s, () => [("t", "Disconnected popup")], _ => null) { FolderMacros = () => [("fix", "Rejoin game"), ("other", "Close ad")] };
                s.SelectOne(1); s.SelectOne(2);                                    // the app sets FolderMacros after creating the panel; selecting refreshes it
                Render(insp, 360, 1500, Path.Combine(outDir, "inspector-when.png"));
            });
        }
        finally { EventInfo.MacroNameOf = null; }
    }
}
