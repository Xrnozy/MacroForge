using MacroStudio.Model;
using MacroStudio.Playback;
using MacroStudio.Storage;
using MacroStudio.Vision;
using OpenCvSharp;
using static MacroStudio.Tests.CoreTests;

namespace MacroStudio.Tests;

/// <summary>When blocks: conditions, connected macros, restart, watching, click first. Scripted screen + simulated clock.</summary>
public class FlowTests
{
    /// <summary>Target "pop" is visible while Visible(clock ms) says so. Each look costs 10 ms.</summary>
    sealed class Screen(FakeClock clock) : ITargetFinder
    {
        public Func<double, bool> Visible = _ => false;
        public string? NameOf(string id) => id == "pop" ? "Popup" : null;
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id)
        {
            clock.Now += 10;
            return id == "pop" && Visible(clock.Now) ? new Sighting(new Detection(new Rect(760, 380, 80, 40), 0.97, 1), (long)clock.Now, DateTime.UtcNow) : null;
        }
    }

    static MacroEvent Mark(double t, int x) => new(t, EventKind.MouseMove, X: x, Y: 0);
    static MacroEvent When(double t, FlowOptions f, StepOptions? o = null) => new(t, EventKind.When, Target: "pop", Flow: f, Step: o);

    sealed class Rig
    {
        public FakeClock Clock = new(); public RecordingSink Out; public Screen Scr; public ExecutionLog Log = new();
        public Dictionary<string, MacroEvent[]> Lib = new();
        public Rig() { Out = new RecordingSink(Clock); Scr = new Screen(Clock); }
        public Player Play(MacroEvent[] main, int loops = 1)
        {
            var p = new Player(Out, Clock) { Log = Log };
            p.Flow = new FlowRunner(Scr, Clock, () => p.StopRequested, Out, id => Lib.GetValueOrDefault(id), (_, _) => (Out, Out), () => 1, Log);
            p.Run(main, 0, main.Length, loops);
            return p;
        }
        public int[] Xs => Out.Sent.Where(s => s.E.Kind == EventKind.MouseMove).Select(s => s.E.X).ToArray();
    }

    [Fact]
    public void When_visible_runs_the_connected_macro_then_continues()
    {
        var r = new Rig { }; r.Scr.Visible = _ => true;
        r.Lib["fix"] = [Mark(0, 100), Mark(10, 101)];
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "fix", WaitMs = 500 }), Mark(20, 2)]);
        Assert.Equal([1, 100, 101, 2], r.Xs);
    }

    [Fact]
    public void When_not_visible_does_nothing_and_the_main_macro_goes_on()
    {
        var r = new Rig(); r.Lib["fix"] = [Mark(0, 100)];
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "fix", WaitMs = 200 }), Mark(20, 2)]);
        Assert.Equal([1, 2], r.Xs);
    }

    [Fact]
    public void When_it_appears_during_the_wait_it_acts()
    {
        var r = new Rig(); r.Scr.Visible = t => t > 120; r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Macro = "fix", WaitMs = 1000 }), Mark(10, 2)]);
        Assert.Equal([100, 2], r.Xs);
    }

    [Fact]
    public void Disappears_waits_until_it_is_gone()
    {
        var r = new Rig(); r.Scr.Visible = t => t < 300; r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Condition = WhenCondition.Disappears, Macro = "fix", WaitMs = 2000 }), Mark(10, 2)]);
        Assert.Equal([100, 2], r.Xs);
        Assert.True(r.Clock.Now >= 300);
    }

    [Fact]
    public void Does_not_appear_acts_only_when_it_never_showed_up()
    {
        var gone = new Rig(); gone.Lib["fix"] = [Mark(0, 100)];
        gone.Play([When(0, new FlowOptions { Condition = WhenCondition.NotFound, Macro = "fix", WaitMs = 300 }), Mark(10, 2)]);
        Assert.Equal([100, 2], gone.Xs);
        var shown = new Rig(); shown.Scr.Visible = t => t > 100; shown.Lib["fix"] = [Mark(0, 100)];
        shown.Play([When(0, new FlowOptions { Condition = WhenCondition.NotFound, Macro = "fix", WaitMs = 300 }), Mark(10, 2)]);
        Assert.Equal([2], shown.Xs);
    }

    [Fact]
    public void Restart_goes_back_to_the_beginning_and_stops_at_the_limit()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        r.Play([Mark(0, 1), When(10, new FlowOptions { Do = FlowDo.Restart, WaitMs = 100, MaxRuns = 3 }), Mark(20, 2)]);
        Assert.Equal([1, 1, 1, 1, 2], r.Xs);                 // acted 3 times, the 4th pass is over the limit and goes on
    }

    [Fact]
    public void Run_the_macro_then_restart()
    {
        var r = new Rig();
        r.Scr.Visible = _ => !r.Xs.Contains(100); r.Lib["fix"] = [Mark(0, 100)];   // the popup goes away once the connected macro has run
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "fix", After = FlowAfter.Restart, WaitMs = 100 }), Mark(20, 2)]);
        Assert.Equal([1, 100, 1, 2], r.Xs);
    }

    [Fact]
    public void Continue_does_nothing_extra()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        r.Play([When(0, new FlowOptions { Do = FlowDo.Continue, WaitMs = 100 }), Mark(10, 2)]);
        Assert.Equal([2], r.Xs);
    }

    [Fact]
    public void A_watching_block_fires_between_steps_and_the_main_macro_resumes()
    {
        var r = new Rig(); r.Scr.Visible = t => t is > 900 and < 1100; r.Lib["fix"] = [Mark(0, 100)];
        r.Play([When(0, new FlowOptions { Always = true, Macro = "fix" }), Mark(10, 1), Mark(2000, 2)]);   // a long gap before the last step
        Assert.Equal([1, 100, 2], r.Xs);
        Assert.InRange(r.Out.Sent[^1].At, 2000, 2400);        // the handler's time was not "caught up": the step still waited its full gap
    }

    [Fact]
    public void A_watching_block_fires_inside_a_click_that_is_waiting_for_its_target()
    {
        var r = new Rig(); r.Scr.Visible = t => t is > 300 and < 600; r.Lib["fix"] = [Mark(0, 100)];
        var clock = r.Clock; var fin = r.Scr;
        // the waiting step is a target click on "abc"-like target that never shows; the watcher on "pop" must still act
        var fm = new Finder2(clock, fin);
        var exec = new StepExecutor(r.Out, fm, clock, () => false, r.Log);
        var p = new Player(exec, clock) { Log = r.Log };
        var flow = new FlowRunner(fm, clock, () => p.StopRequested, r.Out, id => r.Lib.GetValueOrDefault(id), (_, _) => (r.Out, r.Out), () => 1, r.Log);
        exec.Idle = () => { var s = flow.Poll(); if (s == FlowStep.Restart) throw new FlowRestartException(); };
        p.Flow = flow;
        var main = new[] { When(0, new FlowOptions { Always = true, Macro = "fix" }), new MacroEvent(10, EventKind.WaitTarget, Target: "slow", Delta: 1000, Extended: true) };
        p.Run(main, 0, main.Length, 1);
        Assert.Contains(100, r.Xs);
    }

    /// <summary>The popup from <see cref="Screen"/> plus a target "slow" that never shows.</summary>
    sealed class Finder2(FakeClock clock, Screen inner) : ITargetFinder
    {
        public string? NameOf(string id) => id == "slow" ? "Slow" : inner.NameOf(id);
        public double ThresholdOf(string id) => 0.85;
        public Sighting? Find(string id) { if (id != "slow") return inner.Find(id); clock.Now += 10; return null; }
    }

    [Fact]
    public void Click_first_clicks_the_target_before_the_action()
    {
        var r = new Rig(); r.Scr.Visible = _ => true; r.Lib["fix"] = [Mark(0, 100)];
        var clock = r.Clock; var exec = new StepExecutor(r.Out, r.Scr, clock, () => false, r.Log, null, () => true, null, () => false);   // dynamic path off: a plain click
        var p = new Player(exec, clock) { Log = r.Log };
        p.Flow = new FlowRunner(r.Scr, clock, () => p.StopRequested, exec, id => r.Lib.GetValueOrDefault(id), (_, _) => (r.Out, r.Out), () => 1, r.Log);
        p.Run([When(0, new FlowOptions { Macro = "fix", ClickFirst = true, WaitMs = 100 }, new StepOptions { RelX = 0.5, RelY = 0.5, Move = MoveMode.Jump })], 0, 1, 1);
        int click = r.Out.Sent.FindIndex(s => s.E.Kind == EventKind.ButtonDown);
        int action = r.Out.Sent.FindIndex(s => s.E.Kind == EventKind.MouseMove && s.E.X == 100);
        Assert.True(click >= 0 && action > click, "the click comes first, then the connected macro");
        Assert.Equal((840, 420), (r.Out.Sent[click].E.X, r.Out.Sent[click].E.Y));          // the bottom-right corner of the target
    }

    [Fact]
    public void A_macro_that_runs_itself_stops_at_the_depth_limit()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        var self = new FlowOptions { Macro = "loop", WaitMs = 50, MaxRuns = 0 };
        r.Lib["loop"] = [Mark(0, 7), When(5, self)];
        r.Play([When(0, self)]);
        Assert.InRange(r.Xs.Length, 2, 10);
        Assert.True(r.Log.Count(LogResult.Failed) >= 1);
    }

    [Fact]
    public void A_missing_connected_macro_is_logged_and_the_run_goes_on()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        r.Play([When(0, new FlowOptions { Macro = "gone", WaitMs = 50 }), Mark(10, 2)]);
        Assert.Equal([2], r.Xs);
        Assert.True(r.Log.Count(LogResult.Failed) >= 1);
    }

    [Fact]
    public void Stop_during_a_connected_macro_stops_everything()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        r.Lib["fix"] = [Mark(0, 100), Mark(500, 101), Mark(1000, 102)];
        Player? pl = null;
        r.Out.OnSend = n => { if (n == 1) pl!.Stop(); };
        var p = new Player(r.Out, r.Clock) { Log = r.Log }; pl = p;
        p.Flow = new FlowRunner(r.Scr, r.Clock, () => p.StopRequested, r.Out, id => r.Lib.GetValueOrDefault(id), (_, _) => (r.Out, r.Out), () => 1, r.Log);
        p.Run([When(0, new FlowOptions { Macro = "fix", WaitMs = 50 }), Mark(10, 2)], 0, 2, 1);
        Assert.DoesNotContain(2, r.Xs);
        Assert.DoesNotContain(102, r.Xs);
    }

    [Fact]
    public void Flow_settings_survive_the_file_and_copy_paste()
    {
        var f = new FlowOptions { Condition = WhenCondition.Disappears, Always = true, WaitMs = 1234, ClickFirst = true, Do = FlowDo.Restart, Macro = "m-1", After = FlowAfter.Restart, MaxRuns = 4 };
        var m = new Macro { Events = [When(0, f, new StepOptions { RelX = 0.25 })] };
        var back = MacroSerializer.Deserialize(MacroSerializer.Serialize(m)).Events[0];
        Assert.Equal(f, back.Flow);
        Assert.Equal(0.25, back.Step!.RelX);
        Assert.Equal(f, MacroSerializer.DeserializeEvents(MacroSerializer.SerializeEvents([When(0, f)]))[0].Flow);
        Assert.Contains("\"kind\": \"When\"", MacroSerializer.Serialize(m).Replace("\"kind\":\"When\"", "\"kind\": \"When\""));
    }

    [Fact]
    public void Old_files_without_when_blocks_load_unchanged()
    {
        var m = MacroSerializer.Deserialize("{\"version\":1,\"name\":\"x\",\"events\":[{\"t\":0,\"kind\":\"MouseMove\",\"x\":1,\"y\":2}]}");
        Assert.Null(m.Events[0].Flow);
    }

    [Fact]
    public void Remap_follows_the_target_of_a_when_block()
    {
        var e = When(0, new FlowOptions());
        Assert.Equal(["pop"], TargetRemap.Used(e));
        Assert.Equal("new", TargetRemap.Apply(e, new Dictionary<string, string> { ["pop"] = "new" }).Target);
    }

    [Fact]
    public void Library_finds_macros_by_id_and_lists_connected_macros_underneath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-flow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new MacroLibrary(dir);
            var fix = new Macro { Id = "fix-1", Name = "Rejoin", Events = [Mark(0, 1)] };
            MacroSerializer.Save(fix, lib.UniquePath("Rejoin"));
            var main = new Macro { Id = "main-1", Name = "Main", Subs = ["fix-1"], Events = [When(0, new FlowOptions { Macro = "fix-1" })] };
            MacroSerializer.Save(main, lib.UniquePath("Main"));
            var noId = lib.UniquePath("Plain"); MacroSerializer.Save(new Macro { Name = "Plain", Events = [Mark(0, 1)] }, noId);

            Assert.Equal("Rejoin", lib.NameOfId("fix-1"));
            Assert.Single(lib.LoadById("fix-1")!.Events);
            Assert.Null(lib.PathOfId("nope"));
            Assert.False(string.IsNullOrEmpty(lib.EnsureId(noId)));                        // a macro with no id gets one, and keeps its events
            Assert.Equal(lib.EnsureId(noId), lib.EnsureId(noId));

            var closed = lib.ListTree(new HashSet<string>());
            Assert.Equal(2, closed.Count);                                                    // Rejoin is filed in Main: it is not listed twice
            var mainItem = closed.Single(x => x.Name == "Main");
            Assert.True(mainItem.HasKids && !mainItem.Open);
            Assert.DoesNotContain(closed, x => x.Name == "Rejoin");
            var open = lib.ListTree(new HashSet<string> { mainItem.Path });
            Assert.Equal(3, open.Count);                                                      // Rejoin appears once, indented under Main
            int at = open.FindIndex(x => x.Name == "Main");
            Assert.Equal(("Rejoin", 1), (open[at + 1].Name, open[at + 1].Depth));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Problems_are_reported_for_a_when_block_that_cannot_work()
    {
        Analysis.TargetExists = id => id == "pop"; Analysis.MacroExists = id => id == "fix"; Analysis.CurrentMacroId = () => "me";
        try
        {
            MacroEvent B(FlowOptions f, string? target = "pop") => new(0, EventKind.When, Target: target, Flow: f);
            var ev = new[]
            {
                B(new FlowOptions { Macro = "fix" }),                          // fine
                B(new FlowOptions { Macro = "fix" }, target: null),            // no target
                B(new FlowOptions()),                                          // nothing to run
                B(new FlowOptions { Macro = "gone" }),                         // macro missing
                B(new FlowOptions { Macro = "me" }),                           // runs itself
                B(new FlowOptions { Do = FlowDo.Restart }),                    // fine: restart needs no macro
            };
            var issues = Analysis.Of(ev, null).Issues;
            Assert.DoesNotContain(0, issues.Keys);
            Assert.DoesNotContain(5, issues.Keys);
            Assert.Equal([1, 2, 3, 4], issues.Keys.OrderBy(k => k));
        }
        finally { Analysis.TargetExists = null; Analysis.MacroExists = null; Analysis.CurrentMacroId = null; }
    }

    [Fact]
    public void When_text_reads_in_plain_words()
    {
        EventInfo.MacroNameOf = id => id == "fix" ? "Rejoin" : null;
        try
        {
            var e = When(0, new FlowOptions { Macro = "fix", After = FlowAfter.Restart, ClickFirst = true, Always = true });
            Assert.Contains("appears", e.WhenText());
            Assert.Contains("run 'Rejoin'", e.WhenText());
            Assert.Contains("then restart", e.WhenText());
            Assert.Contains("click it", e.WhenText());
            Assert.Contains("watching all the time", e.WhenText());
        }
        finally { EventInfo.MacroNameOf = null; }
    }

    [Fact]
    public void Dropping_a_macro_on_another_files_it_inside_and_never_loops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new MacroLibrary(dir);
            string P(string id, string name) { var p = lib.UniquePath(name); MacroSerializer.Save(new Macro { Id = id, Name = name, Events = [Mark(0, 1), Mark(10, 2)] }, p); return p; }
            string main = P("main", "Main"), sub = P("sub", "Sub"), deep = P("deep", "Deep");

            Assert.Null(lib.AddSub(main, "sub"));                                             // Sub goes inside Main
            Assert.Null(lib.AddSub(sub, "deep"));                                             // Deep inside Sub
            Assert.NotNull(lib.AddSub(main, "main"));                                         // not inside itself
            Assert.NotNull(lib.AddSub(deep, "main"));                                         // Main -> Sub -> Deep: Deep cannot hold Main
            Assert.Equal(["sub"], MacroSerializer.Load(main).Subs);
            Assert.Equal(2, MacroSerializer.Load(main).Events.Count);                         // the events are untouched

            var tree = lib.ListTree(new HashSet<string> { main, sub });
            Assert.Equal(["Main", "Sub", "Deep"], tree.Select(x => x.Name));                  // one row each: Sub only inside Main, Deep only inside Sub
            Assert.Equal([0, 1, 2], tree.Select(x => x.Depth));
            Assert.Equal(main, tree[1].Parent);

            string other = P("other", "Other");
            Assert.Null(lib.AddSub(other, "sub"));                                            // moving Sub (with Deep) into Other takes it out of Main
            Assert.Empty(MacroSerializer.Load(main).Subs);
            Assert.Equal(["sub"], MacroSerializer.Load(other).Subs);

            Assert.True(lib.RemoveSub(other, "sub"));
            Assert.False(lib.RemoveSub(other, "sub"));
            Assert.Contains(lib.ListTree(new HashSet<string>()), x => x.Name == "Sub");       // back at the top
            Assert.Equal(["deep"], MacroSerializer.Deserialize(MacroSerializer.Serialize(MacroSerializer.Load(sub))).Subs);   // the file format keeps them
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_sub_macro_sees_the_targets_of_every_folder_above_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-inherit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new MacroLibrary(dir);
            string P(string id, string name) { var p = lib.UniquePath(name); MacroSerializer.Save(new Macro { Id = id, Name = name, Events = [Mark(0, 1)] }, p); return p; }
            string top = P("top", "Top"), mid = P("mid", "Mid"), low = P("low", "Low"), other = P("other", "Other");
            Assert.Null(lib.AddSub(top, "mid")); Assert.Null(lib.AddSub(mid, "low"));

            Assert.Equal(["top", "mid"], lib.AncestorIds(low).OrderByDescending(x => x == "top"));   // folder and the folder's folder
            Assert.Equal(["top"], lib.AncestorIds(mid));
            Assert.Empty(lib.AncestorIds(top));
            Assert.Empty(lib.AncestorIds(other));

            var fromTop = new MacroStudio.Vision.Target { Id = "t1", Owner = "top" };
            var fromOther = new MacroStudio.Vision.Target { Id = "t2", Owner = "other" };
            var inherited = lib.AncestorIds(low);
            var none = new HashSet<string>();
            Assert.True(fromTop.VisibleTo("low", none, inherited));                                  // inherited from two levels up
            Assert.False(fromOther.VisibleTo("low", none, inherited));                               // a macro in another folder is not
            Assert.False(fromTop.VisibleTo("other", none, lib.AncestorIds(other)));
            Assert.False(fromTop.VisibleTo("low", none));                                            // and without the folders, as before
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_connected_macro_that_ends_with_its_own_when_block_runs_the_next_macro()
    {
        // main -> "menu" -> "aotr": the When block is the LAST step of the middle macro
        var r = new Rig(); r.Scr.Visible = _ => true;
        r.Lib["menu"] = [Mark(0, 50), When(30, new FlowOptions { Macro = "aotr", WaitMs = 500 })];
        r.Lib["aotr"] = [Mark(0, 900), Mark(20, 901)];
        r.Play([Mark(0, 1), When(10, new FlowOptions { Macro = "menu", WaitMs = 500 }), Mark(20, 2)]);
        Assert.Equal([1, 50, 900, 901, 2], r.Xs);
    }

    [Fact]
    public void Three_levels_deep_each_level_waits_for_its_own_target()
    {
        var r = new Rig(); r.Scr.Visible = t => t > 200;                      // the popup only shows up after 200 ms: the inner block has to wait for it
        r.Lib["menu"] = [Mark(0, 50), When(30, new FlowOptions { Macro = "aotr", WaitMs = 2000 })];
        r.Lib["aotr"] = [Mark(0, 900)];
        r.Play([When(0, new FlowOptions { Macro = "menu", WaitMs = 2000 })]);
        Assert.Equal([50, 900], r.Xs);
    }

    [Fact]
    public void Keep_waiting_holds_the_macro_on_the_step_until_the_target_shows_up()
    {
        var r = new Rig(); r.Scr.Visible = t => t > 60_000;                    // a minute of "loading", far beyond the 5 s wait
        r.Lib["aotr"] = [Mark(0, 900)];
        r.Play([When(0, new FlowOptions { Macro = "aotr", WaitMs = 5000, KeepWaiting = true }), Mark(10, 2)]);
        Assert.Equal([900, 2], r.Xs);
        Assert.True(r.Clock.Now > 60_000);

        var limited = new Rig(); limited.Scr.Visible = t => t > 60_000; limited.Lib["aotr"] = [Mark(0, 900)];
        limited.Play([When(0, new FlowOptions { Macro = "aotr", WaitMs = 5000 }), Mark(10, 2)]);
        Assert.Equal([2], limited.Xs);                                          // without it the block gives up after 5 s, as before
    }

    [Fact]
    public void The_trail_names_every_macro_that_is_still_running()
    {
        var r = new Rig(); r.Scr.Visible = _ => true;
        var seen = new List<string>();
        FlowRunner? flow = null;
        r.Lib["menu"] = [Mark(0, 50), When(10, new FlowOptions { Macro = "aotr", WaitMs = 100 })];
        r.Lib["aotr"] = [Mark(0, 900)];
        EventInfo.MacroNameOf = id => id;
        r.Out.OnSend = _ => seen.Add(flow!.Trail.Text);
        try
        {
            var p = new Player(r.Out, r.Clock) { Log = r.Log };
            flow = new FlowRunner(r.Scr, r.Clock, () => p.StopRequested, r.Out, id => r.Lib.GetValueOrDefault(id), (_, _) => (r.Out, r.Out), () => 1, r.Log);
            p.Flow = flow;
            p.Run([Mark(0, 1), When(5, new FlowOptions { Macro = "menu", WaitMs = 100 }), Mark(500, 2)], 0, 3, 1);
        }
        finally { EventInfo.MacroNameOf = null; }
        Assert.Equal(["", "menu", "menu › aotr", ""], seen);                    // main, inside menu, inside aotr (still inside menu), back in main
        Assert.Equal("", flow!.Trail.Text);
    }
}
