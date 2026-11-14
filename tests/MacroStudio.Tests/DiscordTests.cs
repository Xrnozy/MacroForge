using System.Net.Http;
using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Vision;

namespace MacroStudio.Tests;

public class DiscordTests
{
    sealed class Fake : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Got = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Got.Add((r.RequestUri!.ToString(), await r.Content!.ReadAsStringAsync(ct))); return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent); }
    }
    sealed class Cap(List<MacroEvent> l) : IInputSink { public void Send(in MacroEvent e) => l.Add(e); }
    const string Hook = "https://discord.com/api/webhooks/123456789/abc_DEF-ghi";

    [Theory]
    [InlineData(Hook, true)]
    [InlineData("https://discordapp.com/api/webhooks/1/x", true)]
    [InlineData("http://discord.com/api/webhooks/1/x", false)]
    [InlineData("https://evil.example/api/webhooks/1/x", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/x", false)]
    [InlineData("https://discord.com/api/webhooks/1/x?foo=1", false)]
    public void Only_real_discord_webhook_addresses_are_accepted(string url, bool ok) => Assert.Equal(ok, DiscordWebhook.IsValid(url));

    [Fact]
    public async Task A_message_is_posted_as_json_with_mentions_switched_off_and_a_bad_address_sends_nothing()
    {
        var h = new Fake(); using var http = new HttpClient(h);
        Assert.Null(await DiscordWebhook.Post(Hook, "done @everyone", http));
        var (url, body) = Assert.Single(h.Got);
        Assert.Equal(Hook, url);
        Assert.Contains("\"content\":\"done @everyone\"", body);
        Assert.Contains("\"parse\":[]", body);
        Assert.NotNull(await DiscordWebhook.Post("https://evil.example/x", "hi", http));
        Assert.Single(h.Got);
    }

    [Fact]
    public async Task A_screenshot_is_sent_as_a_file_next_to_the_message()
    {
        var h = new Fake(); using var http = new HttpClient(h);
        Assert.Null(await DiscordWebhook.Post(Hook, "look", http, [1, 2, 3, 4]));
        var body = Assert.Single(h.Got).Body;
        Assert.Contains("payload_json", body);
        Assert.Contains("\"content\":\"look\"", body);
        Assert.Contains("filename=screenshot.jpg", body);
    }

    [Fact]
    public void A_discord_step_is_taken_by_the_sink_and_never_reaches_the_real_input()
    {
        var got = new List<MacroEvent>(); var log = new ExecutionLog();
        var sink = new DiscordSink(new Cap(got), () => null, log, () => 0);
        sink.Send(new MacroEvent(0, EventKind.Discord, Target: "hello"));
        sink.Send(new MacroEvent(10, EventKind.KeyDown, 0x41));
        Assert.Equal([EventKind.KeyDown], got.Select(e => e.Kind));
        Assert.False(new MacroEvent(0, EventKind.Discord, Target: "x").HasPos());
        Assert.Contains(log.Snapshot(), x => x.What == "discord" && x.Result == LogResult.Skipped);   // no webhook set: skipped, the macro goes on
    }

    const string HookA = "https://discord.com/api/webhooks/111/AAAA";
    const string HookB = "https://discord.com/api/webhooks/222/BBBB";

    [Fact]
    public void Each_recording_uses_its_own_link_and_falls_back_to_the_default()
    {
        var d = new DiscordSettings { Url = HookA };
        d.SetLink("rec1", HookB);
        Assert.Equal(HookB, d.UrlFor("rec1"));
        Assert.Equal(HookA, d.UrlFor("rec2"));      // no link of its own: the default
        Assert.Equal(HookA, d.UrlFor(null));        // an unsaved recording has no Id: the default
        d.CopyLink("rec1", "copy");
        d.SetLink("copy", null);
        Assert.Equal(HookB, d.UrlFor("rec1"));      // a copy's link can change without touching the original
        Assert.Equal(HookA, d.UrlFor("copy"));
    }

    [Fact]
    public void An_exported_recording_carries_no_webhook_and_no_recording_id_even_if_one_was_typed_into_a_message()
    {
        var m = new MacroStudio.Model.Macro { Id = "rec1", Name = "Daily " + HookA };
        m.Events.Add(new MacroEvent(0, EventKind.Discord, Target: "done, see " + HookB + " ok"));
        m.Events.Add(new MacroEvent(10, EventKind.KeyDown, 0x41));
        var json = MacroStudio.Storage.MacroSerializer.Serialize(DiscordWebhook.ForExport(m));
        Assert.DoesNotContain("discord.com/api/webhooks", json);
        Assert.DoesNotContain("AAAA", json); Assert.DoesNotContain("BBBB", json);
        Assert.DoesNotContain("rec1", json);
        Assert.Contains("[webhook removed]", json);
        Assert.Equal("rec1", m.Id);                 // the live recording is untouched
        Assert.Contains("\"id\": \"rec1\"", MacroStudio.Storage.MacroSerializer.Serialize(m));   // normal saves keep the Id (it is only a key, not a secret)
    }

    [Fact]
    public void A_masked_link_never_shows_the_secret_token()
    {
        var shown = DiscordWebhook.Mask("https://discord.com/api/webhooks/123456789/abc_DEF-ghi");
        Assert.Equal("discord.com/api/webhooks/123456789/••••••••", shown);
        Assert.DoesNotContain("abc_DEF", shown);
        Assert.Equal("", DiscordWebhook.Mask("not a link"));
        Assert.Equal("", DiscordWebhook.Mask(null));
    }
}

public class ClickThenDiscordTests
{
    sealed class Cap(List<MacroEvent> l) : IInputSink { public void Send(in MacroEvent e) => l.Add(e); }
    sealed class Finder : ITargetFinder
    {
        public string? NameOf(string id) => "T"; public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) => new Sighting(new Detection(new OpenCvSharp.Rect(100, 100, 80, 40), 0.97, 1), 1, DateTime.UtcNow);
    }

    [Fact]
    public void A_click_with_a_discord_message_sends_the_message_only_after_the_click()
    {
        var got = new List<MacroEvent>();
        var ex = new StepExecutor(new Cap(got), new Finder(), new RealClock(), () => false);
        var step = new StepOptions { Move = MoveMode.Jump, RequireStable = false, Retries = 0, MinRepeatMs = 0, DiscordText = "clicked it", DiscordShot = true };
        ex.Send(new MacroEvent(0, EventKind.TargetClick, Target: "t", Delta: 2000, Step: step));
        int up = got.FindIndex(e => e.Kind == EventKind.ButtonUp), msg = got.FindIndex(e => e.Kind == EventKind.Discord);
        Assert.True(up >= 0 && msg > up, "the message must come after the click");
        Assert.Equal(("clicked it", true), (got[msg].Target, got[msg].Extended));
    }

    [Fact]
    public void A_click_without_a_message_sends_nothing_to_discord_and_an_export_scrubs_a_webhook_typed_into_it()
    {
        var got = new List<MacroEvent>();
        var ex = new StepExecutor(new Cap(got), new Finder(), new RealClock(), () => false);
        ex.Send(new MacroEvent(0, EventKind.TargetClick, Target: "t", Delta: 2000, Step: new StepOptions { Move = MoveMode.Jump, RequireStable = false, Retries = 0, MinRepeatMs = 0 }));
        Assert.DoesNotContain(got, e => e.Kind == EventKind.Discord);

        var m = new MacroStudio.Model.Macro();
        m.Events.Add(new MacroEvent(0, EventKind.TargetClick, Target: "t", Step: new StepOptions { DiscordText = "see https://discord.com/api/webhooks/1/SECRETTOKEN done" }));
        var json = MacroStudio.Storage.MacroSerializer.Serialize(DiscordWebhook.ForExport(m));
        Assert.DoesNotContain("SECRETTOKEN", json);
    }
}

public class RecordIntoTests
{
    [Fact]
    public void A_recorded_block_is_inserted_at_the_playhead_time_keeping_its_own_timing_and_pushing_later_events_back()
    {
        var s = new MacroStudio.Editing.EditSession();
        s.Load(new MacroStudio.Model.Macro { Events = [new MacroEvent(0, EventKind.MouseMove, X: 1, Y: 1), new MacroEvent(1000, EventKind.KeyDown, 0x41), new MacroEvent(1100, EventKind.KeyUp, 0x41)] }.WithIds(), null);
        var recorded = new[] { new MacroEvent(0, EventKind.MouseMove, X: 5, Y: 5), new MacroEvent(300, EventKind.ButtonDown, X: 6, Y: 6), new MacroEvent(380, EventKind.ButtonUp, X: 6, Y: 6) };
        s.InsertBlock(recorded, 500, ripple: true);
        var ev = s.Events;
        Assert.Equal([0, 500, 800, 880], ev.Take(4).Select(e => e.T));                    // the new block starts at 500 ms and keeps its own spacing
        Assert.True(ev[4].T > 880 && ev[4].Kind == EventKind.KeyDown);                       // what was at 1000 ms comes after it
        Assert.True(s.CanUndo);
    }
}

static class MacroTestExt { public static MacroStudio.Model.Macro WithIds(this MacroStudio.Model.Macro m) { m.Events = m.Events.Select((e, i) => e with { Id = i + 1 }).ToList(); return m; } }

public class HotkeyTests
{
    static MacroStudio.Native.Combo C(int mods, int vk) => new(mods, vk);
    const int Alt = 1, Ctrl = 2, Shift = 4;

    [Fact]
    public void Defaults_are_F9_record_F10_play_F11_pause_F8_stop_F12_paths_and_a_chosen_combination_replaces_only_its_own()
    {
        var k = new MacroStudio.Native.HotkeySettings();
        Assert.Equal(["F9", "F10", "F11", "F8", "F12"], new[] { "record", "play", "pause", "stop", "paths" }.Select(k.Name));
        k.Set("record", C(Ctrl | Shift | Alt, 0x52));                                  // Ctrl+Shift+Alt+R
        Assert.Equal("Ctrl+Shift+Alt+R", k.Name("record")); Assert.Equal("F10", k.Name("play"));
        Assert.Equal("Play", k.Owner(C(0, 0x79), "record"));                          // another action already uses F10
        Assert.Null(k.Owner(C(Ctrl | Shift | Alt, 0x52), "record"));                  // its own combination is not a clash
        Assert.Null(k.Owner(C(Ctrl, 0x79), "record"));                                // Ctrl+F10 is not F10
        k.Reset(); Assert.Equal("F9", k.Name("record"));
    }

    [Theory]
    [InlineData(0, 0x70, true)] [InlineData(0, 0x87, true)] [InlineData(0, 0x13, true)] [InlineData(0, 0x2D, true)] [InlineData(0, 0x60, true)]   // plain function / navigation / number-pad keys
    [InlineData(0, 0x41, false)] [InlineData(0, 0x31, false)] [InlineData(0, 0x20, false)] [InlineData(0, 0x0D, false)] [InlineData(0, 0x1B, false)]   // plain typing keys
    [InlineData(Shift, 0x41, true)]                                                     // Shift+A: allowed (it costs the capital A elsewhere, which the app tells you)
    [InlineData(Shift, 0x74, true)]                                                     // Shift+F5
    [InlineData(Ctrl, 0x52, true)] [InlineData(Alt, 0x31, true)] [InlineData(Ctrl | Shift | Alt, 0x52, true)]
    [InlineData(Ctrl, 0x11, false)] [InlineData(Ctrl | Shift, 0xA0, false)]              // a modifier is never the main key
    public void Any_modifier_makes_a_letter_a_valid_hotkey_and_plain_typing_keys_are_refused(int mods, int vk, bool ok) => Assert.Equal(ok, MacroStudio.Native.HotkeySettings.Allowed(C(mods, vk)));

    [Fact]
    public void Combinations_survive_saving_and_the_modifier_keys_are_known()
    {
        var k = new MacroStudio.Native.HotkeySettings();
        k.Set("stop", C(Ctrl | Alt, 0x53));
        var back = System.Text.Json.JsonSerializer.Deserialize<MacroStudio.Native.HotkeySettings>(System.Text.Json.JsonSerializer.Serialize(k))!;
        Assert.Equal("Ctrl+Alt+S", back.Name("stop"));
        Assert.Contains(0x11, MacroStudio.Native.HotkeySettings.VksOf(Ctrl));
        Assert.Contains(0xA4, MacroStudio.Native.HotkeySettings.VksOf(Alt));
        Assert.True(back.UsesModifiers);
        Assert.Contains(0xA2, back.KeysToIgnore());                                   // the modifier keys are not "the user using the keyboard"
        Assert.DoesNotContain(0xA2, new MacroStudio.Native.HotkeySettings().KeysToIgnore());
    }
}

public class ShiftHotkeyTests
{
    [Fact]
    public void Shift_plus_a_letter_is_allowed_but_flagged_as_taking_the_capital_letter_away()
    {
        Assert.True(MacroStudio.Native.HotkeySettings.StealsTyping(new MacroStudio.Native.Combo(4, 0x41)));          // Shift+A
        Assert.False(MacroStudio.Native.HotkeySettings.StealsTyping(new MacroStudio.Native.Combo(2 | 4, 0x41)));    // Ctrl+Shift+A costs nothing
        Assert.False(MacroStudio.Native.HotkeySettings.StealsTyping(new MacroStudio.Native.Combo(4, 0x74)));        // Shift+F5
    }
}

public class PerRecordingTargetTests : IDisposable
{
    readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms-tg-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true); }

    [Fact]
    public void A_recording_lists_its_own_targets_the_shared_ones_and_those_its_steps_use_and_nothing_else()
    {
        var store = new MacroStudio.Vision.TargetStore(_dir);
        using var img = new OpenCvSharp.Mat(30, 60, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(40, 90, 200));
        var d = new MacroStudio.Model.ScreenRect(0, 0, 100, 100);
        var mine = store.Add("mine", img, 96, d, 0.8); mine.Owner = "rec-A"; store.Save(mine);
        var other = store.Add("other", img, 96, d, 0.8); other.Owner = "rec-B"; store.Save(other);
        var everyone = store.Add("everyone", img, 96, d, 0.8); everyone.Owner = MacroStudio.Vision.Target.Everyone; store.Save(everyone);
        var legacy = store.Add("legacy", img, 96, d, 0.8);                                      // made before targets belonged to recordings (no owner)
        var all = store.List();
        var none = new HashSet<string>();
        Assert.Equal(["everyone", "mine"], all.Where(t => t.VisibleTo("rec-A", none)).Select(t => t.Name).OrderBy(n => n));
        Assert.Equal(["everyone", "other"], all.Where(t => t.VisibleTo("rec-B", none)).Select(t => t.Name).OrderBy(n => n));
        Assert.Equal(["everyone"], all.Where(t => t.VisibleTo(null, none)).Select(t => t.Name));          // a recording with no id and no steps: only what is shared on purpose
        Assert.Equal(["everyone", "legacy", "mine"], all.Where(t => t.VisibleTo("rec-A", new HashSet<string> { legacy.Id })).Select(t => t.Name).OrderBy(n => n));   // an older target shows up once a step uses it
    }

    [Fact]
    public void A_cloned_target_has_its_own_id_and_owner_and_its_own_pixels()
    {
        var store = new MacroStudio.Vision.TargetStore(_dir);
        using var img = new OpenCvSharp.Mat(30, 60, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(40, 90, 200));
        var a = store.Add("btn", img, 96, new MacroStudio.Model.ScreenRect(0, 0, 100, 100), 0.77); a.Owner = "rec-A"; a.ColorTolerance = 55; store.Save(a);
        var b = store.Clone(a.Id, "rec-B");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(("rec-B", "btn", 0.77, 55.0), (b.Owner, b.Name, b.Threshold, b.ColorTolerance));
        Assert.Equal(("rec-A"), store.Get(a.Id)!.Owner);                                      // the original is untouched
        store.Delete(a.Id);
        Assert.Equal(60, store.Image(b.Id).Width);                                            // deleting the original does not take the copy's pixels with it
    }
}

public class TargetRemapTests
{
    [Fact]
    public void Steps_report_every_target_they_use_and_can_be_pointed_at_other_targets()
    {
        var click = new MacroEvent(0, EventKind.TargetClick, Target: "a", Step: new StepOptions { VerifyTarget = "b", RecoveryTarget = "c" });
        var key = new MacroEvent(5, EventKind.KeyDown, 0x41);
        Assert.Equal(["a", "b", "c"], TargetRemap.Used(click));
        Assert.Empty(TargetRemap.Used(key));
        var moved = TargetRemap.Apply(click, new Dictionary<string, string> { ["a"] = "A2", ["c"] = "C2" });
        Assert.Equal(("A2", "b", "C2"), (moved.Target, moved.Step!.VerifyTarget, moved.Step.RecoveryTarget));   // untouched ids stay
        Assert.Equal(key, TargetRemap.Apply(key, new Dictionary<string, string> { ["a"] = "x" }));
    }
}

public class TransformTests
{
    [Fact]
    public void Move_scale_and_rotate_map_positions_about_the_pivot()
    {
        Assert.Equal((100.0, 50.0), new MacroStudio.Editing.Xf(0, 0).Map(100, 50));                                      // identity
        var m = new MacroStudio.Editing.Xf(0, 0, Dx: 10, Dy: -5).Map(100, 50); Assert.Equal((110.0, 45.0), m);          // move
        var s = new MacroStudio.Editing.Xf(100, 100, 2, 0.5).Map(150, 140); Assert.Equal((200.0, 120.0), s);            // scale about (100,100): x doubles, y halves
        var r = new MacroStudio.Editing.Xf(100, 100, RotDeg: 90).Map(150, 100);                                         // 90 degrees clockwise on screen (y points down)
        Assert.InRange(r.X, 99.999, 100.001); Assert.InRange(r.Y, 149.999, 150.001);
        var back = new MacroStudio.Editing.Xf(100, 100, RotDeg: 33).Map(180, 40);
        var home = new MacroStudio.Editing.Xf(100, 100, RotDeg: -33).Map(back.X, back.Y);
        Assert.InRange(home.X, 179.999, 180.001); Assert.InRange(home.Y, 39.999, 40.001);                               // rotating back returns to the start
    }

    [Fact]
    public void Applying_a_transform_moves_positions_and_the_saved_click_position_of_target_clicks_but_not_keys_or_time()
    {
        var xf = new MacroStudio.Editing.Xf(0, 0, 2, 2, Dx: 5, Dy: 5);
        var move = xf.Apply(new MacroEvent(120, EventKind.MouseMove, X: 10, Y: 20));
        Assert.Equal((25, 45, 120.0), (move.X, move.Y, move.T));
        var click = xf.Apply(new MacroEvent(300, EventKind.TargetClick, Target: "t", Step: new StepOptions { FallbackX = 100, FallbackY = 50 }));
        Assert.Equal((205, 105), (click.Step!.FallbackX, click.Step.FallbackY));
        var key = new MacroEvent(5, EventKind.KeyDown, 0x41);
        Assert.Equal(key, xf.Apply(key));
        Assert.Equal((10.0, 20.0, 30.0, 40.0), MacroStudio.Editing.Xf.BoundsOf([new MacroEvent(0, EventKind.MouseMove, X: 10, Y: 20), new MacroEvent(1, EventKind.MouseMove, X: 40, Y: 60), key])!.Value);
    }
}
