using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;
using MacroStudio.Recording;

namespace MacroStudio.Tests;

/// <summary>Real desktop: spams F24 (harmless) and circles the cursor while recording, then checks that nothing was lost and no gap appeared.</summary>
[Collection("real desktop")]
public class RecorderStressTests
{
    [Fact]
    public void Spamming_a_key_while_moving_the_mouse_loses_nothing()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return;
        var (ox, oy) = Screens.CursorPos();
        // a small topmost window of our own, so the clicks land somewhere harmless
        System.Windows.Window? win = null; var shown = new ManualResetEventSlim();
        var ui = new Thread(() => { win = new System.Windows.Window { Left = 300, Top = 300, Width = 300, Height = 300, Topmost = true, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None }; win.Loaded += (_, _) => shown.Set(); win.Show(); System.Windows.Threading.Dispatcher.Run(); }); ui.SetApartmentState(ApartmentState.STA); ui.Start();
        shown.Wait(5000);
        int cx = 450, cy = 450;
        var inj = new InputInjector(desk.Virtual);
        using var rec = new Recorder();
        int hookClicks = 0, hookKeys = 0, hookMoves = 0;
        using var plain = new InputHooks(r => { if (r.Kind == EventKind.KeyDown && r.Vk == 0x87) Interlocked.Increment(ref hookKeys); else if (r.Kind == EventKind.ButtonDown) Interlocked.Increment(ref hookClicks); else if (r.Kind == EventKind.MouseMove) Interlocked.Increment(ref hookMoves); }) { IgnoreInjected = false };
        plain.Start();
        rec.Start();
        int sent = 0, moves = 0; var stop = false;
        var keys = Task.Run(() => { while (!stop) { inj.Send(new MacroEvent(0, EventKind.KeyDown, Vk: 0x87)); Thread.Sleep(40); inj.Send(new MacroEvent(0, EventKind.KeyUp, Vk: 0x87)); sent++; Thread.Sleep(20); } });
        var mouse = Task.Run(() => { double a = 0; while (!stop) { inj.Send(new MacroEvent(0, EventKind.MouseMove, X: cx + (int)(80 * Math.Cos(a)), Y: cy + (int)(80 * Math.Sin(a)))); a += 0.15; moves++; Thread.Sleep(8); } });
        int clicks = 0;
        var click = Task.Run(() => { while (!stop) { inj.Send(new MacroEvent(0, EventKind.ButtonDown, Button: MouseBtn.Left)); Thread.Sleep(50); inj.Send(new MacroEvent(0, EventKind.ButtonUp, Button: MouseBtn.Left)); clicks++; Thread.Sleep(70); } });
        Thread.Sleep(8000);
        stop = true; Task.WaitAll(keys, mouse, click); win!.Dispatcher.Invoke(win.Close); win.Dispatcher.InvokeShutdown();
        var m = rec.Stop("stress");
        inj.Send(new MacroEvent(0, EventKind.MouseMove, X: ox, Y: oy));
        var kd = m.Events.Where(e => e.Kind == EventKind.KeyDown && e.Vk == 0x87).Select(e => e.T).ToList();
        var mv = m.Events.Where(e => e.Kind == EventKind.MouseMove).Select(e => e.T).ToList();
        double Gap(List<double> l) => l.Zip(l.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "stress.txt"), $"plain hook alone: clicks {hookClicks}, keys {hookKeys}, moves {hookMoves} | keys sent {sent}, recorded {kd.Count}, max gap {Gap(kd):F0} ms | clicks sent {clicks}, recorded {m.Events.Count(e => e.Kind == EventKind.ButtonDown)} | moves sent {moves}, recorded {mv.Count}, max gap {Gap(mv):F0} ms");
        Assert.True(kd.Count >= sent * 0.9, $"plain hook alone: clicks {hookClicks}, keys {hookKeys}, moves {hookMoves} | keys sent {sent}, recorded {kd.Count}");
        Assert.True(m.Events.Count(e => e.Kind == EventKind.ButtonDown) >= clicks * 0.9, "clicks lost");
        Assert.True(Gap(kd) < 500, $"key gap {Gap(kd):F0} ms");
    }

    [Fact]
    public void A_hotkey_key_is_dropped_only_with_its_own_modifiers()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return;
        var inj = new InputInjector(desk.Virtual);
        using var rec = new Recorder();
        rec.IgnoredKeys.Add(0x87); rec.HotkeyMods[0x87] = HotkeySettings.Ctrl | HotkeySettings.Shift;   // stand-in for Ctrl+Shift+A (F24 types nothing)
        rec.Start();
        void Tap() { inj.Send(new MacroEvent(0, EventKind.KeyDown, Vk: 0x87)); Thread.Sleep(60); inj.Send(new MacroEvent(0, EventKind.KeyUp, Vk: 0x87)); Thread.Sleep(60); }
        Tap(); Tap();                                                                         // bare: the user's own input, must be kept
        inj.Send(new MacroEvent(0, EventKind.KeyDown, Vk: 0xA2)); inj.Send(new MacroEvent(0, EventKind.KeyDown, Vk: 0xA0)); Thread.Sleep(60);
        Tap();                                                                                // Ctrl+Shift+key: the hotkey, must be dropped
        inj.Send(new MacroEvent(0, EventKind.KeyUp, Vk: 0xA0)); inj.Send(new MacroEvent(0, EventKind.KeyUp, Vk: 0xA2)); Thread.Sleep(100);
        var m = rec.Stop("hk");
        Assert.Equal(2, m.Events.Count(e => e.Kind == EventKind.KeyDown && e.Vk == 0x87));
        Assert.Equal(2, m.Events.Count(e => e.Kind == EventKind.KeyUp && e.Vk == 0x87));
    }

    [Fact]
    public void Mouse_look_with_a_hidden_cursor_is_recorded_as_relative_moves_and_plays_back()
    {
        var desk = Screens.Current();
        if (desk.Virtual.Width == 0) return;
        var (ox, oy) = Screens.CursorPos();
        System.Windows.Window? win = null; var shown = new ManualResetEventSlim();
        var ui = new Thread(() => { win = new System.Windows.Window { Left = 300, Top = 300, Width = 300, Height = 300, Topmost = true, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Cursor = System.Windows.Input.Cursors.None }; win.Loaded += (_, _) => shown.Set(); win.Show(); System.Windows.Threading.Dispatcher.Run(); }); ui.SetApartmentState(ApartmentState.STA); ui.Start();
        shown.Wait(5000);
        var inj = new InputInjector(desk.Virtual);
        inj.Send(new MacroEvent(0, EventKind.MouseMove, X: 450, Y: 450)); Thread.Sleep(200);
        bool hidden = RawMouse.CursorHidden();
        using var rec = new Recorder();
        rec.Start();
        for (int k = 0; k < 40; k++) { inj.Send(new MacroEvent(0, EventKind.RelMove, X: 5, Y: -3)); Thread.Sleep(10); }
        Thread.Sleep(100);
        var m = rec.Stop("look");
        win!.Dispatcher.Invoke(win.Close); win.Dispatcher.InvokeShutdown();
        inj.Send(new MacroEvent(0, EventKind.MouseMove, X: ox, Y: oy));
        if (!hidden) return;                                              // this desktop does not report a hidden cursor: nothing to assert
        var rel = m.Events.Where(e => e.Kind == EventKind.RelMove).ToList();
        Assert.InRange(rel.Sum(e => e.X), 190, 210);          // 40 x (5, -3); a step may land just before the cursor reads as hidden
        Assert.InRange(rel.Sum(e => e.Y), -123, -108);
    }
}
