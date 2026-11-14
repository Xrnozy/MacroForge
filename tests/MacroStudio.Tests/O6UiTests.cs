using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Views;

namespace MacroStudio.Tests;

/// <summary>O6: geometry-caching in the views, screen fallback, and the hotkey-combination rules.</summary>
public class O6UiTests
{
    static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw new Exception("O6 test failed", err);
    }

    static void Render(FrameworkElement v, double w, double h)
    {
        v.Measure(new Size(w, h)); v.Arrange(new Rect(0, 0, w, h)); v.UpdateLayout();
        new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32).Render(v);
    }

    static Macro Synthetic(int moves)
    {
        var ev = new List<MacroEvent>();
        double t = 0;
        for (int i = 0; i < moves; i++)
        {
            t += 4;
            ev.Add(new(t, EventKind.MouseMove, X: (i * 37) % 1920, Y: (i * 53) % 1080));
            if (i % 100 == 0) { ev.Add(new(t + 1, EventKind.ButtonDown, X: (i * 37) % 1920, Y: (i * 53) % 1080)); ev.Add(new(t + 60, EventKind.ButtonUp, X: (i * 37) % 1920, Y: (i * 53) % 1080)); }
            if (i % 200 == 0) ev.Add(new(t + 1, EventKind.Wheel, X: 500, Y: 500, Delta: -120));
        }
        return new Macro { Name = "o6", Events = ev.OrderBy(e => e.T).Select((e, i) => e with { Id = i + 1 }).ToList(), Desktop = new DesktopInfo(new ScreenRect(0, 0, 1920, 1080), [new ScreenRect(0, 0, 1920, 1080)]) };
    }

    [Fact]
    public void PathView_rebuilds_geometry_only_when_events_or_transform_change()
    {
        Sta(() =>
        {
            var s = new EditSession();
            s.Load(Synthetic(20_000), null);
            var v = new PathView(s);
            Render(v, 900, 500); Render(v, 900, 500);
            Assert.Equal(1, v.CacheBuilds);                          // one build covers both renders

            s.SetPlayhead(1234);                                     // playhead ticks ~30x/second during playback
            Render(v, 900, 500); Render(v, 900, 500);
            Assert.Equal(1, v.CacheBuilds);                          // the path is reused, not rebuilt per tick

            s.SelectOne(s.Events[5].Id);                             // selection may refit the view once
            int after = v.CacheBuilds;
            Render(v, 900, 500); Render(v, 900, 500);
            Assert.Equal(after, v.CacheBuilds);                      // and then stays cached again

            s.Modify([s.Events[0].Id], e => e with { X = e.X + 40 }, "test");   // an edit must invalidate
            Render(v, 900, 500);
            Assert.Equal(after + 1, v.CacheBuilds);
        });
    }

    [Fact]
    public void Screens_always_reports_at_least_one_monitor()
    {
        // EnumDisplayMonitors can yield nothing in headless / RDP sessions: Current() falls back to the
        // virtual screen so RegionPicker's windows[0] and overlays never index an empty list.
        var d = Screens.Current();
        Assert.True(d.Monitors.Count >= 1, "at least the virtual-screen fallback monitor must exist");
        Assert.True(d.Virtual.Width > 0 && d.Virtual.Height > 0);
        foreach (var m in d.Monitors)
        {
            Assert.True(m.X >= d.Virtual.X && m.Y >= d.Virtual.Y, "monitor must be inside the virtual desktop");
            Assert.True(m.X + m.Width <= d.Virtual.X + d.Virtual.Width && m.Y + m.Height <= d.Virtual.Y + d.Virtual.Height);
        }
        Assert.True(Screens.MonitorDpis().Count >= 1);
    }

    [Fact]
    public void Hotkey_rules_reject_keys_that_would_steal_typing()
    {
        Assert.False(HotkeySettings.Allowed(new Combo(0, 0x41)));            // bare A would take the letter from every program
        Assert.False(HotkeySettings.Allowed(new Combo(0, 0x20)));            // bare Space
        Assert.False(HotkeySettings.Allowed(new Combo(0, 0x0D)));            // bare Enter
        Assert.False(HotkeySettings.Allowed(new Combo(2, 0x10)));            // Ctrl+Shift itself: a modifier is not a key
        Assert.True(HotkeySettings.Allowed(new Combo(0, 0x78)));             // F9 (the default Record key)
        Assert.True(HotkeySettings.Allowed(new Combo(0, 0x2E)));             // Delete
        Assert.True(HotkeySettings.Allowed(new Combo(2, 0x41)));             // Ctrl+A is fine
        Assert.True(HotkeySettings.Allowed(new Combo(4, 0x41)));             // Shift+A works but…
        Assert.True(HotkeySettings.StealsTyping(new Combo(4, 0x41)));        // …it blocks Shift+A typing elsewhere
        Assert.False(HotkeySettings.StealsTyping(new Combo(2, 0x41)));
        Assert.False(HotkeySettings.StealsTyping(new Combo(0, 0x78)));
    }

    [Fact]
    public void Hotkey_conflicts_and_fallbacks_are_reported()
    {
        var s = new HotkeySettings();
        s.Set("record", new Combo(2, 0x52));                                  // Ctrl+R
        Assert.Equal("Record", s.Owner(new Combo(2, 0x52), "play"));          // another action wants the same combo
        Assert.Null(s.Owner(new Combo(2, 0x52), "record"));                   // the owner itself is not a conflict
        Assert.Null(s.Owner(new Combo(8, 0x52), "record"));                   // different combo: no conflict
        s.Set("play", new Combo(0, 0x41));                                    // invalid stored combo (bare A)
        Assert.Equal(new Combo(0, 0x79), s.Get("play"));                      // falls back to F10 instead of stealing the A key
    }
}
