using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Views;

/// <summary>What the inspector needs to show and manage the Discord link of the open recording (kept outside this view so the secret never lives here).</summary>
/// <param name="State">Masked link text and where it comes from: "own", "default" or "none".</param>
/// <param name="Set">Saves a new link for this recording; returns an error message, or null when saved.</param>
/// <param name="Clear">Removes this recording's own link (it then uses the default).</param>
/// <param name="Test">Sends a test message; the callback gets null on success or the reason it failed.</param>
public sealed record DiscordHooks(Func<(string Shown, string Source)> State, Func<string, string?> Set, Action Clear, Action<Action<string?>> Test);

/// <summary>Shows every field of the primary selected event and edits it through the session (so undo/redo and all views follow).</summary>
public sealed class InspectorPanel : ScrollViewer
{
    static readonly Brush CardBg = Theme.Brush("#F8FAFC"), CardLine = Theme.Brush("#E8EBF0"), Ok = Theme.Brush("#15803D"), BadgeBg = Theme.Brush("#EEF2FF"), BadgeFg = Theme.Brush("#3730A3");

    readonly EditSession _s;
    readonly DiscordHooks? _discord;
    readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 17, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _badgeText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = BadgeFg };
    readonly Border _badge = new() { Background = BadgeBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock _desc = new() { Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _info = new() { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 12) };
    readonly StackPanel _root = new() { Margin = new Thickness(14, 14, 14, 10) };
    readonly StackPanel _single = new(), _stepBody = new(), _pos = new(), _key = new(), _btn = new(), _scroll = new(), _hold = new(), _multi = new(), _tgt = new(), _smart = new();
    readonly Border _tgtCard, _keyCard, _btnCard, _scrollCard, _holdCard;
    readonly Func<IReadOnlyList<(string Id, string Name)>> _targets;
    readonly ComboBox _targetBox = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    readonly TextBox _tx = new(), _ty = new(), _tmo = new(), _minConf = new(), _retries = new(), _retryDelay = new(), _minRepeat = new(), _fbx = new(), _fby = new(), _verifyTmo = new(), _recKey = new(), _glideMs = new();
    readonly AnchorPicker _anchor = new();
    readonly Func<string, ImageSource?> _targetImage;
    readonly CheckBox _stable = new() { Content = "Require two matching detections before clicking", Margin = new Thickness(0, 8, 0, 4) };
    readonly ComboBox _move = new(), _onFail = new(), _verify = new(), _verifyTarget = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" }, _recTarget = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    readonly CheckBox _skip = new() { Content = "Skip if not found (otherwise stop playback)", Margin = new Thickness(0, 8, 0, 4) };
    readonly ComboBox _kind = new(), _button = new(), _dir = new();
    readonly TextBox _scalePct = new(), _rotDeg = new();
    readonly TextBox _time = new(), _delay = new(), _x = new(), _y = new(), _keyBox = new(), _notch = new(), _raw = new(), _holdBox = new(), _shift = new(), _offX = new(), _offY = new();
    readonly CheckBox _ripple = new() { Content = "Ripple later events", IsChecked = true, Margin = new Thickness(0, 8, 0, 2) };

    // Discord: the message of a Discord step, and the link of this recording
    readonly TextBox _dText = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    readonly CheckBox _dShot = new() { Content = "Attach a screenshot", Margin = new Thickness(0, 8, 0, 2) };
    readonly TextBox _afterText = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Sent to this recording's Discord link right after the click succeeds. Leave empty to send nothing." };
    readonly CheckBox _afterShot = new() { Content = "Attach a screenshot", Margin = new Thickness(0, 8, 0, 2) };
    readonly Border _dMsgCard, _dLinkCard;
    readonly StackPanel _dFirst = new();   // the Discord cards come first: for a Discord step they are the point of the panel
    readonly TextBlock _dShown = new() { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _dSource = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBox _dInput = new() { ToolTip = "Paste a Discord webhook link here, then press Save link" };
    readonly TextBlock _dNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    readonly Button _dSave = Btn("Save link", null), _dTest = Btn("Send test", null), _dClear = Btn("Use default", null);
    bool _updating;

    // When block: "when [target] appears / disappears ... then run a connected macro / restart / continue"
    readonly Border _whenCard;
    readonly ComboBox _whenTarget = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" }, _whenCond = new(), _whenCheck = new(), _whenDo = new(), _whenAfter = new();
    readonly TextBox _whenWait = new(), _whenMax = new();
    readonly CheckBox _whenKeep = new() { Content = "Keep waiting until it happens (no time limit)", Margin = new Thickness(0, 8, 0, 2), ToolTip = "The macro stays on this step until the condition is met, however long that takes (for example a screen that is still loading)" };
    readonly CheckBox _whenClick = new() { Content = "Click first (Where to click)", Margin = new Thickness(0, 10, 0, 2), ToolTip = "Click the target before doing the action below" };
    readonly TextBlock _whenMacro = new() { FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _whenDrop = new() { BorderBrush = CardLine, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 6, 6), AllowDrop = true, Background = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _whenRunPanel = new(), _whenWaitPanel = new();
    readonly Button _whenRecord = Btn("+ Record", null), _whenClear = Btn("Remove", null);
    readonly TextBlock _whenPickLabel = new() { Text = "...or choose any macro (this macro's folder first)", Foreground = Theme.Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 3) };
    readonly ComboBox _whenPick = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    readonly TextBlock _whenNote = new() { Foreground = Theme.Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>The user dropped a library macro on the When block (or its inspector slot): connect it.</summary>
    public void AttachMacro(string macroId)
    {
        if (_s.PrimaryEvent is not { Kind: EventKind.When }) return;
        if (FileMacro?.Invoke(macroId) is { } problem) { _whenNote.Text = problem; return; }   // it is also filed in this macro's folder; refused when that would loop
        _whenNote.Text = "";
        EditFlow(f => f with { Macro = macroId, Do = FlowDo.RunMacro });
    }
    /// <summary>Files a dropped macro in the open macro's folder (so it is listed under it); returns why not, or null.</summary>
    public Func<string, string?>? FileMacro { get; set; }
    /// <summary>The macros filed in the open macro's folder, offered in the drop-down next to the drop slot.</summary>
    public Func<IReadOnlyList<(string Id, string Name)>>? FolderMacros { get; set; }
    /// <summary>The user pressed "+ Record": record a new macro and connect it to this block.</summary>
    public event Action? RecordHandlerRequested;

    public InspectorPanel(EditSession s, Func<IReadOnlyList<(string Id, string Name)>>? targets = null, Func<string, ImageSource?>? targetImage = null, DiscordHooks? discord = null)
    {
        _s = s;
        _discord = discord;
        _targets = targets ?? (() => []);
        _targetImage = targetImage ?? (_ => null);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = _root;
        _badge.Child = _badgeText;
        var head = new DockPanel();
        DockPanel.SetDock(_badge, Dock.Right);
        head.Children.Add(_badge); head.Children.Add(_title);
        _root.Children.Add(head);
        _root.Children.Add(_desc);
        _root.Children.Add(_info);
        _root.Children.Add(_dFirst);
        _root.Children.Add(_single);

        _kind.ItemsSource = Enum.GetValues<EventKind>();
        _button.ItemsSource = Enum.GetValues<MouseBtn>();
        _dir.ItemsSource = new[] { "Up", "Down", "Left", "Right" };
        _kind.SelectionChanged += (_, _) => { if (!_updating && _kind.SelectedItem is EventKind k) Edit(e => e with { Kind = k }); };
        _button.SelectionChanged += (_, _) => { if (!_updating && _button.SelectedItem is MouseBtn b) EditPair(e => e with { Button = b }); };
        _dir.SelectionChanged += (_, _) => { if (!_updating && _dir.SelectedItem is string d) SetDirection(d); };

        // ---- the step itself ----
        Row(_stepBody, "Type", _kind);
        Field(_stepBody, "Time (ms since start)", _time, v => Edit(e => e with { T = v }));
        Field(_stepBody, "Delay since previous (ms)", _delay, v => { if (_s.PrimaryEvent is { } e) _s.SetDelay(e.Id, v, _ripple.IsChecked == true); });
        _stepBody.Children.Add(_ripple);
        Field(_pos, "X (screen px)", _x, v => Edit(e => e with { X = (int)Math.Round(v) }));
        Field(_pos, "Y (screen px)", _y, v => Edit(e => e with { Y = (int)Math.Round(v) }));
        _stepBody.Children.Add(_pos);
        _single.Children.Add(Card("Step", _stepBody));

        // ---- Discord message ----
        _dText.LostFocus += (_, _) => { if (!_updating && _s.PrimaryEvent is { Kind: EventKind.Discord } e && (e.Target ?? "") != _dText.Text) Edit(x => x with { Target = _dText.Text }); };
        _dShot.Click += (_, _) => Edit(e => e with { Extended = _dShot.IsChecked == true });
        var dm = new StackPanel();
        Row(dm, "Message sent to Discord", _dText);
        dm.Children.Add(_dShot);
        _dFirst.Children.Add(_dMsgCard = Card("Discord message", dm));

        // ---- visual target steps (click / wait visible / wait gone) ----
        _targetBox.SelectionChanged += (_, _) => { if (!_updating && _targetBox.SelectedValue is string id) Edit(e => e with { Target = id }); };
        Row(_tgt, "Visual target", _targetBox);
        Field(_tgt, "Wait up to (ms)", _tmo, v => Edit(e => e with { Delta = (int)Math.Max(0, Math.Round(v)) }));
        _skip.Click += (_, _) => Edit(e => e with { Extended = _skip.IsChecked == true });
        _tgt.Children.Add(_skip);
        _single.Children.Add(_tgtCard = Card("Target", _tgt));

        // ---- When block ----
        var wh = new StackPanel();
        _whenTarget.SelectionChanged += (_, _) => { if (!_updating && _whenTarget.SelectedValue is string id) Edit(e => e with { Target = id }); };
        Row(wh, "Visual target", _whenTarget);
        _whenCond.ItemsSource = new[] { "appears", "disappears", "does not appear" };
        _whenCond.SelectionChanged += (_, _) => { if (!_updating && _whenCond.SelectedIndex >= 0) EditFlow(f => f with { Condition = (WhenCondition)_whenCond.SelectedIndex }); };
        Row(wh, "When the target", _whenCond);
        _whenCheck.ItemsSource = new[] { "Check here, at this step", "Watch all the time from here on" };
        _whenCheck.SelectionChanged += (_, _) => { if (!_updating && _whenCheck.SelectedIndex >= 0) EditFlow(f => f with { Always = _whenCheck.SelectedIndex == 1 }); };
        Row(wh, "Check", _whenCheck);
        Field(_whenWaitPanel, "Wait up to (seconds)", _whenWait, v => EditFlow(f => f with { WaitMs = (int)Math.Clamp(v * 1000, 0, 3_600_000) }));
        wh.Children.Add(_whenWaitPanel);
        _whenKeep.Click += (_, _) => EditFlow(f => f with { KeepWaiting = _whenKeep.IsChecked == true });
        wh.Children.Add(_whenKeep);
        _whenClick.Click += (_, _) => EditFlow(f => f with { ClickFirst = _whenClick.IsChecked == true });
        wh.Children.Add(_whenClick);
        _whenDo.ItemsSource = new[] { "Run the connected macro", "Restart the main macro from the beginning", "Continue where it was interrupted" };
        _whenDo.SelectionChanged += (_, _) => { if (!_updating && _whenDo.SelectedIndex >= 0) EditFlow(f => f with { Do = (FlowDo)_whenDo.SelectedIndex }); };
        Row(wh, "Then", _whenDo);
        _whenAfter.ItemsSource = new[] { "then continue the main macro", "then restart the main macro" };
        _whenAfter.SelectionChanged += (_, _) => { if (!_updating && _whenAfter.SelectedIndex >= 0) EditFlow(f => f with { After = (FlowAfter)_whenAfter.SelectedIndex }); };
        _whenRunPanel.Children.Add(new TextBlock { Text = "Connected macro (drag one here from the library)", Foreground = Theme.Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        var slot = new DockPanel();
        var slotButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _whenRecord.Click += (_, _) => RecordHandlerRequested?.Invoke();
        _whenClear.Click += (_, _) => EditFlow(f => f with { Macro = null });
        slotButtons.Children.Add(_whenRecord); slotButtons.Children.Add(_whenClear);
        DockPanel.SetDock(slotButtons, Dock.Right);
        slot.Children.Add(slotButtons); slot.Children.Add(_whenMacro);
        _whenDrop.Child = slot;
        _whenDrop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(MacroDrag.Format) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        _whenDrop.Drop += (_, e) => { if (e.Data.GetData(MacroDrag.Format) is string mid) AttachMacro(mid); e.Handled = true; };
        _whenRunPanel.Children.Add(_whenDrop);
        _whenRunPanel.Children.Add(_whenNote);
        _whenPick.SelectionChanged += (_, _) => { if (!_updating && _whenPick.SelectedValue is string pid && pid.Length > 0) AttachMacro(pid); };
        _whenRunPanel.Children.Add(_whenPickLabel);
        _whenPick.Margin = new Thickness(0);
        _whenRunPanel.Children.Add(_whenPick);
        Row(_whenRunPanel, "After the connected macro", _whenAfter);
        wh.Children.Add(_whenRunPanel);
        Field(wh, "At most this many times per run (0 = no limit)", _whenMax, v => EditFlow(f => f with { MaxRuns = (int)Math.Clamp(v, 0, 10000) }));
        _single.Children.Add(_whenCard = Card("When", wh));

        var cp = new StackPanel();
        _anchor.Margin = new Thickness(0, 6, 0, 4);
        _anchor.Changed += (rx, ry) => EditStep(o => o with { RelX = rx, RelY = ry }, "anchor");
        _anchor.Completed += () => _s.EndCoalesce();
        cp.Children.Add(_anchor);
        Field(cp, "Extra offset X (px)", _tx, v => Edit(e => e with { X = (int)Math.Round(v) }));
        Field(cp, "Extra offset Y (px)", _ty, v => Edit(e => e with { Y = (int)Math.Round(v) }));
        _smart.Children.Add(Card("Click position on the target", cp));

        var mv = new StackPanel();
        _move.ItemsSource = new[] { "Exact recorded path, then jump to the target", "Bend the recorded path to the target's current position", "Glide straight to the target", "Optimized: skip the recorded approach, go straight to the target" };
        _move.SelectionChanged += (_, _) => { if (!_updating && _move.SelectedIndex >= 0) EditStep(o => o with { Move = (MoveMode)_move.SelectedIndex }); };
        Row(mv, "How the mouse reaches the target (playback only; the recording is never changed)", _move);
        Field(mv, "Glide duration in ms (0 = automatic)", _glideMs, v => EditStep(o => o with { GlideMs = (int)Math.Clamp(v, 0, 10000) }));
        _smart.Children.Add(Card("Mouse movement", mv));

        var rl = new StackPanel();
        Field(rl, "Minimum confidence (0 = the target's own threshold)", _minConf, v => EditStep(o => o with { MinConfidence = Math.Clamp(v, 0, 0.99) }));
        Field(rl, "Retries after a failure", _retries, v => EditStep(o => o with { Retries = (int)Math.Clamp(v, 0, 20) }));
        Field(rl, "Delay between retries (ms)", _retryDelay, v => EditStep(o => o with { RetryDelayMs = (int)Math.Clamp(v, 0, 60000) }));
        _stable.Click += (_, _) => EditStep(o => o with { RequireStable = _stable.IsChecked == true });
        rl.Children.Add(_stable);
        Field(rl, "Minimum ms between clicks on the same spot", _minRepeat, v => EditStep(o => o with { MinRepeatMs = (int)Math.Clamp(v, 0, 60000) }));
        _smart.Children.Add(Card("Reliability", rl));

        var fl = new StackPanel();
        _onFail.ItemsSource = new[] { "Stop playback", "Skip this step", "Click the recorded position" };
        _onFail.SelectionChanged += (_, _) => { if (!_updating && _onFail.SelectedIndex >= 0) EditStep(o => o with { OnFail = (FailAction)_onFail.SelectedIndex }); };
        Row(fl, "If it still fails", _onFail);
        Field(fl, "Recorded click X (fallback)", _fbx, v => EditStep(o => o with { FallbackX = (int)Math.Round(v) }));
        Field(fl, "Recorded click Y (fallback)", _fby, v => EditStep(o => o with { FallbackY = (int)Math.Round(v) }));
        fl.Children.Add(new TextBlock { Text = "The recorded position is only used while the screen layout is the one it was recorded on.", Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        _smart.Children.Add(Card("If it fails", fl));

        var vf = new StackPanel();
        _verify.ItemsSource = new[] { "Nothing", "The target has disappeared", "Another target appears" };
        _verify.SelectionChanged += (_, _) => { if (!_updating && _verify.SelectedIndex >= 0) EditStep(o => o with { Verify = (VerifyMode)_verify.SelectedIndex }); };
        Row(vf, "After the click, verify", _verify);
        _verifyTarget.SelectionChanged += (_, _) => { if (!_updating && _verifyTarget.SelectedValue is string id) EditStep(o => o with { VerifyTarget = id.Length == 0 ? null : id }); };
        Row(vf, "...the other target", _verifyTarget);
        Field(vf, "Verify timeout (ms)", _verifyTmo, v => EditStep(o => o with { VerifyTimeoutMs = (int)Math.Clamp(v, 100, 120000) }));
        _recKey.IsReadOnly = true;
        _recKey.ToolTip = "Click, then press a key (e.g. Esc). Delete clears it.";
        _recKey.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
            int vk = key is Key.Delete or Key.Back ? 0 : KeyInterop.VirtualKeyFromKey(key);
            EditStep(o => o with { RecoveryKey = vk });
        };
        Row(vf, "If verification fails, press key", _recKey);
        _recTarget.SelectionChanged += (_, _) => { if (!_updating && _recTarget.SelectedValue is string id) EditStep(o => o with { RecoveryTarget = id.Length == 0 ? null : id }); };
        Row(vf, "...and/or click this target, then retry", _recTarget);
        _smart.Children.Add(Card("Check the result", vf));

        var after = new StackPanel();
        _afterText.LostFocus += (_, _) => { if (!_updating && _s.PrimaryEvent is { Kind: EventKind.TargetClick } e && (e.Step?.DiscordText ?? "") != _afterText.Text) EditStep(o => o with { DiscordText = _afterText.Text }); };
        _afterShot.Click += (_, _) => EditStep(o => o with { DiscordShot = _afterShot.IsChecked == true });
        Row(after, "Message to send once the click has worked (empty = none)", _afterText);
        after.Children.Add(_afterShot);
        _smart.Children.Add(Card("Discord after the click", after));
        _single.Children.Add(_smart);

        _keyBox.IsReadOnly = true;
        _keyBox.ToolTip = "Click, then press the key to assign";
        _keyBox.PreviewKeyDown += KeyCapture;
        Row(_key, "Key (click and press a key)", _keyBox);
        _single.Children.Add(_keyCard = Card("Key", _key));

        Row(_btn, "Mouse button", _button);
        _single.Children.Add(_btnCard = Card("Mouse button", _btn));

        Row(_scroll, "Direction", _dir);
        Field(_scroll, "Amount (notches, 1 = 120 units)", _notch, v => SetAmount((int)Math.Round(Math.Abs(v) * 120)));
        Field(_scroll, "Raw wheel delta (units)", _raw, v => SetAmount((int)Math.Round(Math.Abs(v))));
        _single.Children.Add(_scrollCard = Card("Scroll", _scroll));

        Field(_hold, "Hold duration (ms)", _holdBox, v => { if (Partner() is { } p) _s.Modify([p.Id], e => e with { T = Primary().T + Math.Max(0, v) }); });
        _single.Children.Add(_holdCard = Card("Hold", _hold));

        var actions = new WrapPanel { Margin = new Thickness(-3, 0, 0, 6) };
        actions.Children.Add(Btn("Duplicate", () => _s.Duplicate(_s.Selected)));
        actions.Children.Add(Btn("Delete", () => _s.Delete(_s.Selected), "Danger"));
        actions.Children.Add(Btn("↑ Earlier", () => _s.Move(_s.Primary, -1)));
        actions.Children.Add(Btn("↓ Later", () => _s.Move(_s.Primary, 1)));
        _single.Children.Add(actions);

        var multi = new StackPanel();
        Field(multi, "Shift time by (ms)", _shift, v => { _s.ShiftTime(_s.Selected.ToList(), v); Refresh(); }, "0");
        Field(multi, "Offset X by (px)", _offX, v => OffsetSelection((int)Math.Round(v), 0), "0");
        Field(multi, "Offset Y by (px)", _offY, v => OffsetSelection(0, (int)Math.Round(v)), "0");
        Field(multi, "Scale (%) around the centre of the selection", _scalePct, v => TransformSelection(v / 100.0, 0), "100");
        Field(multi, "Rotate (°) around the centre of the selection", _rotDeg, v => TransformSelection(1, v), "0");
        multi.Children.Add(new TextBlock { Text = "Or drag the box that appears around the selected points in the path view: inside to move, corners to scale, the round handle to rotate.", Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _multi.Children.Add(Card("Whole selection", multi));
        _root.Children.Add(_multi);

        // ---- Discord link of this recording: right here, for a Discord step and when nothing is selected ----
        var dl = new StackPanel();
        dl.Children.Add(_dSource);
        var shownRow = new Border { Background = Theme.White, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Child = _dShown };
        dl.Children.Add(shownRow);
        Row(dl, "Paste a new link for this recording", _dInput);
        var lb = new WrapPanel { Margin = new Thickness(-3, 6, 0, 0) };
        lb.Children.Add(_dSave); lb.Children.Add(_dTest); lb.Children.Add(_dClear);
        dl.Children.Add(lb);
        dl.Children.Add(_dNote);
        _dSave.Click += (_, _) => SaveLink();
        _dInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveLink(); e.Handled = true; } };
        _dClear.Click += (_, _) => { _discord?.Clear(); Note("This recording now uses the default link.", Ok); Refresh(); };
        _dTest.Click += (_, _) => { Note("Sending a test message…", Theme.Muted); _discord?.Test(err => Note(err == null ? "Test message sent." : "Test failed: " + err, err == null ? Ok : Theme.Warn)); };
        _dFirst.Children.Add(_dLinkCard = Card("Discord link · this recording", dl));

        s.SelectionChanged += Refresh;
        s.EventsChanged += Refresh;
        Refresh();
    }

    /// <summary>Re-read the selection and the Discord link (the link can change from outside, e.g. the Options panel).</summary>
    public void Reload() => Refresh();

    void SaveLink()
    {
        if (_discord == null) return;
        var text = _dInput.Text.Trim();
        if (text.Length == 0) { Note("Paste a Discord webhook link first.", Theme.Warn); return; }
        var err = _discord.Set(text);
        if (err != null) { Note(err, Theme.Warn); return; }
        _dInput.Clear();
        Note("Link saved for this recording.", Ok);
        Refresh();
    }

    void Note(string text, Brush brush) { _dNote.Text = text; _dNote.Foreground = brush; _dNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }

    MacroEvent Primary() => _s.PrimaryEvent ?? default;

    MacroEvent? Partner()
    {
        int i = _s.IndexOfId(_s.Primary);
        return i >= 0 && _s.Analysis.Partner[i] >= 0 ? _s.Events[_s.Analysis.Partner[i]] : null;
    }

    void Edit(Func<MacroEvent, MacroEvent> f) { if (_s.Primary != 0) _s.Modify([_s.Primary], f); }
    void EditStep(Func<StepOptions, StepOptions> f, string? coalesce = null) { if (_s.Primary != 0) _s.Modify([_s.Primary], e => e with { Step = f(e.Step ?? StepOptions.Legacy(e.Extended)) }, coalesce); }
    void EditFlow(Func<FlowOptions, FlowOptions> f) { if (_s.Primary != 0) _s.Modify([_s.Primary], e => e with { Flow = f(e.Flow ?? new FlowOptions()) }); }
    void EditPair(Func<MacroEvent, MacroEvent> f) { if (_s.Primary != 0) _s.Modify(_s.WithPartners([_s.Primary]), f); }

    /// <summary>Scale and/or rotate the selected positions around the middle of their bounding box.</summary>
    void TransformSelection(double scale, double rotDeg)
    {
        _scalePct.Text = "100"; _rotDeg.Text = "0";
        if (Math.Abs(scale - 1) < 1e-9 && Math.Abs(rotDeg) < 1e-9 || scale == 0) return;
        var picked = _s.Selected.Select(_s.IndexOfId).Where(i => i >= 0).Select(i => _s.Events[i]).ToList();
        if (Xf.BoundsOf(picked) is not { } b) return;
        var xf = new Xf(b.X + b.W / 2, b.Y + b.H / 2, scale, scale, rotDeg);
        _s.Modify(_s.Selected.ToList(), xf.Apply);
    }

    void OffsetSelection(int dx, int dy) =>
        _s.Modify(_s.Selected.ToList(), e => e.HasPos() ? e with { X = e.X + dx, Y = e.Y + dy } : e);

    void SetDirection(string d)
    {
        if (d is "Up" or "Down") Edit(e => e with { Kind = EventKind.Wheel, Delta = (d == "Up" ? 1 : -1) * Math.Max(1, Math.Abs(e.Delta)) });
        else Edit(e => e with { Kind = EventKind.HWheel, Delta = (d == "Right" ? 1 : -1) * Math.Max(1, Math.Abs(e.Delta)) });
    }

    void SetAmount(int units) => Edit(e => e with { Delta = (e.Delta < 0 ? -1 : 1) * Math.Max(1, units) });

    void KeyCapture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || _s.Primary == 0) return;
        uint r = NativeMethods.MapVirtualKey((uint)vk, 4); // MAPVK_VK_TO_VSC_EX
        bool ext = (r & 0xFF00) is 0xE000 or 0xE100;
        EditPair(ev => ev with { Vk = vk, Scan = (int)(r & 0xFF), Extended = ext });
    }

    // ---- layout helpers ----

    /// <summary>A rounded section with a small heading. Every group of fields in the inspector lives in one, so the panel reads as a stack of clear blocks.</summary>
    static Border Card(string title, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 2) });
        sp.Children.Add(body);
        return new Border { Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 10), Child = sp };
    }

    static void Row(Panel p, string label, Control c)
    {
        p.Children.Add(new TextBlock { Text = label, Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3) });
        c.Margin = new Thickness(0);
        p.Children.Add(c);
    }

    void Field(Panel p, string label, TextBox tb, Action<double> commit, string? initial = null)
    {
        if (initial != null) tb.Text = initial;
        Row(p, label, tb);
        void Go()
        {
            if (_updating) return;
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.TryParse(tb.Text, out v)) commit(v);
            else Refresh();
        }
        tb.LostFocus += (_, _) => Go();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
    }

    static Button Btn(string text, Action? a, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(11, 5, 11, 5) };
        if (style != null && Application.Current?.TryFindResource(style) is Style st) b.Style = st;
        if (a != null) b.Click += (_, _) => a();
        return b;
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void Refresh()
    {
        _updating = true;
        try
        {
            var pe = _s.PrimaryEvent;
            int n = _s.Selected.Count;
            _single.Visibility = pe == null ? Visibility.Collapsed : Visibility.Visible;
            _multi.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
            bool discordStep = pe is { Kind: EventKind.Discord };
            _dLinkCard.Visibility = _discord != null && (pe == null || discordStep) ? Visibility.Visible : Visibility.Collapsed;
            _dMsgCard.Visibility = discordStep ? Visibility.Visible : Visibility.Collapsed;   // before the early return: with nothing selected there is no message to edit
            if (_discord != null) RefreshLink();
            if (pe is not { } e)
            {
                _title.Text = "Inspector";
                _badge.Visibility = Visibility.Collapsed;
                _desc.Text = "Select an event in the path, timeline or table to inspect and edit it.";
                _info.Text = "";
                return;
            }
            int i = _s.IndexOfId(e.Id);
            _title.Text = $"Step {i + 1}" + (n > 1 ? $"  +{n - 1}" : "");
            _badge.Visibility = Visibility.Visible;
            _badgeText.Text = e.KindLabel();
            _desc.Text = e.Describe();
            _info.Text = _s.Analysis.Issues.TryGetValue(i, out var issue) ? "⚠ " + issue : $"Event id {e.Id}";
            _info.Foreground = _s.Analysis.Issues.ContainsKey(i) ? Theme.Warn : Theme.Muted;

            _kind.SelectedItem = e.Kind;
            _time.Text = F(e.T);
            _delay.Text = F(e.T - (i > 0 ? _s.Events[i - 1].T : 0));
            _pos.Visibility = e.HasPos() ? Visibility.Visible : Visibility.Collapsed;
            _x.Text = e.X.ToString(); _y.Text = e.Y.ToString();
            _keyCard.Visibility = e.IsKey() ? Visibility.Visible : Visibility.Collapsed;
            _keyBox.Text = $"{EventInfo.KeyName(e.Vk)}   (vk 0x{e.Vk:X2}, scan 0x{e.Scan:X2}{(e.Extended ? ", extended" : "")})";
            _dMsgCard.Visibility = discordStep ? Visibility.Visible : Visibility.Collapsed;
            if (discordStep && !_dText.IsKeyboardFocused) _dText.Text = e.Target ?? "";
            _dShot.IsChecked = e.Extended;
            _tgtCard.Visibility = e.IsTarget() ? Visibility.Visible : Visibility.Collapsed;
            bool when = e.Kind == EventKind.When;
            var fo = e.Flow ?? new FlowOptions();
            _whenCard.Visibility = when ? Visibility.Visible : Visibility.Collapsed;
            // "Where to click" is the existing click-position card, shown as it is; the other target-click cards stay hidden for a When block
            _smart.Visibility = e.Kind == EventKind.TargetClick || (when && fo.ClickFirst && fo.Condition != WhenCondition.NotFound) ? Visibility.Visible : Visibility.Collapsed;
            for (int c = 1; c < _smart.Children.Count; c++) _smart.Children[c].Visibility = when ? Visibility.Collapsed : Visibility.Visible;
            if (when)
            {
                var wl = _targets().Select(t => new { t.Id, t.Name }).ToList();
                if (e.Target != null && wl.All(t => t.Id != e.Target)) wl.Add(new { Id = e.Target, Name = EventInfo.TargetLabel(e.Target) });
                _whenTarget.ItemsSource = wl; _whenTarget.SelectedValue = e.Target;
                _whenCond.SelectedIndex = (int)fo.Condition; _whenCheck.SelectedIndex = fo.Always ? 1 : 0;
                if (!_whenWait.IsKeyboardFocused) _whenWait.Text = F(fo.WaitMs / 1000.0);
                bool checkedHere = !(fo.Always && fo.Condition != WhenCondition.NotFound);
                _whenKeep.IsChecked = fo.KeepWaiting;
                _whenKeep.Visibility = checkedHere && fo.Condition != WhenCondition.NotFound ? Visibility.Visible : Visibility.Collapsed;
                _whenWaitPanel.Visibility = checkedHere && !(fo.KeepWaiting && fo.Condition != WhenCondition.NotFound) ? Visibility.Visible : Visibility.Collapsed;
                _whenClick.IsChecked = fo.ClickFirst; _whenClick.Visibility = fo.Condition == WhenCondition.NotFound ? Visibility.Collapsed : Visibility.Visible;
                _whenDo.SelectedIndex = (int)fo.Do; _whenAfter.SelectedIndex = (int)fo.After;
                _whenRunPanel.Visibility = fo.Do == FlowDo.RunMacro ? Visibility.Visible : Visibility.Collapsed;
                _whenMacro.Text = fo.Macro == null ? "Drop a macro here" : "📁 " + (EventInfo.MacroNameOf?.Invoke(fo.Macro) ?? "(not in the library)");
                _whenMacro.Foreground = fo.Macro == null ? Theme.Muted : Theme.Text;
                _whenClear.IsEnabled = fo.Macro != null;
                var folder = (FolderMacros?.Invoke() ?? []).Select(m => new { m.Id, m.Name }).ToList();
                bool pickable = folder.Count > 0;
                _whenPick.Visibility = pickable ? Visibility.Visible : Visibility.Collapsed;
                _whenPickLabel.Visibility = _whenPick.Visibility;
                _whenPick.ItemsSource = folder.Prepend(new { Id = "", Name = "(choose a macro)" }).ToList();
                _whenPick.SelectedValue = fo.Macro != null && folder.Any(f => f.Id == fo.Macro) ? fo.Macro : "";
                if (!_whenMax.IsKeyboardFocused) _whenMax.Text = fo.MaxRuns.ToString();
                _tx.Text = e.X.ToString(); _ty.Text = e.Y.ToString();
                var wo = e.Step ?? new StepOptions();
                _anchor.Set(e.Target == null ? null : _targetImage(e.Target), wo.RelX, wo.RelY);
            }
            _skip.Visibility = e.Kind == EventKind.TargetClick ? Visibility.Collapsed : Visibility.Visible; // a click has the richer "If it still fails" choice
            if (e.IsTarget())
            {
                var list = _targets().Select(t => new { t.Id, t.Name }).ToList();
                if (e.Target != null && list.All(t => t.Id != e.Target)) list.Add(new { Id = e.Target, Name = EventInfo.TargetLabel(e.Target) }); // keep a missing target visible
                _targetBox.ItemsSource = list;
                _targetBox.SelectedValue = e.Target;
                _tmo.Text = (e.Delta > 0 ? e.Delta : 10000).ToString();
                _skip.IsChecked = e.Extended;
                if (e.Kind == EventKind.TargetClick)
                {
                    var o = e.Step ?? StepOptions.Legacy(e.Extended);
                    var inv = CultureInfo.InvariantCulture;
                    _tx.Text = e.X.ToString(); _ty.Text = e.Y.ToString();
                    _anchor.Set(e.Target == null ? null : _targetImage(e.Target), o.RelX, o.RelY);
                    _move.SelectedIndex = (int)o.Move; _glideMs.Text = o.GlideMs.ToString();
                    _minConf.Text = o.MinConfidence.ToString("0.00", inv); _retries.Text = o.Retries.ToString(); _retryDelay.Text = o.RetryDelayMs.ToString();
                    _stable.IsChecked = o.RequireStable; _minRepeat.Text = o.MinRepeatMs.ToString();
                    _onFail.SelectedIndex = (int)o.OnFail; _fbx.Text = o.FallbackX.ToString(); _fby.Text = o.FallbackY.ToString();
                    _verify.SelectedIndex = (int)o.Verify; _verifyTmo.Text = o.VerifyTimeoutMs.ToString();
                    var withNone = list.Prepend(new { Id = "", Name = "(none)" }).ToList();
                    _verifyTarget.ItemsSource = withNone; _verifyTarget.SelectedValue = o.VerifyTarget ?? "";
                    _recTarget.ItemsSource = withNone; _recTarget.SelectedValue = o.RecoveryTarget ?? "";
                    _recKey.Text = o.RecoveryKey > 0 ? EventInfo.KeyName(o.RecoveryKey) : "(none)";
                    if (!_afterText.IsKeyboardFocused) _afterText.Text = o.DiscordText ?? "";
                    _afterShot.IsChecked = o.DiscordShot;
                }
            }
            _btnCard.Visibility = e.IsButton() || e.Kind == EventKind.TargetClick || (when && fo.ClickFirst && fo.Condition != WhenCondition.NotFound) ? Visibility.Visible : Visibility.Collapsed;
            _button.SelectedItem = e.Button;
            _scrollCard.Visibility = e.IsWheel() ? Visibility.Visible : Visibility.Collapsed;
            _dir.SelectedItem = e.Name();
            _notch.Text = F(Math.Abs(e.Delta) / 120.0);
            _raw.Text = Math.Abs(e.Delta).ToString();
            var p = Partner();
            _holdCard.Visibility = e.IsDown() && p != null ? Visibility.Visible : Visibility.Collapsed;
            _holdBox.Text = p is { } pp ? F(pp.T - e.T) : "";
        }
        finally { _updating = false; }
    }

    void RefreshLink()
    {
        var (shown, source) = _discord!.State();
        _dShown.Text = shown.Length > 0 ? shown : "no link set";
        _dShown.Foreground = shown.Length > 0 ? Theme.Text : Theme.Muted;
        (_dSource.Text, _dSource.Foreground) = source switch
        {
            "own" => ("● This recording's own link", Ok),
            "default" => ("● Using the default link", Theme.Accent),
            _ => ("● No link yet", Theme.Muted),
        };
        _dTest.IsEnabled = source != "none";
        _dClear.IsEnabled = source == "own";
    }
}
