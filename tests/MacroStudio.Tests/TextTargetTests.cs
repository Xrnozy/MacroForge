using System.IO;
using MacroStudio.Model;
using MacroStudio.Vision;
using OpenCvSharp;

namespace MacroStudio.Tests;

public sealed class TextTargetTests : IDisposable
{
    static readonly DesktopInfo Info = new(new ScreenRect(0, 0, 800, 400), [new ScreenRect(0, 0, 800, 400)]);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ms-text-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static Mat Screen(string text, int x = 300, int y = 200)
    {
        var m = new Mat(400, 800, MatType.CV_8UC3, Scalar.All(235));
        Cv2.PutText(m, text, new Point(x, y), HersheyFonts.HersheySimplex, 1.2, Scalar.All(20), 2);
        return m;
    }

    [Fact]
    public void Norm_and_similarity_ignore_case_spacing_and_one_misread_letter()
    {
        Assert.Equal("startgame", TextRecognizer.Norm("Start  GAME!"));
        Assert.True(TextRecognizer.Similarity("startgame", "startgarne") >= 0.85);
        Assert.True(TextRecognizer.Similarity("startgame", "settings") < 0.5);
    }

    [Fact]
    public void Find_picks_the_words_in_a_line_and_not_other_text()
    {
        var lines = new List<List<TextRecognizer.Word>> { new() { new("Press", new Rect(0, 0, 50, 20)), new("Start", new Rect(60, 0, 50, 20)), new("Game", new Rect(120, 0, 50, 20)) } };
        var hit = TextRecognizer.Find(lines, "start game")[0];
        Assert.Equal(new Rect(60, 0, 110, 20), hit.Rect);
        Assert.Empty(TextRecognizer.Find(lines, "Settings"));
    }

    [Fact]
    public void Watcher_finds_text_that_is_on_screen_and_loses_it_when_it_goes()
    {
        if (!TextRecognizer.Available) return;
        using var store = new TargetDisposer(_dir);
        using var scr = Screen("Start Game");
        var t = store.S.Add("txt", new Mat(scr, new Rect(290, 160, 260, 60)).Clone(), 96, Info.Virtual, 0.85);
        t.Text = "Start Game"; store.S.Save(t);
        var shown = scr;
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var w = new TargetWatcher(store.S, r => new Mat(shown, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => clock);
        using var h = w.Watch([t]);
        for (int i = 0; i < 6 && w.Get(t.Id)?.Hit == null; i++) { var d = w.Tick(); clock = clock.AddMilliseconds(d + 1); }
        var hit = w.Get(t.Id)?.Hit;
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Center.X, 290, 560); Assert.InRange(hit.Value.Center.Y, 160, 220);

        using var other = Screen("Settings");
        shown = other;
        for (int i = 0; i < 8 && w.Get(t.Id)?.Hit != null; i++) { var d = w.Tick(); clock = clock.AddMilliseconds(d + 1); }
        Assert.Null(w.Get(t.Id)?.Hit);
    }

    [Fact]
    public void Watcher_text_target_without_ocr_reports_unavailable_and_does_not_hang()
    {
        // OCR off — documented semantic: the text target reports not-found with an "unavailable" note, retries on the
        // normal idle cadence. No hang, no crash, no busy OCR loop, no pixel fallback.
        var prev = TextRecognizer.ForceAvailable;
        TextRecognizer.ForceAvailable = false;
        try
        {
            using var store = new TargetDisposer(_dir);
            using var scr = Screen("Start Game");
            var t = store.S.Add("txt", new Mat(scr, new Rect(290, 160, 260, 60)).Clone(), 96, Info.Virtual, 0.85);
            t.Text = "Start Game"; store.S.Save(t);
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var w = new TargetWatcher(store.S, r => new Mat(scr, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => clock);
            using var h = w.Watch([t]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 8; i++) { var d = w.Tick(); clock = clock.AddMilliseconds(d + 1); }
            Assert.True(sw.ElapsedMilliseconds < 5000, $"unavailable-OCR path took {sw.ElapsedMilliseconds} ms — it must not hang");
            Assert.Null(w.Get(t.Id)?.Hit);
            Assert.Contains("unavailable", w.Get(t.Id)?.Note ?? "");
        }
        finally { TextRecognizer.ForceAvailable = prev; }
    }

    [Fact]
    public void Watcher_text_target_via_injected_reader_finds_and_loses_text()
    {
        // No OS engine needed: the recognizer seam feeds scripted words, so the OCR plumbing is covered on machines
        // where TextRecognizer.Available is false for real. The fake reads whatever "screen" the test says is shown.
        var prevA = TextRecognizer.ForceAvailable; var prevR = TextMatch.Reader;
        List<List<TextRecognizer.Word>> reading = [new() { new TextRecognizer.Word("Start", new Rect(10, 10, 95, 35)), new TextRecognizer.Word("Game", new Rect(110, 10, 90, 35)) }];
        TextRecognizer.ForceAvailable = true;
        TextMatch.Reader = _ => reading;
        try
        {
            using var store = new TargetDisposer(_dir);
            using var scr = Screen("Start Game");
            var t = store.S.Add("txt", new Mat(scr, new Rect(290, 160, 260, 60)).Clone(), 96, Info.Virtual, 0.85);
            t.Text = "Start Game"; store.S.Save(t);
            var shown = scr;
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var w = new TargetWatcher(store.S, r => new Mat(shown, new Rect(r.X, r.Y, r.Width, r.Height)).Clone(), Info, 96, threaded: false, clock: () => clock);
            using var h = w.Watch([t]);
            for (int i = 0; i < 6 && w.Get(t.Id)?.Hit == null; i++) { var d = w.Tick(); clock = clock.AddMilliseconds(d + 1); }
            var hit = w.Get(t.Id)?.Hit;
            Assert.NotNull(hit);
            Assert.Equal("text", hit!.Value.Method);   // found by reading, not by the pixel template

            using var other = Screen("Settings");
            shown = other;
            reading = [new() { new TextRecognizer.Word("Settings", new Rect(10, 10, 150, 35)) }];
            for (int i = 0; i < 8 && w.Get(t.Id)?.Hit != null; i++) { var d = w.Tick(); clock = clock.AddMilliseconds(d + 1); }
            Assert.Null(w.Get(t.Id)?.Hit);
        }
        finally { TextMatch.Reader = prevR; TextMatch.Reset(); TextRecognizer.ForceAvailable = prevA; }
    }

    sealed class TargetDisposer(string dir) : IDisposable { public TargetStore S = new(dir); public void Dispose() { } }
}
