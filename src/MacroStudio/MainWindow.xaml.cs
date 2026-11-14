using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Playback;
using MacroStudio.Recording;
using MacroStudio.Storage;
using MacroStudio.Views;
using MacroStudio.Vision;

namespace MacroStudio;

public partial class MainWindow : Window
{
    readonly HotkeySettings _keys = HotkeySettings.Load();
    static readonly double[] Speeds = [0.25, 0.5, 1, 1.5, 2, 4, 8];

    readonly EditSession _s = new();
    readonly MacroLibrary _lib = new();
    readonly Recorder _recorder = new();
    readonly TargetStore _targets = new();
    readonly Detector _detector = new();
    readonly TargetWatcher _watcher;
    readonly ExecutionLog _log = new();
    ScreenTargetFinder? _finder;
    FlowRunner? _flow;
    volatile PlaybackSession? _session;          // library snapshot + loaded connected macros of the running playback; released when it ends
    volatile bool _starting, _cancelStart;       // Play pressed but the player thread not started yet (the window is minimising): a Stop in that gap cancels the start
    CancellationTokenSource? _glide;             // the pointer gliding to its final place after a run
    TargetsPanel _targetsPanel = null!;
    Dictionary<string, string> _targetNames = new();
    Player? _player;
    HotKeys? _hotKeys;
    PathView _path = null!;
    TimelineView _timeline = null!;
    EventGridView _grid = null!;
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(33) };
    WindowState _restoreState = WindowState.Normal;
    bool _playing, _loadingLibrary;
    readonly DiscordSettings _discord = DiscordSettings.Load();
    InspectorPanel? _inspector;
    double? _insertAt;   // set while recording INTO the open macro at the playhead (instead of making a new recording)
    string? _playId;   // the recording that is playing: its own Discord link is looked up by this
    string _playName = "";
    PathTrace? _trace;
    (int X, int Y)? _endPoint;   // where the pointer ended in the recording (mapped to this PC)
    Views.PathOverlay? _paths;
    List<(int X, int Y)> _recordedPath = new();
    (int L, int T, int R, int B)? _savedBounds;
    WindowState _savedState;
    long _lastPlaced;
    readonly DispatcherTimer _live;
    int _liveHave, _liveEpoch;                   // events of the running recording already in the session
    volatile bool _recVisible;                   // "Always on top": the window stays on screen while recording
    volatile bool _visiblePlay;                  // the window stays on screen during this playback
    nint _hwnd;
    volatile bool _dynamicPath = true;           // master switch for bending / gliding mouse paths (read on the playback thread)
    MacroEvent[] _playEvents = [];

    public MainWindow()
    {
        InitializeComponent(); InitChrome();
        _path = new PathView(_s); PathHost.Child = _path;
        _timeline = new TimelineView(_s); TimelineHost.Child = _timeline;
        _grid = new EventGridView(_s, id => _lib.LoadById(id)?.Events.OrderBy(x => x.T).ToArray()); GridHost.Child = _grid;
        _grid.MacroDropped += GridMacroDropped;
        _grid.WhenChipDropped += InsertWhen;
        _grid.OpenMacroRequested += OpenConnectedMacro;
        EventInfo.TargetNameOf = id => _targetNames.GetValueOrDefault(id);
        Analysis.TargetExists = id => _targetNames.ContainsKey(id);
        EventInfo.MacroNameOf = id => _session?.NameOf(id) ?? _lib.NameOfId(id);   // during a run: the per-run snapshot (also read from the playback thread)
        Analysis.MacroExists = id => _lib.PathOfId(id) != null;
        Analysis.CurrentMacroId = () => _s.Id;
        InspectorHost.Child = _inspector = new InspectorPanel(_s, () => MyTargets().Select(t => (t.Id, t.Name)).OrderBy(x => x.Name).ToList(), TargetThumb, DiscordHooksForInspector());
        _inspector.RecordHandlerRequested += RecordHandler;
        _inspector.FileMacro = FileUnderOpenMacro;
        _inspector.FolderMacros = () =>
        {   // this macro's own folder first, then every other macro in the library: a block can reference any of them, wherever it lives
            var mine = _s.Subs.Select(id => (Id: id, Name: _lib.NameOfId(id) ?? "(not in the library)")).OrderBy(x => x.Name).ToList();
            var taken = mine.Select(m => m.Id).ToHashSet();
            var others = _lib.List().Where(i => !string.Equals(i.Path, _s.Path, StringComparison.OrdinalIgnoreCase))
                .Select(i => (Id: _lib.IdFor(i.Path), i.Name)).Where(x => x.Id != null && x.Id != _s.Id && !taken.Contains(x.Id)).OrderBy(x => x.Name)
                .Select(x => (x.Id!, x.Name)).ToList();
            return mine.Concat(others).ToList();
        };
        InitDiagnostics();
        _watcher = new TargetWatcher(_targets);
        _targetsPanel = new TargetsPanel(_targets, _detector, _watcher, this); TargetsHost.Child = _targetsPanel;
        _targetsPanel.RecordingId = () => _s.Id;
        _targetsPanel.Referenced = ReferencedTargets;
        _targetsPanel.EnsureRecordingId = EnsureRecordingId;
        _targetsPanel.InheritedOwners = () => _inheritedOwners;
        _targetsPanel.Changed += RefreshTargetNames;
        _targetsPanel.OverlayCapturable = () => CapturablePathsBox.IsChecked == true;
        RefreshTargetNames();
        _s.EventsChanged += UpdateSummary;
        _s.SelectionChanged += UpdateSummary;
        _ui.Tick += (_, _) => Tick();
        _live = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _live.Tick += (_, _) =>
        {
            if (!_recorder.IsRecording) { _live.Stop(); return; }
            // only the new events are copied; the full snapshot is the first tick and the fallback when an earlier event was taken back out
            var ev = _recorder.SnapshotFrom(_liveHave, ref _liveEpoch, out bool full);
            if (full || !_s.Append(ev))
            {
                if (!full) ev = _recorder.SnapshotFrom(0, ref _liveEpoch, out _);
                _s.Load(new Macro { Name = "Recording…", Desktop = MacroStudio.Native.Screens.Current(), Events = ev }, null);
                _liveHave = ev.Count;
            }
            else _liveHave += ev.Count;
            if (_s.Events.Length > 0) _s.SetPlayhead(_s.Events[^1].T);
            _path.RequestFit(); _timeline.RequestFit();
        };
        _ui.Start();
        PreviewKeyDown += OnShortcut;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _s.Load(new Macro { Name = "Untitled", Desktop = Screens.Current(), Env = Screens.CurrentEnv() }, null);
        RefreshLibrary(null);
        UpdateSummary();
    }

    /// <summary>The path-overlay checkboxes (and F12) act at once, also while a macro is playing.</summary>
    void PathsOptions_Click(object sender, RoutedEventArgs e)
    {
        _targetsPanel.SetOverlayCapturable(CapturablePathsBox.IsChecked == true);   // the live detection boxes follow the same switch
        if (_paths == null) return;
        _paths.Capturable = CapturablePathsBox.IsChecked == true;
        _paths.Visible = ShowPathsBox.IsChecked == true;
    }

    // ---- hotkeys (changeable) ----

    Action ActionOf(string action) => action switch
    {
        "record" => ToggleRecord,
        "play" => TogglePlay,
        "pause" => TogglePause,
        "stop" => StopAll,
        _ => () => { ShowPathsBox.IsChecked = ShowPathsBox.IsChecked != true; PathsOptions_Click(this, new RoutedEventArgs()); },
    };

    /// <summary>Registers every hotkey with the key it is set to now. Returns the ones another program already owns.</summary>
    List<string> ApplyHotkeys()
    {
        var failed = new List<string>();
        _recorder.IgnoredKeys.Clear(); _recorder.HotkeyMods.Clear();
        for (int i = 0; i < HotkeySettings.Actions.Length; i++)
        {
            var (action, label, _) = HotkeySettings.Actions[i];
            var c = _keys.Get(action);
            _hotKeys!.Unregister(i + 1);
            _recorder.IgnoredKeys.Add(c.Vk);                         // control keys never become part of a macro
            _recorder.HotkeyMods[c.Vk] = c.Mods;                     // nor do the modifier keys pressed to reach them
            var run = ActionOf(action);
            if (!_hotKeys.Register(i + 1, c.Mods, c.Vk, () => Dispatcher.BeginInvoke(run))) failed.Add($"{label} ({_keys.Name(action)})");
        }
        RefreshHotkeyLabels();
        return failed;
    }

    /// <summary>The hotkey as shown on a button: only when it is short (F9). A long combination lives in the tooltip, so the four big buttons never overflow.</summary>
    string Short(string action) { var n = _keys.Name(action); return n.Length <= 4 ? "  " + n : ""; }

    void RefreshHotkeyLabels()
    {
        StopBtn.Content = "■ Stop" + Short("stop"); StopBtn.ToolTip = $"Stop ({_keys.Name("stop")})";
        RecBtn.ToolTip = $"Record ({_keys.Name("record")})"; PlayBtn.ToolTip = $"Play ({_keys.Name("play")})"; RecPauseBtn.ToolTip = $"Pause ({_keys.Name("pause")})";
        ShowPathsBox.Content = $"Show paths while playing ({_keys.Name("paths")})";
        foreach (var (btn, action) in new[] { (HkRecord, "record"), (HkPlay, "play"), (HkPause, "pause"), (HkStop, "stop"), (HkPaths, "paths") })
            btn.Content = $"{HotkeySettings.Actions.First(a => a.Action == action).Label}: {_keys.Name(action)}";
    }

    void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        string action = (string)((System.Windows.Controls.Button)sender).Tag;
        string label = HotkeySettings.Actions.First(a => a.Action == action).Label;
        _hotKeys?.UnregisterAll();                                   // while waiting for the key, pressing a hotkey must not run it
        (int Mods, int Vk)? got;
        try { got = Views.Prompt.PressCombo(this, $"Press the new hotkey for {label}: hold any of Ctrl, Shift, Alt, Win and press a key (Ctrl+Shift+Alt+R, Shift+A). A function key alone also works. Esc cancels."); }
        finally { ApplyHotkeys(); }
        if (got is not { } g) return;
        var combo = new Combo(g.Mods, g.Vk);
        if (!HotkeySettings.Allowed(combo)) { SetStatus($"{HotkeySettings.Display(combo)} can't be a hotkey: a letter, digit, Space or Enter on its own would stop working everywhere else. Hold Ctrl, Shift, Alt or Win with it, or use a function key."); return; }
        if (_keys.Owner(combo, action) is { } other) { SetStatus($"{HotkeySettings.Display(combo)} is already the {other} hotkey."); return; }
        var old = _keys.Get(action);
        _keys.Set(action, combo);
        var failed = ApplyHotkeys();
        if (failed.Any(f => f.StartsWith(label + " ")))
        {
            _keys.Set(action, old); ApplyHotkeys();
            SetStatus($"Another program already uses {HotkeySettings.Display(combo)}; {label} stays on {_keys.Name(action)}.");
            return;
        }
        _keys.Save();
        SetStatus($"{label} is now {_keys.Name(action)}." + (HotkeySettings.StealsTyping(combo) ? $" Note: while Macro Studio is open, {_keys.Name(action)} no longer types in other programs. Ctrl, Alt or Win avoids that." : ""));
    }

    void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        _keys.Reset(); _keys.Save();
        var failed = ApplyHotkeys();
        SetStatus(failed.Count > 0 ? $"Defaults restored, but {string.Join(", ", failed)} is used by another program." : "Hotkeys are back to F9 record, F10 play, F11 pause, F8 stop, F12 paths.");
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotKeys = new HotKeys(this);
        DiscordEndBox.IsChecked = _discord.OnFinish; DiscordShotBox.IsChecked = _discord.ShotOnFinish;
        var failed = ApplyHotkeys();
        if (failed.Count > 0) SetStatus($"Hotkey(s) {string.Join(", ", failed)} are used by another program: pick another key in Options > Hotkeys, or use the buttons.");
        else SetStatus($"Ready. {_keys.Name("record")} record · {_keys.Name("play")} play · {_keys.Name("pause")} pause · {_keys.Name("stop")} stop · {_keys.Name("paths")} show/hide paths.");
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        RequestStop();
        if (_player is { } running) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (running.IsRunning && sw.ElapsedMilliseconds < 2000) Thread.Sleep(10); }   // let the playback thread end before its finder and watcher go
        _glide?.Cancel();
        _session?.Dispose(); _session = null;
        try { _stopTask?.Wait(3000); } catch { }   // a stop already draining on a worker thread finishes before Dispose would call Stop() again
        _recorder.Dispose();
        _hotKeys?.Dispose();
        _targetsPanel.Dispose();
        _finder?.Dispose();
        StopGuards();
        _paths?.Dispose();
        _watcher.Dispose();
    }

    /// <summary>Playback may fall back to the recorded click position only while the screen layout is the one it was recorded on.</summary>
    bool FallbackSafe() => FallbackSafe(_s.Desktop);

    /// <summary>No editor state: safe on the playback thread, with the layout captured when the run started.</summary>
    static bool FallbackSafe(DesktopInfo? rec)
    {
        var cur = Screens.Current();
        return rec != null && rec.Virtual == cur.Virtual && rec.Monitors.SequenceEqual(cur.Monitors);
    }

    // ---- status ----

    void SetStatus(string text) => StatusText.Text = text;

    void UpdateSummary()
    {
        int issues = _s.Analysis.Issues.Count;
        SummaryText.Text = $"{_s.Name}{(_s.Dirty ? " *" : "")}   ·   {_s.Events.Length:N0} events   ·   {EventInfo.Seconds(_s.Duration)}   ·   {_s.Selected.Count} selected";
        ProblemLink.Visibility = issues > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (issues > 0)
        {
            ProblemLink.Text = $"⚠ {issues} problem{(issues == 1 ? "" : "s")} — click to jump";
            ProblemLink.ToolTip = string.Join(Environment.NewLine, _s.Analysis.Issues.OrderBy(k => k.Key).Take(8).Select(k => $"#{k.Key + 1}: {k.Value}"));
        }
        Title = $"Macro Studio – {_s.Name}{(_s.Dirty ? "*" : "")}";
        _inspector?.Reload();
        if (_targetsPanel != null) SyncTargetsPanel();
        DiscordInfo.Text = _discord.HasOwn(_s.Id) ? "this recording has its own link" : _discord.Url != null ? "using the default link" : "no link set";
    }

    void ProblemLink_Click(object s, System.Windows.Input.MouseButtonEventArgs e) => GoToProblem();

    /// <summary>Select the next event with a problem after the current one (wrapping), move the playhead there and show it in the event list.</summary>
    void GoToProblem()
    {
        var bad = _s.Analysis.Issues.Keys.OrderBy(k => k).ToList();
        if (bad.Count == 0) return;
        int cur = _s.IndexOfId(_s.Primary);
        int ix = bad.FirstOrDefault(k => k > cur, bad[0]);
        if (_bottomCollapsed) SetBottomCollapsed(false);
        BottomTabs.SelectedIndex = 0;
        _s.SetPlayhead(_s.Events[ix].T);
        _s.SelectOne(_s.Events[ix].Id);
        SetStatus($"Problem at event #{ix + 1}: {_s.Analysis.Issues[ix]}  (click again for the next one)");
    }

    void Tick()
    {
        if (_recorder.IsRecording)
            SetStatus($"{(_recorder.IsPaused ? "● Paused" : "● Recording")}   {_recorder.Count:N0} events   {EventInfo.Seconds(_recorder.ElapsedMs)}   ({_keys.Name("record")} stop, {_keys.Name("pause")} pause)" + (_insertAt is { } ia ? $"   · inserting at {EventInfo.Seconds(ia)}" : "") + (_recorder.Warning is { } warn ? $"   ⚠ {warn}" : ""));
        else if (_playing && _player != null)
        {
            int i = Math.Clamp(_player.CurrentIndex, 0, Math.Max(0, _playEvents.Length - 1));
            if (_playEvents.Length > 0) _s.SetPlayhead(_playEvents[i].T);
            if (_visiblePlay && Environment.TickCount64 - _lastPlaced > 800) AvoidPaths();
            string inside = _flow?.Trail.Text is { Length: > 0 } tr ? $"   · inside: {_s.Name} › {tr}" : "";   // a connected macro (and the ones it runs) is still running
            SetStatus($"{(_player.IsPaused ? "⏸ Paused" : "▶ Playing")}   event {i + 1:N0} / {_playEvents.Length:N0}{inside}   max lateness {_player.MaxLatenessMs:0.0} ms");
        }
        SlowTick();
        RecBtn.Content = _recorder.IsRecording ? $"■ Stop recording{Short("record")}" : $"● Record{Short("record")}";
        PlayBtn.Content = _playing ? $"■ Stop{Short("play")}" : $"▶ Play{Short("play")}";
        bool busy = _recorder.IsRecording || _playing;
        RecBtn.IsEnabled = !_playing;
        PlayBtn.IsEnabled = !_recorder.IsRecording;
        PlaySelBtn.IsEnabled = TlPlaySelBtn.IsEnabled = !busy;
        StepBtn.IsEnabled = TlStepBtn.IsEnabled = !_recorder.IsRecording;
        RecPauseBtn.IsEnabled = _recorder.IsRecording;
        RecPauseBtn.Content = _recorder.IsPaused ? $"▶ Resume{Short("pause")}" : $"⏸ Pause{Short("pause")}";
    }

    // ---- recording ----

    void ToggleRecord() { if (_recorder.IsRecording) StopRecord(); else StartRecord(); }

    void StartRecord(bool forceNew = false)
    {
        if (_playing) return;
        // A macro is open and the playhead is somewhere after its start: record INTO it, at that time. Otherwise a new recording, as before.
        bool into = !forceNew && _s.Events.Length > 0 && _s.PlayheadMs > 0;
        if (!into && !forceNew && !ConfirmDiscard()) return;
        try { _recorder.Start(); }
        catch (Exception ex) { MessageBox.Show(this, "Could not start recording: " + ex.Message, "Macro Studio"); return; }
        _insertAt = into ? _s.PlayheadMs : null;
        _recorder.Note($"options: always on top {Topmost}, recording into an open macro {into}, window state {WindowState}");
        if (Topmost)
        {
            _hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            _recVisible = true;
            Views.WindowPlacement.NoActivate(_hwnd, true);
            _recorder.Ignore = OverOwnWindow; // this window's own clicks stay out of the recording
            if (!into) { _liveHave = 0; _liveEpoch = -1; _live.Start(); }         // a brand-new recording is drawn as it grows
            return;
        }
        _restoreState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        WindowState = WindowState.Minimized; // keeps this window's own clicks out of the recording
    }

    bool _stopping;
    Task<Macro>? _stopTask;   // the drain running on a worker thread: OnClosing waits for it before Recorder.Dispose would Stop() again

    /// <summary>
    /// Recorder.Stop waits for the hook thread and the queued events to drain; a stuck consumer makes that take seconds.
    /// It runs on a worker thread so the window stays alive meanwhile. Everything after the await is back on the UI thread.
    /// </summary>
    async void StopRecord()
    {
        if (_stopping) return;   // a second Stop while the first is still draining would call Recorder.Stop concurrently
        _stopping = true;
        Macro macro;
        try { macro = await (_stopTask = Task.Run(() => _recorder.Stop(_handlerFor is { } hf ? $"{hf.ParentName} - {hf.TargetName} handler" : $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}"))); }
        catch (Exception ex) { SetStatus("Could not stop the recording cleanly: " + ex.Message); return; }
        finally { _stopping = false; }
        if (_recVisible) { _live.Stop(); _recVisible = false; _recorder.Ignore = null; Views.WindowPlacement.NoActivate(_hwnd, false); }
        else { WindowState = _restoreState; Activate(); }
        if (_insertAt is { } at)
        {
            _insertAt = null;
            _s.InsertBlock(macro.Events, at, ripple: true);   // one undo step; later events move out of the way
            SetStatus($"Recorded {macro.Events.Count:N0} events into '{_s.Name}' at {EventInfo.Seconds(at)}. Ctrl+Z undoes it; save the macro to keep it.");
            return;
        }
        var path = _lib.UniquePath(macro.Name);
        macro.Name = MacroLibrary.NameOf(path);
        MacroSerializer.Save(macro, path); // auto-saved so a fresh recording is never lost
        LoadMacro(macro, path);
        RefreshLibrary(path);
        SetStatus($"Recorded {macro.Events.Count:N0} events and saved to the library. Log: {_recorder.LogPath}");
        if (_handlerFor is { } h) { _handlerFor = null; LinkHandler(h, path); }
    }

    // "+ Record" on a When block: save the main macro, record a new macro, connect it, and come back to the main macro
    (string ParentPath, int Index, string ParentName, string TargetName)? _handlerFor;

    void RecordHandler()
    {
        if (_recorder.IsRecording || _playing || _s.PrimaryEvent is not { Kind: EventKind.When } blk) return;
        if (_s.Path == null || _s.Dirty) { SaveCurrent(); if (_s.Path == null) return; }   // the main macro must be on disk to be reopened afterwards
        _handlerFor = (_s.Path, _s.IndexOfId(blk.Id), _s.Name, blk.Target == null ? "popup" : EventInfo.TargetLabel(blk.Target));
        StartRecord(forceNew: true);
        if (!_recorder.IsRecording) _handlerFor = null;
        else SetStatus("Recording the handler. Do what should happen, then stop the recording (the usual stop hotkey).");
    }

    void LinkHandler((string ParentPath, int Index, string ParentName, string TargetName) h, string handlerPath)
    {
        try
        {
            string id = _lib.EnsureId(handlerPath);
            var parent = MacroSerializer.Load(h.ParentPath);
            var evs = parent.Events.OrderBy(e => e.T).ToList();
            if (h.Index < 0 || h.Index >= evs.Count || evs[h.Index].Kind != EventKind.When) { SetStatus("Recorded and saved, but the When block moved: connect it by dragging the macro onto the block."); return; }
            evs[h.Index] = evs[h.Index] with { Flow = (evs[h.Index].Flow ?? new FlowOptions()) with { Macro = id, Do = FlowDo.RunMacro } };
            parent.Events = evs;
            MacroSerializer.Save(parent, h.ParentPath);
            LoadMacro(MacroSerializer.Load(h.ParentPath), h.ParentPath);
            _s.Expanded.Add(_s.Events[h.Index].Id);
            _s.SelectOne(_s.Events[h.Index].Id);
            RefreshLibrary(h.ParentPath);
            SetStatus($"Recorded '{MacroLibrary.NameOf(handlerPath)}' and connected it to the When block.");
        }
        catch (Exception ex) { SetStatus("Recorded, but could not connect it: " + ex.Message); }
    }

    void TogglePause()
    {
        if (_recorder.IsRecording) { if (_recorder.IsPaused) _recorder.Resume(); else _recorder.Pause(); }
        else if (_playing && _player != null) { if (_player.IsPaused) _player.Resume(); else _player.Pause(); }
    }

    void StopAll()
    {
        if (_recorder.IsRecording) StopRecord();
        if (_playing) RequestStop();
    }

    // ---- playback ----

    /// <summary>Stops at any depth (nested players watch the main player). Also cancels a start still waiting for the window to get out of the way.</summary>
    void RequestStop() { if (_starting) _cancelStart = true; _player?.Stop(); }

    void TogglePlay() { if (_playing) RequestStop(); else StartPlay(0, int.MaxValue, false); }

    async void StartPlay(int from, int to, bool step)
    {
        if (_playing || _recorder.IsRecording || _s.Events.Length == 0) return;
        if (_s.Analysis.Issues.Count > 0 &&
            MessageBox.Show(this, $"This macro has {_s.Analysis.Issues.Count} problem(s) (see the Issue column). Play anyway?\nHeld keys and buttons are always released when playback ends.",
                "Macro Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { GoToProblem(); return; }
        if (!int.TryParse(LoopsBox.Text, out int loops) || loops < 0) loops = 1;
        if (InfiniteBox.IsChecked == true) loops = 0;   // 0 = until stopped

        _glide?.Cancel();                                   // the pointer glide after the last run must not fight this one
        _starting = true; _cancelStart = false;
        try
        {
            // Everything the playback thread will read is captured HERE, on the UI thread: it must never touch a control or the editor (a past bug: AdaptBox.IsChecked on the playback thread).
            var events = _playEvents = _s.Events;
            var desk = Screens.Current();
            var curEnv = Screens.CurrentEnv();
            var mainDesk = _s.Desktop; var mainEnv = _s.Env;
            bool adaptOn = AdaptBox.IsChecked == true;
            string? unsaved = _s.Dirty ? _s.Path : null;
            string? playId = _playId = _s.Id; _playName = _s.Name;
            _log.Clear();                                       // one log per playback: the saved file and the Run log tab show this run only
            _session?.Dispose();
            var session = _session = PlaybackSession.For(_lib, _log, p => unsaved != null && string.Equals(p, unsaved, StringComparison.OrdinalIgnoreCase));   // library read once for the whole run
            var used = session.UsedTargets(events);             // every nested level, read once
            _finder?.Dispose();
            StopGuards();
            var finder = _finder = new ScreenTargetFinder(_watcher, _targets.List().Where(t => used.Contains(t.Id)).ToList()); // watches only what this macro (and the macros its When blocks run) clicks
            // chain: Player -> adapt to this PC -> bend recorded approach paths to moved targets -> adaptive steps (glide, click, verify) -> real input
            _visiblePlay = !step && KeepVisibleBox.IsChecked == true;
            Player? player = null;
            _trace = new PathTrace(new DiscordSink(new InputInjector(desk.Virtual), () => _discord.UrlFor(playId), _log, () => player?.CurrentIndex ?? -1));
            IInputSink injector = _trace;
            if (_visiblePlay) { var h = _hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); injector = new WindowShield(injector, () => Views.WindowPlacement.Bounds(h), on => Views.WindowPlacement.PassThrough(h, on)); }
            // one full chain per macro: the main one here, and one for every connected macro when its When block fires (built on the playback thread: only captured values are read)
            (IInputSink Play, StepExecutor Exec, AdaptingSink? Adapt) Chain(MacroEvent[] evs, DesktopInfo? srcDesk, EnvInfo? srcEnv, Func<int> index, ExecutionLog log)
            {
                AdaptingSink? ad = null;
                var exec = new StepExecutor(injector, finder, new RealClock(), () => player?.StopRequested == true, log, index, () => FallbackSafe(srcDesk ?? mainDesk),
                    () => player?.Speed ?? 1, () => _dynamicPath, Screens.CursorPos, correctCursor: true);
                IInputSink s = new TrajectorySink(exec, evs, session.PlanFor(evs), finder, index, p => ad?.MapPoint(p.X, p.Y) ?? p, () => _dynamicPath, log, () => player?.Speed ?? 1,
                    p => (Math.Clamp(p.X, desk.Virtual.X, desk.Virtual.X + desk.Virtual.Width - 1), Math.Clamp(p.Y, desk.Virtual.Y, desk.Virtual.Y + desk.Virtual.Height - 1)));
                if (adaptOn) { ad = new AdaptingSink(s, srcDesk, srcEnv, desk, curEnv); s = ad; }
                return (s, exec, ad);
            }
            var (sink, executor, adapt) = Chain(events, mainDesk, mainEnv, () => player?.CurrentIndex ?? -1, _log);
            if (adapt is { IsAdapting: true }) SetStatus($"Adapting recording ({mainDesk?.Virtual.Width}×{mainDesk?.Virtual.Height}, {mainEnv?.WheelLines ?? 0} scroll lines) to this PC ({desk.Virtual.Width}×{desk.Virtual.Height}, {curEnv.WheelLines} lines). ");
            player = _player = new Player(sink) { Log = _log, BreakAt = i => i >= 0 && i < events.Length && _s.IsBreakpoint(events[i].Id) };
            player.Speed = Speeds[Math.Max(0, SpeedBox.SelectedIndex)];
            var flow = new FlowRunner(finder, new RealClock(), () => player.StopRequested, executor,
                id => session.Events(id),                        // from the per-run cache: a macro called 100 times is read once
                (evs, index) => { var (src, name) = session.SourceOf(evs); var c = Chain(evs, src?.Desktop, src?.Env, index, name != null ? _log.Scoped(name) : _log); return (c.Play, c.Exec); },
                () => player.Speed, _log, macroKey: playId ?? "");
            player.Flow = flow; _flow = flow;
            executor.Idle = () => { var f = flow.Poll(); if (f == FlowStep.Restart) throw new FlowRestartException(); if (f == FlowStep.Stop) throw new FlowStopException(); };   // a watching When block may act while a click waits for its target
            player.Finished += () => Dispatcher.BeginInvoke(OnPlayFinished);
            _playing = true;
            _log.Add(-1, "playback", LogResult.Ok, $"started: {events.Length:N0} events, speed {player.Speed}x" + (step ? ", step-by-step" : "") + $", {used.Count} visual target(s) watched");
            StartGuards();
            Func<(int X, int Y), (int X, int Y)> map = pt => adapt?.MapPoint(pt.X, pt.Y) ?? pt;
            _recordedPath = PathTrace.Recorded(events, map);
            _endPoint = _recordedPath.Count > 0 ? _recordedPath[^1] : null;
            _recordedPath.AddRange(events.Where(e => e.Kind == EventKind.TargetClick && e.Step is { } st && (st.FallbackX != 0 || st.FallbackY != 0)).Select(e => map((e.Step!.FallbackX, e.Step!.FallbackY))));
            var names = _targets.List().ToDictionary(t => t.Id, t => t.Name);
            _paths?.Dispose();                       // the previous run's paths stay up after it ends, until hidden or replaced here
            _paths = new Views.PathOverlay(_recordedPath, _trace, CapturablePathsBox.IsChecked == true, () =>
            {
                var l = new List<(ScreenRect, string)>();
                foreach (var id in used) if (_watcher.Get(id) is { Hit: { } h } w && (DateTime.UtcNow - w.SeenUtc).TotalMilliseconds < 1500 && names.TryGetValue(id, out var nm))
                    l.Add((new ScreenRect(h.Rect.X, h.Rect.Y, h.Rect.Width, h.Rect.Height), nm));
                return l;
            }, ShowPathsBox.IsChecked == true);
            if (_visiblePlay) { MakeRoomForPlayback(); Views.WindowPlacement.PlaybackMode(_hwnd, true); }
            // stays on screen: never takes focus, hidden from capture, replayed clicks pass through it
            else if (!step)
            {
                _restoreState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
                WindowState = WindowState.Minimized; // get out of the way of the replayed clicks
                await Task.Delay(350);
            }
            if (_cancelStart) { _cancelStart = false; _log.Add(-1, "playback", LogResult.Ok, "stopped before it started"); player.Stop(); OnPlayFinished(); return; }   // Stop pressed during the start-up gap
            player.Start(events, from, to, loops, step);
            _starting = false;
            if (_cancelStart) { _cancelStart = false; player.Stop(); }   // a Stop that came in just before Start would have been wiped by Start's reset
        }
        catch (Exception ex)
        {
            _starting = false;
            _log.Add(-1, "playback", LogResult.Failed, "could not start: " + ex.Message);
            _playing = true;                                    // so the normal clean-up runs
            FinishRun("could not start: " + ex.Message);
        }
    }

    void OnPlayFinished() => FinishRun(null);

    /// <summary>The run is over (finished, stopped, failed, or never started): release everything and show a clean idle state. Safe to call twice.</summary>
    void FinishRun(string? startError)
    {
        if (!_playing) return;
        var shot = startError == null && _discord.OnFinish && _discord.ShotOnFinish && _discord.UrlFor(_playId) != null ? DiscordWebhook.Screenshot() : null;   // first, before the window comes back and covers the screen
        _playing = false; _starting = false;
        _finder?.Dispose(); _finder = null;
        _flow = null;                                           // the "inside:" trail of this run goes with it
        var session = _session; _session = null; session?.Dispose();   // loaded connected macros and the id map are released
        StopGuards();
        if (_visiblePlay) { Views.WindowPlacement.PassThrough(_hwnd, false); Views.WindowPlacement.PlaybackMode(_hwnd, false); RestoreWindow(); _visiblePlay = false; }
        if (WindowState == WindowState.Minimized) { WindowState = _restoreState; Activate(); }
        var p = startError == null ? _player : null;
        if (p is { Error: null, StopRequested: false } && _endPoint is { } end && _trace != null)
        {   // the pointer finishes where it finished in the recording
            var tr = _trace; double sp = Math.Clamp(p.Speed, 0.1, 50);
            _glide?.Dispose(); var cts = _glide = new CancellationTokenSource(); var tok = cts.Token;
            _ = Task.Run(() => GlideTo(tr, end, sp, tok));
        }
        string outcome = startError != null ? "stopped: " + startError : p?.Error != null ? "stopped: " + p.Error.Message : p?.StopRequested == true ? "stopped by you" : "finished";
        int bad = _log.Count(LogResult.Failed), warn = _log.Count(LogResult.Warn) + _log.Count(LogResult.Retry);
        _log.Add(-1, "playback", startError != null || p?.Error != null ? LogResult.Failed : LogResult.Ok, startError != null || p?.Error != null ? outcome : "finished");
        SavePlayLog(_playName, outcome);
        if (_discord.OnFinish && _discord.UrlFor(_playId) is { } hook)
            SendDiscord(hook, () => shot, $"**{_playName}** " + (startError != null || p?.Error != null ? $"❌ {outcome}" : p?.StopRequested == true ? "⏹ stopped by you" : "✅ finished") + (bad + warn > 0 ? $" ({bad} failed, {warn} warnings/retries)" : ""));
        SetStatus(startError != null ? "Playback could not start: " + startError : p?.Error != null ? "Playback stopped: " + p.Error.Message : $"Playback finished. Max lateness {p?.MaxLatenessMs:0.0} ms" + (bad + warn > 0 ? $"; log: {bad} failed, {warn} warnings/retries (File > Logs, or the Run log tab)." : "."));
    }

    /// <summary>Smooth straight movement (slow start, fast middle, slow end) from the real pointer to <paramref name="end"/>. Stops at once when cancelled (a new run, or the window closing).</summary>
    static void GlideTo(IInputSink sink, (int X, int Y) end, double speed, CancellationToken stop)
    {
        try
        {
            var from = Screens.CursorPos();
            double d = Math.Sqrt(Math.Pow(end.X - from.X, 2) + Math.Pow(end.Y - from.Y, 2));
            int steps = Math.Max(1, (int)(Math.Clamp(120 + d * 0.45, 120, 900) / speed / 8));
            for (int k = 1; k <= steps && !stop.IsCancellationRequested; k++)
            {
                double t = (double)k / steps, e = t * t * t * (10 - 15 * t + 6 * t * t);
                var mv = new MacroEvent(0, EventKind.MouseMove, X: (int)Math.Round(from.X + (end.X - from.X) * e), Y: (int)Math.Round(from.Y + (end.Y - from.Y) * e));
                sink.Send(in mv);
                Thread.Sleep(8);
            }
        }
        catch { }
    }

    // ---- compact (TinyTask-style) layout: a small controller; the full editor opens on demand ----
    bool _compact = true;
    System.Windows.Shell.WindowChrome Chrome => System.Windows.Shell.WindowChrome.GetWindowChrome(this);
    (double W, double H)? _fullSize;

    // ---- custom title bar and rounded window ----
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void InitChrome()
    {
        SourceInitialized += (_, _) => { Views.WindowPlacement.RoundCorners(this); Views.WindowPlacement.MaximizeToWorkArea(this); };
        StateChanged += (_, _) =>
        {
            if (_compact && WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; return; }   // the compact window is fixed size (a title-bar double-click would maximise it)
            bool max = WindowState == WindowState.Maximized;
            MaxBtn.Content = max ? "\uE923" : "\uE922";
        };
    }

    void Infinite_Click(object sender, RoutedEventArgs e) => LoopsBox.IsEnabled = InfiniteBox.IsChecked != true;

    // ---- the bottom panel (events, run log, detections) folds down to its tab strip ----
    bool _bottomCollapsed;

    void SetBottomCollapsed(bool collapsed)
    {
        _bottomCollapsed = collapsed;
        BottomTabs.Tag = collapsed ? "collapsed" : null;
        BottomRow.Height = collapsed ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        BottomRow.MinHeight = collapsed ? 0 : 100;
        BottomSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseBtn.Content = collapsed ? "" : "";
        CollapseBtn.ToolTip = collapsed ? "Open the panel again" : "Fold the panel away (only the tabs stay). Click a tab to open it again.";
    }

    void CollapseBottom_Click(object sender, RoutedEventArgs e) => SetBottomCollapsed(!_bottomCollapsed);

    /// <summary>Click the open tab again to fold the panel; click any tab while it is folded to open it.</summary>
    void BottomTabs_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        for (; d != null && d is not System.Windows.Controls.TabItem; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) { }
        if (d is not System.Windows.Controls.TabItem tab) return;
        if (_bottomCollapsed) SetBottomCollapsed(false);                       // the click also selects that tab
        else if (tab.IsSelected) { SetBottomCollapsed(true); e.Handled = true; }
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        bool open = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OptionsBtn.Content = open ? "⚙ Options ▴" : "⚙ Options ▾";
    }

    void TopMost_Click(object sender, RoutedEventArgs e) => Topmost = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;

    void Editor_Click(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    void SetCompact(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;
        EditorBtn.Content = compact ? "⤢ Editor" : "⤡ Compact";
        MaxBtn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            _fullSize = (ActualWidth, ActualHeight);
            Editor.Visibility = PerfBar.Visibility = Visibility.Collapsed;
            OptionChecks.Columns = 2; OptionsStepRow.Visibility = Visibility.Visible;   // the editor has these two above its timeline; the compact window keeps them in Options
            ToolbarLayout(compact: true);
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            // Locked by size limits and no resize border. ResizeMode is never changed at runtime: doing so breaks dragging by the custom title bar.
            Chrome.ResizeBorderThickness = new Thickness(0);
            MinWidth = 600; MaxWidth = 600; MinHeight = 0; Width = 600; SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Chrome.ResizeBorderThickness = new Thickness(6);
            MaxWidth = double.PositiveInfinity; MinWidth = 1000; MinHeight = 600;
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(_fullSize?.W ?? 1500, wa.Width); Height = Math.Min(_fullSize?.H ?? 900, wa.Height);
            Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width)); Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
            Editor.Visibility = PerfBar.Visibility = Visibility.Visible;
            OptionChecks.Columns = 4; OptionsStepRow.Visibility = Visibility.Collapsed;
            ToolbarLayout(compact: false);
        }
    }

    /// <summary>Compact window: the toolbar stacks (buttons, speed row, status). Editor: one slim row, since the window is wide and the editor needs the height.</summary>
    void ToolbarLayout(bool compact)
    {
        void Place(FrameworkElement e, int row, int col, int span) { Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span); }
        if (compact)
        {
            Place(MainButtons, 0, 0, 3); Place(SpeedRow, 1, 0, 3); Place(StatusPill, 2, 0, 3);
            SpeedRow.Margin = new Thickness(3, 8, 3, 0); StatusPill.Margin = new Thickness(3, 10, 3, 0);
            StatusPill.VerticalAlignment = VerticalAlignment.Stretch; StatusText.TextWrapping = TextWrapping.Wrap;
            ToolbarCard.Padding = new Thickness(12);
            SpeedRow.HorizontalAlignment = HorizontalAlignment.Stretch; SpeedRow.VerticalAlignment = MainButtons.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Place(MainButtons, 0, 0, 1); Place(SpeedRow, 0, 1, 1); Place(StatusPill, 0, 2, 1);
            SpeedRow.Margin = new Thickness(18, 0, 0, 0); StatusPill.Margin = new Thickness(18, 0, 3, 0);
            StatusPill.VerticalAlignment = VerticalAlignment.Center; StatusText.TextWrapping = TextWrapping.NoWrap;
            SpeedRow.VerticalAlignment = VerticalAlignment.Center; MainButtons.VerticalAlignment = VerticalAlignment.Center;
            ToolbarCard.Padding = new Thickness(8, 6, 8, 6);
        }
    }

    /// <summary>Posts to Discord on a worker thread: the screenshot capture and the HTTP call never block the UI.</summary>
    void SendDiscord(string hook, Func<byte[]?> shot, string text, Action<string?>? done = null) => _ = Task.Run(async () =>
    {
        var err = await DiscordWebhook.Post(hook, text, null, shot());
        _log.Add(-1, "discord", err == null ? LogResult.Ok : LogResult.Warn, err ?? "message sent");
        if (done != null) _ = Dispatcher.BeginInvoke(() => done(err));
    });

    /// <summary>Saves a link for THIS recording (private store, keyed by the recording's Id). Returns an error text, or null when saved.</summary>
    string? SetRecordingLink(string link)
    {
        link = link.Trim();
        if (!DiscordWebhook.IsValid(link)) return "That is not a Discord webhook link (https://discord.com/api/webhooks/...).";
        bool nothingPending = !_s.Dirty && _s.Path != null;
        _s.Id ??= Guid.NewGuid().ToString("N");
        _discord.SetLink(_s.Id, link); _discord.Save();
        if (nothingPending) SaveCurrent(); else _s.Dirty = true;   // the Id lives in the recording, so the recording must be saved too
        UpdateSummary();
        return null;
    }

    void ClearRecordingLink()
    {
        if (_s.Id != null) _discord.SetLink(_s.Id, null);
        _discord.Save(); UpdateSummary();
    }

    /// <summary>What the inspector's Discord card shows and does. The card never sees the real link, only a masked copy.</summary>
    DiscordHooks DiscordHooksForInspector() => new(
        () => (DiscordWebhook.Mask(_discord.UrlFor(_s.Id)), _discord.HasOwn(_s.Id) ? "own" : _discord.Url != null ? "default" : "none"),
        SetRecordingLink,
        ClearRecordingLink,
        done =>
        {
            var url = _discord.UrlFor(_s.Id);
            if (url == null) { done("no link set"); return; }
            SendDiscord(url, DiscordWebhook.Screenshot, $"✅ Test message from Macro Studio for **{_s.Name}**.", done);
        });

    void Discord_Click(object sender, RoutedEventArgs e)
    {
        var s = Views.Prompt.Ask(this, $"Discord webhook for '{_s.Name}' (empty = use the default link)", "");
        if (s == null) return;
        s = s.Trim();
        if (s.Length == 0)
        {
            ClearRecordingLink();
            SetStatus(_discord.Url != null ? "This recording now uses the default Discord link." : "This recording has no Discord link.");
            return;
        }
        var err = SetRecordingLink(s);
        if (err != null) { SetStatus(err + " Nothing was saved."); return; }
        SetStatus("Link saved for this recording. Sending a test message...");
        SendDiscord(s, DiscordWebhook.Screenshot, $"✅ Macro Studio is connected to this channel for **{_s.Name}**.", e2 => SetStatus(e2 == null ? "Link saved for this recording; the test message arrived." : $"Link saved, but the test message failed: {e2}."));
    }

    /// <summary>The default webhook: used by every recording that has none of its own.</summary>
    void DiscordDefault_Click(object sender, RoutedEventArgs e)
    {
        var s = Views.Prompt.Ask(this, "Default Discord webhook (leave empty to remove it)", "");
        if (s == null) return;
        s = s.Trim();
        if (s.Length == 0) { _discord.Url = null; _discord.Save(); UpdateSummary(); SetStatus("Default Discord webhook removed."); return; }
        if (!DiscordWebhook.IsValid(s)) { SetStatus("That is not a Discord webhook address (https://discord.com/api/webhooks/...). Nothing was saved."); return; }
        _discord.Url = s; _discord.Save(); UpdateSummary();
        SetStatus("Default webhook saved. Sending a test message...");
        SendDiscord(s, DiscordWebhook.Screenshot, "✅ Macro Studio is connected to this channel (default link).", err => SetStatus(err == null ? "Default webhook saved; the test message arrived." : $"Default webhook saved, but the test message failed: {err}."));
    }

    void DiscordEnd_Click(object sender, RoutedEventArgs e)
    {
        _discord.OnFinish = DiscordEndBox.IsChecked == true; _discord.ShotOnFinish = DiscordShotBox.IsChecked == true; _discord.Save();
        if (_discord.OnFinish && _discord.UrlFor(_s.Id) == null) SetStatus("Set a link first: Options > Link for this recording (or Default link).");
    }

    void InsertDiscord_Click(object sender, RoutedEventArgs e)
    {
        var text = Views.Prompt.Ask(this, "Message to send to Discord at this point of the macro", "Macro reached this step");
        if (string.IsNullOrWhiteSpace(text)) return;
        bool shot = MessageBox.Show(this, "Attach a screenshot of the screen to this message?", "Macro Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _s.InsertBlock([new MacroEvent(0, EventKind.Discord, Extended: shot, Target: text.Trim())], _s.PlayheadMs, ripple: true);
        SetStatus(_discord.UrlFor(_s.Id) == null ? "Discord step added. Set a link first: Options > Link for this recording." : "Discord step added at the playhead.");
    }

    void DynamicPath_Click(object sender, RoutedEventArgs e) => _dynamicPath = DynamicPathBox.IsChecked == true;

    void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player != null && SpeedBox.SelectedIndex >= 0) _player.Speed = Speeds[SpeedBox.SelectedIndex];
    }

    // ---- toolbar ----

    void Record_Click(object s, RoutedEventArgs e) => ToggleRecord();
    void Pause_Click(object s, RoutedEventArgs e) => TogglePause();
    void Play_Click(object s, RoutedEventArgs e) => TogglePlay();
    void Stop_Click(object s, RoutedEventArgs e) => StopAll();

    void PlayFromSel_Click(object s, RoutedEventArgs e)
    {
        int i = _s.IndexOfId(_s.Primary);
        if (_s.Selected.Count > 1)
        {
            var idx = _s.Selected.Select(_s.IndexOfId).Where(x => x >= 0).ToList();
            StartPlay(idx.Min(), idx.Max() + 1, false); // several selected: play just that range
        }
        else StartPlay(Math.Max(0, i), int.MaxValue, false);
    }

    void Step_Click(object s, RoutedEventArgs e)
    {
        if (_playing && _player != null) { if (!_starting) _player.Step(); }   // a When block counts as ONE step: its connected macro runs to the end, then the main macro waits at the next step
        else
        {
            int i = _s.IndexOfId(_s.Primary);
            StartPlay(Math.Max(0, i), int.MaxValue, true);
            _ = Task.Run(async () => { await Task.Delay(100); Dispatcher.Invoke(() => { if (_playing && !_starting) _player?.Step(); }); });
        }
    }

    // ---- library / files ----

    void RefreshLibrary(string? select)
    {
        _loadingLibrary = true;
        var items = _lib.ListTree(_libOpen);
        LibList.ItemsSource = items;
        LibList.SelectedItem = select == null ? null : items.FirstOrDefault(i => i.Path == select);
        _loadingLibrary = false;
        UpdateInheritedTargets();                                                    // moving macros between folders changes which targets they inherit
        if (_s.Path != null && File.Exists(_s.Path)) _s.Subs = _lib.ReadSubs(_s.Path);   // moves are written to the files; the open macro must not save an old list over them
        _grid?.Invalidate();        // a connected macro may have been edited, renamed or deleted
    }

    // ---- When blocks: insert, connect a macro by drag and drop, open the connected macro ----

    void InsertWhen(double atT)
    {
        var ids = _s.InsertBlock([new MacroEvent(0, EventKind.When, Flow: new FlowOptions { WaitMs = 30_000 }, Step: new StepOptions())], atT, ripple: true);   // 30 s default wait: a screen that is still loading is the usual reason to wait
        if (ids.Count > 0) _s.SelectOne(ids[0]);
        SetStatus("When block added. Pick its visual target in the inspector, then drag a macro from the library onto it.");
    }

    void InsertWhen_Click(object sender, RoutedEventArgs e) => InsertWhen(_s.PlayheadMs);

    /// <summary>Whatever macro is connected to a When block is also filed in the open macro's folder, so it is listed under it in the library and offered in the block's drop-down.</summary>
    string? FileUnderOpenMacro(string macroId)
    {
        if (macroId == _s.Id) return "A block cannot run the macro it is in.";
        // a macro that already lives in a folder is only REFERENCED here: connecting it must not move it out of its own folder
        if (!_s.Subs.Contains(macroId) && !(_lib.PathOfId(macroId) is { } home && _lib.FolderOf(home) != null))
        {
            if (_s.Path != null) { if (_lib.AddSub(_s.Path, macroId) is { } err) return err; _libOpen.Add(_s.Path); }   // a loose macro is filed in this macro's folder, written to its file now
            else _s.Subs.Add(macroId);                                                                                   // not saved yet: kept with the macro when it is saved
            RefreshLibrary(_s.Path);
        }
        return null;
    }

    void GridMacroDropped(int eventIndex, string macroId)
    {
        if (eventIndex < 0 || eventIndex >= _s.Events.Length) return;
        var ev = _s.Events[eventIndex];
        if (FileUnderOpenMacro(macroId) is { } problem) { SetStatus(problem); return; }
        _s.Expanded.Add(ev.Id);                                  // show it right away, nested under the block
        _s.Modify([ev.Id], x => x with { Flow = (x.Flow ?? new FlowOptions()) with { Macro = macroId, Do = FlowDo.RunMacro } });
        _s.SelectOne(ev.Id);
        SetStatus($"Connected '{_lib.NameOfId(macroId) ?? "macro"}': it runs when this block's condition is met.");
    }

    void OpenConnectedMacro(string macroId)
    {
        if (_lib.PathOfId(macroId) is not { } p) { SetStatus("That macro is not in the library."); return; }
        if (!ConfirmDiscard()) return;
        try { LoadMacro(MacroSerializer.Load(p), p); RefreshLibrary(p); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open the macro: " + ex.Message, "Macro Studio"); }
    }

    // dragging a macro out of the library (a plain click still opens it, but only when the button is released)
    readonly HashSet<string> _libOpen = new(StringComparer.OrdinalIgnoreCase);   // library macros whose connected macros are listed under them
    LibraryItem? _dragCand;
    List<LibraryItem> _dragMany = [];
    Point _dragStart;

    void LibList_PreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragCand = null;
        if (e.OriginalSource is FrameworkElement { Tag: "arrow", DataContext: LibraryItem folder })   // the folder arrow: list / hide the connected macros
        {
            if (!_libOpen.Remove(folder.Path)) _libOpen.Add(folder.Path);
            RefreshLibrary(_s.Path);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is DependencyObject blank && ItemsControl.ContainerFromElement(LibList, blank) == null
            && !IsScrollBarPart(blank))
        {   // empty space in the list: nothing selected, so a paste goes to the top of the list
            LibList.UnselectAll();
            LibList.Focus();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && Keyboard.Modifiers == ModifierKeys.None && ItemsControl.ContainerFromElement(LibList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: LibraryItem { HasKids: true } dbl })
        {
            if (!_libOpen.Remove(dbl.Path)) _libOpen.Add(dbl.Path);
            RefreshLibrary(_s.Path);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;     // Ctrl / Shift click = multi-select, as before
        if (ItemsControl.ContainerFromElement(LibList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: LibraryItem li })
        {
            _dragCand = li; _dragStart = e.GetPosition(null);
            _dragMany = LibList.SelectedItems.Count > 1 && LibList.SelectedItems.Contains(li) ? LibList.SelectedItems.OfType<LibraryItem>().ToList() : [li];   // press on one of several selected macros: they all go together
            LibList.Focus();
            e.Handled = true;                                    // selecting (= opening) waits for the release, so a drag does not replace the open macro
        }
    }

    void LibList_PreviewMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCand is not { } li || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCand = null;
        try
        {
            var moving = _dragMany.Count > 0 ? _dragMany : [li];
            var ids = moving.Select(x => _lib.EnsureId(x.Path)).ToArray();
            var data = new DataObject(Views.MacroDrag.Format, ids[0]);                              // the first one: what a single drop slot takes
            data.SetData(Views.MacroDrag.Many, ids);
            data.SetData(Views.MacroDrag.Parents, moving.Select(x => x.Parent ?? "").ToArray());    // where each came from: dropping on empty space takes them out of their folders
            DragDrop.DoDragDrop(LibList, data, DragDropEffects.Link);
        }
        catch (Exception ex) { SetStatus("Could not start dragging: " + ex.Message); }
    }

    // dropping a macro on another macro in the list files it under that one (the target becomes its folder)
    ListBoxItem? _dropItem;
    void SetDropItem(ListBoxItem? it)
    {
        if (ReferenceEquals(it, _dropItem)) return;
        void Paint(ListBoxItem? x, bool on)
        {
            if (x?.Template?.FindName("Bd", x) is not Border bd) return;
            if (on) { bd.Background = Views.Theme.AccentSoft; bd.BorderBrush = Views.Theme.Accent; bd.BorderThickness = new Thickness(1.5); }
            else { bd.ClearValue(Border.BackgroundProperty); bd.ClearValue(Border.BorderBrushProperty); bd.ClearValue(Border.BorderThicknessProperty); }
        }
        Paint(_dropItem, false);
        _dropItem = it;
        Paint(it, true);
    }

    ListBoxItem? LibItemAt(DragEventArgs e) =>
        e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(LibList, d) is ListBoxItem it && it.DataContext is LibraryItem ? it : null;

    void LibList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(Views.MacroDrag.Format))
        {
            var it = LibItemAt(e);
            SetDropItem(it);
            if (it != null) e.Effects = DragDropEffects.Link;
            else if (e.Data.GetData(Views.MacroDrag.Parents) is string[] ps && ps.Any(p => p.Length > 0)) e.Effects = DragDropEffects.Link;   // empty space: take them out of their folders
        }
        e.Handled = true;
    }

    void LibList_DragLeave(object sender, DragEventArgs e) => SetDropItem(null);

    // dropping on the "Macros" title (or on empty space in the list) moves macros out of their folders, to the top of the list
    static bool FromFolders(IDataObject d) => d.GetData(Views.MacroDrag.Parents) is string[] ps && ps.Any(p => p.Length > 0);

    void LibHeader_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(Views.MacroDrag.Format) && FromFolders(e.Data);
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        LibHeader.Background = ok ? Views.Theme.AccentSoft : System.Windows.Media.Brushes.Transparent;
        e.Handled = true;
    }

    void LibHeader_DragLeave(object sender, DragEventArgs e) => LibHeader.Background = System.Windows.Media.Brushes.Transparent;

    void LibHeader_Drop(object sender, DragEventArgs e)
    {
        LibHeader.Background = System.Windows.Media.Brushes.Transparent;
        e.Handled = true;
        var ids = e.Data.GetData(Views.MacroDrag.Many) as string[] ?? [];
        var parents = e.Data.GetData(Views.MacroDrag.Parents) as string[] ?? [];
        int moved = 0;
        try { for (int k = 0; k < ids.Length && k < parents.Length; k++) if (parents[k].Length > 0 && _lib.RemoveSub(parents[k], ids[k])) moved++; }
        catch (Exception ex) { SetStatus("Could not move it: " + ex.Message); return; }
        SetStatus(moved > 0 ? $"Moved {moved} macro(s) out of their folders." : "Those macros are already at the top of the list.");
        RefreshLibrary(_s.Path);
    }

    void LibList_Drop(object sender, DragEventArgs e)
    {
        var target = LibItemAt(e)?.DataContext as LibraryItem;
        SetDropItem(null);
        e.Handled = true;
        var ids = e.Data.GetData(Views.MacroDrag.Many) as string[] ?? (e.Data.GetData(Views.MacroDrag.Format) is string one ? [one] : []);
        var parents = e.Data.GetData(Views.MacroDrag.Parents) as string[] ?? [];
        if (ids.Length == 0) return;
        try
        {
            int moved = 0; string? firstError = null;
            for (int k = 0; k < ids.Length; k++)
            {
                if (target == null) { if (k < parents.Length && parents[k].Length > 0 && _lib.RemoveSub(parents[k], ids[k])) moved++; }
                else if (_lib.AddSub(target.Path, ids[k]) is { } err) firstError ??= err;
                else moved++;
            }
            if (target != null && moved > 0) _libOpen.Add(target.Path);
            SetStatus(firstError != null && moved == 0 ? firstError
                : target == null ? $"Moved {moved} macro(s) out of their folders."
                : $"Moved {moved} macro(s) into '{target.Name}'." + (firstError != null ? " Some could not move: " + firstError : ""));
            RefreshLibrary(_s.Path);
        }
        catch (Exception ex) { SetStatus("Could not move it: " + ex.Message); }
    }

    void LibList_PreviewUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragCand is { } li) { _dragCand = null; LibList.SelectedItem = li; }
    }

    // the When chip: drag it into the event list, or just click it to insert at the playhead
    bool _chipDown;
    Point _chipStart;
    void WhenChip_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) { _chipDown = true; _chipStart = e.GetPosition(null); ((UIElement)sender).CaptureMouse(); }
    void WhenChip_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_chipDown || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _chipStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _chipDown = false; ((UIElement)sender).ReleaseMouseCapture();
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(Views.MacroDrag.WhenChip, "when"), DragDropEffects.Copy);
    }
    void WhenChip_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool click = _chipDown; _chipDown = false; ((UIElement)sender).ReleaseMouseCapture();
        if (click) InsertWhen(_s.PlayheadMs);
    }

    void LoadMacro(Macro m, string? path)
    {
        _s.Load(m, path);
        _inheritedOwners = m.Id != null && path != null ? _lib.AncestorIds(path) : new HashSet<string>();
        _refVersion = -1; _refSig = ""; _targetsPanel.ReloadForRecording();            // each recording shows its own targets, the shared ones, those of its folders and those its steps use
        _path.RequestFit(); _timeline.RequestFit();
        UpdateSummary();
    }

    bool ConfirmDiscard()
    {
        if (!_s.Dirty || _s.Events.Length == 0) return true;
        var r = MessageBox.Show(this, $"Save changes to '{_s.Name}'?", "Macro Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) SaveCurrent();
        return true;
    }

    void SaveCurrent()
    {
        try
        {
            var path = _s.Path ?? _lib.UniquePath(_s.Name);
            _s.Name = MacroLibrary.NameOf(path);
            MacroSerializer.Save(_s.ToMacro(), path);
            _s.Path = path; _s.Dirty = false;
            RefreshLibrary(path);
            UpdateSummary();
            SetStatus("Saved " + path);
        }
        catch (Exception ex) { MessageBox.Show(this, "Save failed: " + ex.Message, "Macro Studio"); }
    }

    void Lib_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLibrary || LibList.SelectedItems.Count != 1 || LibList.SelectedItem is not LibraryItem item || item.Path == _s.Path) return;   // several selected = for Delete / Duplicate, nothing to open
        if (!ConfirmDiscard()) { RefreshLibrary(_s.Path); return; }
        try { LoadMacro(MacroSerializer.Load(item.Path), item.Path); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open '{item.Name}': {ex.Message}", "Macro Studio"); RefreshLibrary(_s.Path); }
    }

    void NewMacro_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        LoadMacro(new Macro { Name = "Untitled", Desktop = Screens.Current(), Env = Screens.CurrentEnv() }, null);
        RefreshLibrary(null);
    }

    void Save_Click(object s, RoutedEventArgs e) => SaveCurrent();

    /// <summary>A copy of a macro that is its own recording (new id, same Discord link, its own copies of the targets it owns). With <paramref name="withSubs"/> a folder is copied with everything inside it, otherwise only the macro itself.</summary>
    string CopyMacro(string path, bool withSubs = true, int depth = 0, Dictionary<string, string>? inherited = null)
    {
        var copy = _lib.Duplicate(path);
        try
        {
            var m = MacroSerializer.Load(copy);
            var oldId = m.Id; var nid = Guid.NewGuid().ToString("N");
            var map = new Dictionary<string, string>(inherited ?? []);                       // targets of the copied folders -> their copies, so the copy of a sub macro uses its new folder's targets
            if (oldId != null)
            {
                _discord.CopyLink(oldId, nid); _discord.Save();
                foreach (var t in _targets.List().Where(t => t.Owner == oldId).ToList()) map[t.Id] = _targets.Clone(t.Id, nid).Id;
            }
            if (map.Count > 0) m.Events = m.Events.Select(ev => RemapTargets(ev, map)).ToList();
            m.Id = nid;
            if (withSubs && depth < 4 && m.Subs.Count > 0)                                   // the copy gets copies of its sub macros, not shared ones
                m.Subs = m.Subs.Select(sid => _lib.PathOfId(sid)).Where(p => p != null).Select(p => _lib.EnsureId(CopyMacro(p!, true, depth + 1, map))).ToList();
            else m.Subs = new();
            MacroSerializer.Save(m, copy);
        }
        catch { }
        return copy;
    }

    void LibDuplicate_Click(object s, RoutedEventArgs e)
    {
        var picked = LibList.SelectedItems.OfType<LibraryItem>().ToList();
        if (picked.Count == 0) return;
        string? last = null;
        foreach (var item in picked) last = CopyMacro(item.Path);
        RefreshLibrary(last);
    }

    // Ctrl+C on macros in the list, then Ctrl+V on a macro: the copies are filed inside that macro
    List<string> _libClip = [];

    void LibCopy()
    {
        if (LibList.SelectedItems.Count == 0) return;
        _libClip = LibList.SelectedItems.OfType<LibraryItem>().Select(x => x.Path).ToList();
        SetStatus($"Copied {_libClip.Count} macro(s). Click the macro that should hold them, then Ctrl+V (just the macro) or Ctrl+Shift+V (with its sub macros).");
    }

    /// <summary>The one macro that is selected and is not itself one of the copied macros: pasting goes inside it. Otherwise the copies are put next to the originals.</summary>
    LibraryItem? PasteTarget() =>
        LibList.SelectedItems.Count == 1 && LibList.SelectedItem is LibraryItem t && !_libClip.Contains(t.Path, StringComparer.OrdinalIgnoreCase) ? t : null;

    bool SelectionIsCopied() => LibList.SelectedItems.OfType<LibraryItem>().Any(x => _libClip.Contains(x.Path, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Pastes the copied macros: just each macro (one file), or each one with copies of all the macros inside it.
    /// With another macro selected they go inside it; with a copied macro still selected, next to its original; with nothing selected, at the top of the list.
    /// </summary>
    void LibPaste(bool withSubs)
    {
        if (_libClip.Count == 0) { SetStatus("Copy macros first (Ctrl+C or right-click > Copy), then paste with Ctrl+V (macro only) or Ctrl+Shift+V (with its sub macros)."); return; }
        var target = PasteTarget();
        int made = 0; string? error = null;
        foreach (var src in _libClip.Where(File.Exists).ToList())
        {
            var copy = CopyMacro(src, withSubs);
            string? folder = target?.Path ?? (SelectionIsCopied() ? _lib.FolderOf(src) : null);   // next to the original (same folder), or at the top when nothing is selected
            if (folder != null && _lib.AddSub(folder, _lib.EnsureId(copy)) is { } err) { error ??= err; _lib.Delete(copy); }
            else made++;
        }
        if (made > 0 && target != null) _libOpen.Add(target.Path);
        string what = withSubs ? "with their sub macros" : "(macro only)";
        SetStatus(error != null && made == 0 ? error : target != null ? $"Pasted {made} copy(ies) {what} inside '{target.Name}'." : SelectionIsCopied() ? $"Pasted {made} copy(ies) {what} next to the original." : $"Pasted {made} copy(ies) {what} at the top of the list.");
        RefreshLibrary(_s.Path);
    }

    void LibList_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Delete && LibList.SelectedItems.Count > 0) { LibDelete_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { LibList.UnselectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { LibCopy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { LibPaste(withSubs: shift); e.Handled = true; }
    }

    // Ctrl+Shift+V is "paste with sub macros" in the list. If an app hotkey (e.g. Pause) uses the same keys, it is released while the list has the focus.
    void LibList_FocusWithin(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hotKeys == null) return;
        if (e.NewValue is true)
        {
            for (int i = 0; i < HotkeySettings.Actions.Length; i++)
                if (_keys.Get(HotkeySettings.Actions[i].Action) is { Mods: HotkeySettings.Ctrl | HotkeySettings.Shift, Vk: 0x56 }) _hotKeys.Unregister(i + 1);
        }
        else ApplyHotkeys();
    }

    void LibCopy_Click(object s, RoutedEventArgs e) => LibCopy();
    void LibPaste_Click(object s, RoutedEventArgs e) => LibPaste(withSubs: false);
    void LibPasteSubs_Click(object s, RoutedEventArgs e) => LibPaste(withSubs: true);

    // right-click selects the macro under the pointer first (unless it is already part of the selection), so the menu acts on what was clicked
    void LibList_PreviewRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LibList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: LibraryItem li } && !LibList.SelectedItems.Contains(li))
            LibList.SelectedItem = li;
    }

    void LibMenu_Opened(object sender, RoutedEventArgs e)
    {
        LibMenuCopy.IsEnabled = LibList.SelectedItems.Count > 0;
        LibMenuPaste.IsEnabled = LibMenuPasteSubs.IsEnabled = _libClip.Count > 0;
        string where = PasteTarget() is { } t ? $"inside '{t.Name}'" : SelectionIsCopied() ? "next to the original" : "at the top of the list";
        LibMenuPaste.Header = $"Paste the macro only, {where}";
        LibMenuPasteSubs.Header = $"Paste with its sub macros, {where}";
    }

    void LibRename_Click(object s, RoutedEventArgs e)
    {
        if (LibList.SelectedItems.Count != 1 || LibList.SelectedItem is not LibraryItem item) { SetStatus("Select one macro to rename it."); return; }
        var name = Prompt.Ask(this, "Rename macro", item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var np = _lib.Rename(item.Path, name);
            if (_s.Path == item.Path) { _s.Path = np; _s.Name = MacroLibrary.NameOf(np); UpdateSummary(); }
            RefreshLibrary(np);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Macro Studio"); }
    }

    void LibDelete_Click(object s, RoutedEventArgs e)
    {
        var picked = LibList.SelectedItems.OfType<LibraryItem>().ToList();
        if (picked.Count == 0) return;
        string what = picked.Count == 1 ? $"'{picked[0].Name}'" : $"{picked.Count} macros ({string.Join(", ", picked.Take(3).Select(x => x.Name))}{(picked.Count > 3 ? ", …" : "")})";
        if (MessageBox.Show(this, $"Delete {what} permanently? Visual targets that belong only to {(picked.Count == 1 ? "it" : "them")} are deleted too (shared targets stay).", "Macro Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        bool openDeleted = false;
        foreach (var item in picked)
        {
            try { if (MacroSerializer.Load(item.Path).Id is { } rid) foreach (var t in _targets.List().Where(t => t.Owner == rid)) _targets.Delete(t.Id); } catch { }
            _lib.Delete(item.Path);
            if (_s.Path == item.Path) openDeleted = true;
        }
        if (openDeleted) LoadMacro(new Macro { Name = "Untitled", Desktop = Screens.Current(), Env = Screens.CurrentEnv() }, null);   // you deleted it on purpose: no "save changes?" question afterwards
        RefreshLibrary(null);
        RefreshTargetNames(); _targetsPanel.ReloadForRecording();
        UpdateSummary();
    }

    void Import_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Macro files (*.macro.json;*.json)|*.macro.json;*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true || !ConfirmDiscard()) return;
        try
        {
            var m = MacroSerializer.Load(dlg.FileName);
            var path = _lib.UniquePath(string.IsNullOrWhiteSpace(m.Name) ? MacroLibrary.NameOf(dlg.FileName) : m.Name);
            m.Name = MacroLibrary.NameOf(path);
            m.Id = null;   // an imported recording never inherits a link
            MacroSerializer.Save(m, path);
            LoadMacro(m, path);
            RefreshLibrary(path);
        }
        catch (Exception ex) { MessageBox.Show(this, "Import failed: " + ex.Message, "Macro Studio"); }
    }

    void Export_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Macro file (*.macro.json)|*.macro.json", FileName = _s.Name + MacroLibrary.Ext };
        if (dlg.ShowDialog(this) != true) return;
        try { MacroSerializer.Save(DiscordWebhook.ForExport(_s.ToMacro()), dlg.FileName); SetStatus("Exported to " + dlg.FileName + " (no Discord links included)."); }
        catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "Macro Studio"); }
    }

    void RestartAdmin_Click(object s, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { SetStatus("Restart as administrator was cancelled."); }
    }

    // ---- logs ----

    void ShowRunLog_Click(object s, RoutedEventArgs e)
    {
        if (_bottomCollapsed) SetBottomCollapsed(false);
        BottomTabs.SelectedIndex = 1;                       // Events, Run log, Detections
    }

    /// <summary>The newest file of a kind in the library folder, or null.</summary>
    string? Newest(string pattern) => new DirectoryInfo(_lib.Dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;

    void OpenLogFile(string pattern, string what)
    {
        if (Newest(pattern) is not { } f) { SetStatus($"There is no {what} log yet."); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
    }

    void OpenPlayLog_Click(object s, RoutedEventArgs e) => OpenLogFile("play-*.log", "playback");
    void OpenRecordLog_Click(object s, RoutedEventArgs e) => OpenLogFile("record-*.log", "recording");

    /// <summary>Writes what the playback did to a text file next to the macros, so it can be read or sent after the run.</summary>
    string? SavePlayLog(string macroName, string outcome)
    {
        try
        {
            var path = Path.Combine(_lib.Dir, $"play-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Playback of '{macroName}' on {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {outcome}");
            sb.AppendLine($"ok {_log.Count(LogResult.Ok)}, warnings {_log.Count(LogResult.Warn)}, retries {_log.Count(LogResult.Retry)}, skipped {_log.Count(LogResult.Skipped)}, failed {_log.Count(LogResult.Failed)}");
            sb.AppendLine();
            foreach (var e in _log.Snapshot())
                sb.AppendLine($"{e.Utc.ToLocalTime():HH:mm:ss.fff}  #{e.Index + 1,-5} {e.Result,-8} {e.What}: {e.Message}" + (e.Confidence is { } c ? $"  (score {c:0.00})" : "") + (e.X is { } x ? $"  at {x},{e.Y}" : ""));
            File.WriteAllText(path, sb.ToString());
            foreach (var old in new DirectoryInfo(_lib.Dir).GetFiles("play-*.log").OrderByDescending(f => f.LastWriteTime).Skip(20)) try { old.Delete(); } catch { }
            return path;
        }
        catch { return null; }
    }

    void OpenFolder_Click(object s, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lib.Dir) { UseShellExecute = true });

    // ---- edit commands ----

    void Undo_Click(object s, RoutedEventArgs e) => _s.Undo();
    void Redo_Click(object s, RoutedEventArgs e) => _s.Redo();
    void Delete_Click(object s, RoutedEventArgs e) => _s.Delete(_s.Selected.ToList());
    void Duplicate_Click(object s, RoutedEventArgs e) => _s.Duplicate(_s.Selected.ToList());
    void Trim_Click(object s, RoutedEventArgs e) => _s.TrimLeadingIdle();
    void SelectAll_Click(object s, RoutedEventArgs e) => _s.Select(_s.Events.Select(x => x.Id).ToList());
    void HideMoves_Click(object s, RoutedEventArgs e) => _grid.HideMoves = HideMovesBox.IsChecked == true;

    void Copy_Click(object s, RoutedEventArgs e)
    {
        if (_s.Selected.Count > 0) Clipboard.SetText(_s.CopyText(_s.Selected.ToList()));
    }

    void Paste_Click(object s, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        if (_s.PasteText(CarryTargets(Clipboard.GetText()), _s.PlayheadMs).Count == 0) SetStatus("Clipboard does not contain Macro Studio events.");
    }

    /// <summary>
    /// Pasted steps that click targets belonging to ANOTHER recording would point at targets this recording does not list. Copy those targets into
    /// this recording too and re-point the pasted steps at the copies. Targets this recording already can use are left alone.
    /// </summary>
    string CarryTargets(string text)
    {
        List<MacroEvent> block;
        try { block = MacroSerializer.DeserializeEvents(text); } catch { return text; }
        var all = _targets.List().ToDictionary(t => t.Id);
        var foreign = block.SelectMany(TargetRemap.Used).Distinct()
            .Where(id => all.TryGetValue(id, out var t) && t.Owner != null && t.Owner != Target.Everyone && t.Owner != _s.Id && !_inheritedOwners.Contains(t.Owner)).ToList();   // a folder's targets are usable here as they are
        if (foreign.Count == 0) return text;
        var me = EnsureRecordingId();
        var map = foreign.ToDictionary(id => id, id => _targets.Clone(id, me).Id);
        _targetsPanel.ReloadForRecording(); RefreshTargetNames();
        SetStatus($"Pasted. {map.Count} visual target(s) used by those steps were copied into this recording too.");
        return MacroSerializer.SerializeEvents(block.Select(e => TargetRemap.Apply(e, map)));
    }

    /// <summary>The targets the open recording can use: its own and the shared ones.</summary>
    /// <summary>The ids of the macros that hold the open macro in a folder, at any depth: their visual targets are usable here as they are.</summary>
    HashSet<string> _inheritedOwners = new();

    void UpdateInheritedTargets()
    {
        var now = _s.Path != null && File.Exists(_s.Path) ? _lib.AncestorIds(_s.Path) : new HashSet<string>();
        if (now.SetEquals(_inheritedOwners)) return;
        _inheritedOwners = now;
        _targetsPanel?.ReloadForRecording();
        _inspector?.Reload();
    }

    IEnumerable<Target> MyTargets() { var used = ReferencedTargets(); return _targets.List().Where(t => t.VisibleTo(_s.Id, used, _inheritedOwners)); }

    int _refVersion = -1; ISet<string> _refCache = new HashSet<string>(); string _refSig = "";

    /// <summary>The ids of every target the open recording's steps point at (clicks, waits, verify and recovery targets). Recomputed only when the macro changed.</summary>
    ISet<string> ReferencedTargets()
    {
        if (_refVersion == _s.Version) return _refCache;
        var set = new HashSet<string>();
        foreach (var e in _s.Events) foreach (var id in TargetRemap.Used(e)) set.Add(id);
        _refVersion = _s.Version; _refCache = set;
        return set;
    }

    /// <summary>When a step starts or stops using a target, the target list follows.</summary>
    void SyncTargetsPanel()
    {
        var sig = string.Join(",", ReferencedTargets().OrderBy(x => x));
        if (sig == _refSig) return;
        _refSig = sig;
        _targetsPanel.ReloadForRecording();
    }

    /// <summary>A target captured now belongs to this recording, so the recording needs an id (it is saved with the recording).</summary>
    string EnsureRecordingId()
    {
        if (_s.Id == null) { _s.Id = Guid.NewGuid().ToString("N"); _s.Dirty = true; UpdateSummary(); }
        return _s.Id;
    }

    static MacroEvent RemapTargets(MacroEvent e, Dictionary<string, string> map) => TargetRemap.Apply(e, map);

    void RefreshTargetNames()
    {
        _targetNames = _targets.List().ToDictionary(t => t.Id, t => t.Name);
        _thumbs.Clear(); // a target may have been replaced or deleted
        _s.Revalidate(); // names and "missing target" problems update everywhere
    }


    void Insert_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = _s.PositionAt(_s.PlayheadMs) is { } p ? ((int)Math.Round(p.X), (int)Math.Round(p.Y)) : (0, 0);
        MacroEvent[] block = (((MenuItem)sender).Tag as string) switch
        {
            "key" => [new(0, EventKind.KeyDown, 0x41, 0x1E), new(50, EventKind.KeyUp, 0x41, 0x1E)],
            "lclick" => [new(0, EventKind.ButtonDown, X: x, Y: y), new(60, EventKind.ButtonUp, X: x, Y: y)],
            "rclick" => [new(0, EventKind.ButtonDown, Button: MouseBtn.Right, X: x, Y: y), new(60, EventKind.ButtonUp, Button: MouseBtn.Right, X: x, Y: y)],
            "move" => [new(0, EventKind.MouseMove, X: x, Y: y)],
            "down" => [new(0, EventKind.Wheel, X: x, Y: y, Delta: -120)],
            "up" => [new(0, EventKind.Wheel, X: x, Y: y, Delta: 120)],
            "left" => [new(0, EventKind.HWheel, X: x, Y: y, Delta: -120)],
            _ => [new(0, EventKind.HWheel, X: x, Y: y, Delta: 120)],
        };
        _s.InsertBlock(block, _s.PlayheadMs, ripple: true);
    }

    /// <summary>The scroll bar of the list (grabbing it must not clear the selection).</summary>
    static bool IsScrollBarPart(DependencyObject d)
    {
        for (; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d)) if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    static bool InsideOf(DependencyObject d, DependencyObject container)
    {
        for (; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d == container) return true;
        return false;
    }


    void OnShortcut(object sender, KeyEventArgs e)
    {
        // typing in a text box, or working in the macro / target lists: Ctrl+C, Ctrl+V, Delete etc. belong to that control, not to the timeline's events
        bool typing = Keyboard.FocusedElement is TextBox || (Keyboard.FocusedElement is DependencyObject f && (InsideOf(f, LibList) || InsideOf(f, TargetsHost)));
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.S) { SaveCurrent(); e.Handled = true; }
        else if (typing) return; // let text boxes keep their own undo/copy/paste/delete
        else if (ctrl && e.Key == Key.Z) { _s.Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { _s.Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { Copy_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { Paste_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { Duplicate_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.B) { Breakpoint_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAll_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(this, e); e.Handled = true; }
    }
}
