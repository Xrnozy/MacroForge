using System.Threading.Channels;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Recording;

namespace MacroStudio.Tests;

/// <summary>
/// The recording consumer (channel → <see cref="Recorder.Store"/>) through the internal
/// <see cref="Recorder.ConsumeAsync"/> seam: hotkey modifier take-back and the drop rules, with no OS hooks
/// and no desktop — the same ground <see cref="RecorderStressTests.A_hotkey_key_is_dropped_only_with_its_own_modifiers"/>
/// needs a real desktop for.
/// </summary>
public class O1ConsumerSeamTests
{
    static RawInput Key(int i, EventKind kind, int vk) => new(i * 10, kind, vk, 0, false, 0, 0, default, 0);

    /// <summary>Feeds the inputs through the consumer the same way the recording task does, then returns what was stored.</summary>
    static async Task<List<MacroEvent>> Drive(Recorder rec, params RawInput[] inputs)
    {
        var ch = Channel.CreateUnbounded<RawInput>();
        foreach (var r in inputs) ch.Writer.TryWrite(r);
        ch.Writer.Complete();
        await rec.ConsumeAsync(ch.Reader, null);
        return rec.Snapshot();
    }

    [Fact]
    public async Task A_hotkey_press_takes_back_the_modifier_presses_that_led_into_it()
    {
        var rec = new Recorder();
        rec.HotkeyMods[0x87] = HotkeySettings.Ctrl | HotkeySettings.Shift;   // stand-in for Ctrl+Shift+A (F24 types nothing)
        var ev = await Drive(rec,
            Key(1, EventKind.KeyDown, 0xA2),   // LCtrl: stored when pressed
            Key(2, EventKind.KeyDown, 0xA0),   // LShift: stored when pressed
            Key(3, EventKind.KeyDown, 0x87),   // the hotkey: dropped, and the modifier presses go back out
            Key(4, EventKind.KeyUp, 0x87),     // its release goes too
            Key(5, EventKind.KeyUp, 0xA0),     // releases of taken-back presses are noise
            Key(6, EventKind.KeyUp, 0xA2));
        Assert.Empty(ev);
    }

    [Fact]
    public async Task The_same_key_without_the_full_combination_is_the_users_input_and_is_kept()
    {
        var rec = new Recorder();
        rec.HotkeyMods[0x87] = HotkeySettings.Ctrl | HotkeySettings.Shift;
        var ev = await Drive(rec,
            Key(1, EventKind.KeyDown, 0x87),                 // bare: kept
            Key(2, EventKind.KeyUp, 0x87),
            Key(3, EventKind.KeyDown, 0xA2),                 // Ctrl only, not Ctrl+Shift: the press below is kept
            Key(4, EventKind.KeyDown, 0x87),
            Key(5, EventKind.KeyUp, 0x87),
            Key(6, EventKind.KeyUp, 0xA2));
        Assert.Equal(4, ev.Count(e => e.Vk == 0x87));
        // the Ctrl press is the user's own key, recorded and released normally (no hotkey fired)
        Assert.Equal(2, ev.Count(e => e.Vk == 0xA2));
    }

    [Fact]
    public async Task Repeats_and_the_release_of_a_swallowed_hotkey_are_dropped_too()
    {
        var rec = new Recorder();
        rec.HotkeyMods[0x87] = HotkeySettings.Ctrl | HotkeySettings.Shift;
        var ev = await Drive(rec,
            Key(1, EventKind.KeyDown, 0xA2),
            Key(2, EventKind.KeyDown, 0xA0),
            Key(3, EventKind.KeyDown, 0x87),   // hotkey
            Key(4, EventKind.KeyDown, 0x87),   // auto-repeat while held: still the hotkey
            Key(5, EventKind.KeyUp, 0x87),
            Key(6, EventKind.KeyUp, 0xA0),
            Key(7, EventKind.KeyUp, 0xA2));
        Assert.Empty(ev);
    }

    [Fact]
    public async Task A_key_release_whose_press_was_never_seen_is_dropped()
    {
        var rec = new Recorder();
        var ev = await Drive(rec,
            Key(1, EventKind.KeyUp, 0x41),     // e.g. the release of the click that pressed Record
            Key(2, EventKind.KeyDown, 0x41),   // a real press after that still records (and its release does too)
            Key(3, EventKind.KeyUp, 0x41));
        Assert.Equal(2, ev.Count);
        Assert.Equal([EventKind.KeyDown, EventKind.KeyUp], ev.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_button_release_whose_press_was_never_seen_is_dropped()
    {
        var rec = new Recorder();
        var ev = await Drive(rec,
            new RawInput(10, EventKind.ButtonUp, 0, 0, false, 100, 200, MouseBtn.Left, 0),
            new RawInput(20, EventKind.ButtonDown, 0, 0, false, 100, 200, MouseBtn.Left, 0),
            new RawInput(30, EventKind.ButtonUp, 0, 0, false, 100, 200, MouseBtn.Left, 0));
        Assert.Equal(2, ev.Count);
        Assert.Equal([EventKind.ButtonDown, EventKind.ButtonUp], ev.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_taken_back_modifier_makes_the_next_live_snapshot_full()
    {
        var rec = new Recorder();
        rec.HotkeyMods[0x87] = HotkeySettings.Ctrl | HotkeySettings.Shift;
        var ch = Channel.CreateUnbounded<RawInput>();
        ch.Writer.TryWrite(Key(1, EventKind.KeyDown, 0xA2));
        var consume = rec.ConsumeAsync(ch.Reader, null);
        while (rec.Count < 1) Thread.Sleep(1);
        int epoch = 0; rec.SnapshotFrom(0, ref epoch, out _);            // the live view's remembered epoch
        ch.Writer.TryWrite(Key(2, EventKind.KeyDown, 0xA0));
        ch.Writer.TryWrite(Key(3, EventKind.KeyDown, 0x87));             // hotkey: takes back the modifier presses
        ch.Writer.Complete();
        await consume;
        var ev = rec.SnapshotFrom(1, ref epoch, out bool full);
        Assert.True(full);                                               // an earlier event vanished: the view must reload
        Assert.Empty(ev);
    }
}
