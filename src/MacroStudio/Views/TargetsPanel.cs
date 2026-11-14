using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Vision;
using OpenCvSharp;
using Window = System.Windows.Window;
using Rect = OpenCvSharp.Rect;

namespace MacroStudio.Views;

/// <summary>Register on-screen buttons as targets by dragging a box, and watch them being detected live.</summary>
public sealed class TargetsPanel : DockPanel, IDisposable
{
    public sealed record Item(Target T, ImageSource Thumb)
    {
        public string Name => T.Name;
        public string Group => string.IsNullOrWhiteSpace(T.Group) ? "(no group)" : T.Group;
        public string Id => "id " + T.Id;
        public string Info => $"threshold {T.Threshold:0.00}" + (T.ContextPad > 0 ? "  ·  identity" : "") + (T.Owner == Target.Everyone ? "  ·  shared" : "");
    }

    readonly TargetStore _store;
    readonly Detector _detector;
    readonly Window _owner;
    readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(8, 4, 8, 6), FontSize = 11 };
    readonly ToggleButton _live = new() { Content = "● Live detect", Margin = new Thickness(2) };
    readonly TextBox _threshold = new() { Width = 50, VerticalContentAlignment = VerticalAlignment.Center };
    readonly CheckBox _occ = new() { Content = "Allow partly hidden", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        ToolTip = "Also recognise the target when something covers part of it (matches the visible parts, then falls back to feature matching)." };
    readonly TextBox _minVis = new() { Width = 40, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Minimum percent of the target that must be visible (20-90)." };
    readonly TextBox _colorTol = new() { Width = 50, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "How much the target's colour may change and still match. 0 = identical colour, 30 = slight changes such as hover or tint (default), 250 or more = ignore colour." };
    Border _settings = null!;

    /// <summary>Asked when Live detect starts: should its boxes show up in screenshots and recordings?</summary>
    public Func<bool> OverlayCapturable { get; set; } = () => false;
    public void SetOverlayCapturable(bool on) { if (_overlay != null) _overlay.Capturable = on; }
    readonly ToggleButton _gear = new();
    readonly TargetWatcher _watcher;
    IDisposable? _sub;
    List<Target> _watched = new();
    int _renderQueued;
    DetectionOverlay? _overlay;
    bool _loading;

    /// <summary>Raised after targets are added, renamed, retuned or deleted.</summary>
    public event Action? Changed;

    public TargetsPanel(TargetStore store, Detector detector, TargetWatcher watcher, Window owner)
    {
        _store = store; _detector = detector; _watcher = watcher; _owner = owner;
        Background = Theme.White;

        Background = Brushes.Transparent;

        var head = new DockPanel { Margin = new Thickness(12, 8, 6, 2) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Icon("\uE722", "Capture a new target: drag a box around a button or icon", Capture));
        tools.Children.Add(Icon("\uE70F", "Rename the selected target", Rename));
        tools.Children.Add(Icon("\uE8B7", "Put the selected target in a group", SetGroup));
        tools.Children.Add(Icon("\uE74D", "Delete the selected target", Delete, danger: true));
        _all.Content = "\uE7B3"; _all.FontFamily = new FontFamily("Segoe MDL2 Assets"); _all.FontSize = 12; _all.Width = 28; _all.Height = 26; _all.Padding = new Thickness(0); _all.Margin = new Thickness(1, 0, 6, 0);
        _all.ToolTip = "Show the targets of every recording (not only this recording's)";
        _all.Checked += (_, _) => Refresh(null); _all.Unchecked += (_, _) => Refresh(null);
        tools.Children.Insert(0, _all);
        SetDock(tools, Dock.Right); head.Children.Add(tools);
        head.Children.Add(new TextBlock { Text = "Visual targets", FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("#111827"), VerticalAlignment = VerticalAlignment.Center });
        SetDock(head, Dock.Top); Children.Add(head);

        // the settings of the selected target, as one compact block under the list
        _live.Checked += (_, _) => SetLive(true);
        _live.Unchecked += (_, _) => SetLive(false);
        _live.Margin = new Thickness(0); _live.Padding = new Thickness(10, 5, 10, 5); _live.HorizontalAlignment = HorizontalAlignment.Stretch;
        var strip = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        // Live detect, and a gear that opens the selected target's settings. Selecting a target no longer opens them by itself.
        _gear.Content = ""; _gear.FontFamily = new FontFamily("Segoe MDL2 Assets"); _gear.FontSize = 13; _gear.Width = 34; _gear.Height = 30; _gear.Padding = new Thickness(0); _gear.Margin = new Thickness(6, 0, 0, 0);
        _gear.ToolTip = "Settings of the selected target (match threshold, colour tolerance, partly hidden)"; _gear.IsEnabled = false;
        _gear.Checked += (_, _) => ShowSettings(); _gear.Unchecked += (_, _) => ShowSettings();
        var top = new DockPanel();
        DockPanel.SetDock(_gear, Dock.Right);
        top.Children.Add(_gear); top.Children.Add(_live);
        strip.Children.Add(top);

        // the selected target's settings: one small card, label on the left and an equal-width field on the right; hidden until a target is selected
        foreach (var box in new[] { _threshold, _colorTol, _minVis })
        {
            box.Width = 62; box.Height = 26; box.Padding = new Thickness(4, 0, 4, 0); box.Margin = new Thickness(0);
            box.HorizontalContentAlignment = HorizontalAlignment.Center; box.VerticalContentAlignment = VerticalAlignment.Center; box.HorizontalAlignment = HorizontalAlignment.Right;
        }
        _occ.Margin = new Thickness(0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Line(int row, UIElement left, UIElement right)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (row > 0) { ((FrameworkElement)left).Margin = new Thickness(0, 6, 0, 0); ((FrameworkElement)right).Margin = new Thickness(8, 6, 0, 0); }
            else ((FrameworkElement)right).Margin = new Thickness(8, 0, 0, 0);
            Grid.SetRow(left, row); Grid.SetRow(right, row); Grid.SetColumn(right, 1);
            g.Children.Add(left); g.Children.Add(right);
        }
        TextBlock Label(string t, string tip) => new() { Text = t, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        Line(0, Label("Match threshold", "How closely the screen must match the picture (0.30 to 0.99)."), _threshold);
        Line(1, Label("Colour tolerance", _colorTol.ToolTip as string ?? ""), _colorTol);
        Line(2, _occ, new TextBlock());
        Line(3, Label("Minimum visible (%)", _minVis.ToolTip as string ?? ""), _minVis);
        _settings = new Border
        {
            Background = Theme.Brush("#F8FAFC"), BorderBrush = Theme.Brush("#E8EBF0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 8, 0, 0), Child = g, Visibility = Visibility.Collapsed,
        };
        strip.Children.Add(_settings);
        _status.Margin = new Thickness(2, 8, 2, 0);
        strip.Children.Add(_status);
        SetDock(strip, Dock.Bottom); Children.Add(strip);

        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Name}' FontWeight='SemiBold' FontSize='10.5' Foreground='#9CA3AF' Margin='12,6,0,1'/></DataTemplate>") });
        _list.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' Margin='0,1' ToolTip='{Binding Id}'>" +
            "<Image Source='{Binding Thumb}' Width='38' Height='28' Stretch='Uniform' RenderOptions.BitmapScalingMode='HighQuality'/>" +
            "<StackPanel Margin='8,0,0,0' VerticalAlignment='Center'><TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis'/>" +
            "<TextBlock Text='{Binding Info}' Foreground='#9CA3AF' FontSize='10'/></StackPanel></StackPanel></DataTemplate>");
        _list.SelectionChanged += (_, _) =>
        {
            _loading = true;
            _threshold.Text = One is Item i ? i.T.Threshold.ToString("0.00", CultureInfo.InvariantCulture) : "";
            _occ.IsChecked = One?.T.Occlusion ?? true;
            _minVis.Text = One is Item j ? (j.T.MinVisible * 100).ToString("0", CultureInfo.InvariantCulture) : "";
            _colorTol.Text = One is Item k ? k.T.ColorTolerance.ToString("0", CultureInfo.InvariantCulture) : "";
            _gear.IsEnabled = One != null;
            if (_list.SelectedItems.Count > 1) SetStatus($"{_list.SelectedItems.Count} targets selected. Delete removes them all, Group puts them all in one group.");
            ShowSettings();
            _minVis.IsEnabled = _occ.IsChecked == true;
            _loading = false;
        };
        _occ.Click += (_, _) =>
        {
            if (One is not Item i) return;
            i.T.Occlusion = _occ.IsChecked == true; _minVis.IsEnabled = i.T.Occlusion; _store.Save(i.T); Changed?.Invoke();
        };
        void CommitMinVisible()
        {
            if (_loading || One is not Item i) return;
            if (double.TryParse(_minVis.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 20 and <= 90)
            { i.T.MinVisible = p / 100; _store.Save(i.T); Changed?.Invoke(); }
            else _minVis.Text = (i.T.MinVisible * 100).ToString("0", CultureInfo.InvariantCulture);
        }
        void CommitColorTol()
        {
            if (_loading || One is not Item i) return;
            if (double.TryParse(_colorTol.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 500)
            { i.T.ColorTolerance = c; _store.Save(i.T); Changed?.Invoke(); }
            else _colorTol.Text = i.T.ColorTolerance.ToString("0", CultureInfo.InvariantCulture);
        }
        _colorTol.LostFocus += (_, _) => CommitColorTol();
        _colorTol.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitColorTol(); };
        _minVis.LostFocus += (_, _) => CommitMinVisible();
        _minVis.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitMinVisible(); };
        Children.Add(_list);

        void CommitThreshold()
        {
            if (_loading || One is not Item i) return;
            if (double.TryParse(_threshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0.3 and <= 0.99)
            { i.T.Threshold = v; _store.Save(i.T); Refresh(i.T.Id); Changed?.Invoke(); }
            else _threshold.Text = i.T.Threshold.ToString("0.00", CultureInfo.InvariantCulture);
        }
        _threshold.LostFocus += (_, _) => CommitThreshold();
        _threshold.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitThreshold(); };

        // Select several with Ctrl+click, Shift+click or Ctrl+A. Deselect: click the only selected target again, click the empty part of the list, or press Esc.
        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject src || FindAncestor<ScrollBar>(src) != null) return;      // the scroll bar is not "empty space"
            var item = ItemsControl.ContainerFromElement(_list, src) as ListBoxItem;
            if (item == null) _list.UnselectAll();
            else if (item.IsSelected && _list.SelectedItems.Count == 1 && Keyboard.Modifiers == ModifierKeys.None) { _list.UnselectAll(); e.Handled = true; }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _list.SelectedItems.Count > 0) { _list.UnselectAll(); e.Handled = true; }
            else if (e.Key == Key.Delete && _list.SelectedItems.Count > 0) { Delete(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy(); e.Handled = true; }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { Paste(); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" }; copy.Click += (_, _) => Copy();
        var paste = new MenuItem { Header = "Paste into this recording", InputGestureText = "Ctrl+V" }; paste.Click += (_, _) => Paste();
        var mine = new MenuItem { Header = "Keep in this recording only" }; mine.Click += (_, _) => MoveSelected(true);
        var all = new MenuItem { Header = "Share with every recording" }; all.Click += (_, _) => MoveSelected(false);
        menu.Items.Add(copy); menu.Items.Add(paste); menu.Items.Add(new Separator()); menu.Items.Add(mine); menu.Items.Add(all);
        _list.ContextMenu = menu;
        _list.ToolTip = "Ctrl+click or Shift+click to select several. Click the selected target again, click empty space, or press Esc to deselect. Delete removes the selection.";

        _list.SelectionChanged += (_, _) => { if (!_loading && _overlay != null) Resubscribe(); };
        Changed += () => { if (_overlay != null) Resubscribe(); }; // threshold / occlusion edits, rename, delete: hand the watcher the new settings
        Refresh(null);
        SetStatus("Press ＋ Capture, then drag a box around a button or icon.");
    }

    /// <summary>Every selected target (Ctrl+click, Shift+click, Ctrl+A).</summary>
    List<Item> Picked => _list.SelectedItems.OfType<Item>().ToList();
    /// <summary>The selected target when exactly one is selected: the settings card and Rename work on one target at a time.</summary>
    Item? One => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0] as Item : null;

    /// <summary>The open recording's id (null when it has none yet). Set by the window.</summary>
    public Func<string?> RecordingId { get; set; } = () => null;
    /// <summary>Gives the open recording an id if it has none, and returns it (a new target is saved under it).</summary>
    public Func<string> EnsureRecordingId { get; set; } = () => "";

    /// <summary>The targets this recording can use: its own and the shared ones.</summary>
    /// <summary>The ids of the macros whose folder the open macro is in: their targets are listed here too, so a sub macro uses them without capturing them again.</summary>
    public Func<ISet<string>>? InheritedOwners { get; set; }

    List<Target> Visible() { var me = RecordingId(); var used = Referenced(); var inh = InheritedOwners?.Invoke(); return _store.List().Where(t => _all.IsChecked == true || t.VisibleTo(me, used, inh)).ToList(); }

    /// <summary>The ids of the targets the open recording's steps use. Set by the window.</summary>
    public Func<ISet<string>> Referenced { get; set; } = () => new HashSet<string>();

    readonly ToggleButton _all = new();
    static List<string> _clipboard = new();    // copied targets: kept while the app runs, so they can be pasted into another recording

    void Copy()
    {
        var ids = Picked.Select(x => x.T.Id).ToList();
        if (ids.Count == 0) return;
        _clipboard = ids;
        SetStatus($"{ids.Count} target(s) copied. Open another recording and paste (Ctrl+V or right-click > Paste).");
    }

    void Paste()
    {
        var src = _clipboard.Where(id => _store.Get(id) != null).ToList();
        if (src.Count == 0) { SetStatus("Nothing to paste: select targets and press Ctrl+C first."); return; }
        string owner = EnsureRecordingId();
        var names = Visible().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var made = new List<Target>();
        foreach (var id in src)
        {
            var t = _store.Clone(id, owner);                                 // an independent copy: its own id, pixels and settings
            if (names.Contains(t.Name)) { t.Name += " copy"; _store.Save(t); }
            names.Add(t.Name); made.Add(t);
        }
        Refresh(null);
        foreach (var item in _list.Items.OfType<Item>().Where(i => made.Any(m => m.Id == i.T.Id))) _list.SelectedItems.Add(item);
        Changed?.Invoke();
        SetStatus($"Pasted {made.Count} target(s) into this recording.");
    }

    public IReadOnlyList<Target> Targets => Visible();

    /// <summary>A different recording was opened: show its targets.</summary>
    public void ReloadForRecording() { Refresh(null); if (_overlay != null) Resubscribe(); }

    void MoveSelected(bool toThisRecording)
    {
        var all = Picked;
        if (all.Count == 0) return;
        string owner = toThisRecording ? EnsureRecordingId() : Target.Everyone;
        foreach (var i in all) { i.T.Owner = owner; _store.Save(i.T); }
        Refresh(all[0].T.Id); Changed?.Invoke();
        SetStatus(toThisRecording ? $"{all.Count} target(s) now belong to this recording only." : $"{all.Count} target(s) are now shared by every recording.");
    }

    static Button Icon(string glyph, string tip, Action a, bool danger = false)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        if (Application.Current?.TryFindResource(danger ? "IconSmDanger" : "IconSm") is Style st) b.Style = st;
        b.Click += (_, _) => a();
        return b;
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d != null; d = (d is Visual or System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is T t) return t;
        return null;
    }

    /// <summary>The settings card is shown only when the gear is on AND a target is selected.</summary>
    void ShowSettings()
    {
        bool show = _gear.IsChecked == true && One != null;
        _settings.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The card takes room from the list: scroll so the selected target stays in view (after the layout has changed).
        if (show) Dispatcher.BeginInvoke(() => { if (One != null) _list.ScrollIntoView(One); }, DispatcherPriority.Loaded);
    }

    void SetStatus(string s) => _status.Text = s;

    void Refresh(string? select)
    {
        _loading = true;
        var items = Visible().Select(t => new Item(t, Thumb(_store.Image(t.Id)))).ToList();
        var view = new System.Windows.Data.ListCollectionView(items);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Item.Group)));
        _list.ItemsSource = view;
        _list.SelectedItem = items.FirstOrDefault(i => i.T.Id == select);
        _loading = false;
    }

    static BitmapSource Thumb(Mat m)
    {
        return Theme.ToBitmap(m);
    }

    async void Capture()
    {
        var state = _owner.WindowState;
        bool wasLive = _live.IsChecked == true;
        _live.IsChecked = false;
        _owner.WindowState = WindowState.Minimized;
        await Task.Delay(450); // let the window finish minimizing before the screen is frozen
        RegionPicker.Result? pick = null;
        try { pick = await RegionPicker.PickAsync(); }
        catch (Exception ex) { SetStatus("Could not capture the screen: " + ex.Message); }
        finally { _owner.WindowState = state == WindowState.Minimized ? WindowState.Normal : state; _owner.Activate(); }
        try
        {
            if (pick == null) return;
            using (pick.Frame) using (pick.Crop)
            {
                using var gray = new Mat();
                Cv2.CvtColor(pick.Crop, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.MeanStdDev(gray, out _, out var sd);
                var name = Prompt.Ask(_owner, "Name this target", $"Target {Visible().Count + 1}");
                if (string.IsNullOrWhiteSpace(name)) return;
                var v = Screens.Current().Virtual;
                double thr = Math.Round(_detector.AutoThreshold(pick.Frame, new Rect(pick.Rect.X - v.X, pick.Rect.Y - v.Y, pick.Rect.Width, pick.Rect.Height)), 2);

                // keep the surroundings too (when the whole ring is on screen): they tell identical-looking targets apart later
                const int pad = 24;
                Mat? context = null;
                var ring = new Rect(pick.Rect.X - v.X - pad, pick.Rect.Y - v.Y - pad, pick.Rect.Width + 2 * pad, pick.Rect.Height + 2 * pad);
                if (ring.X >= 0 && ring.Y >= 0 && ring.Right <= pick.Frame.Width && ring.Bottom <= pick.Frame.Height) context = new Mat(pick.Frame, ring).Clone();
                var group = _list.SelectedItem is Item cur ? cur.T.Group : "";
                var t = _store.Add(name.Trim(), pick.Crop, Screens.CurrentEnv().Dpi, v, thr, pick.Rect, context, pad, group);
                t.Owner = EnsureRecordingId(); _store.Save(t);   // a new target belongs to the recording it was captured in
                context?.Dispose();
                Refresh(t.Id);
                Changed?.Invoke();
                SetStatus($"Registered '{t.Name}' ({pick.Rect.Width}×{pick.Rect.Height}) with auto threshold {thr:0.00}." +
                          (sd.Val0 < 8 ? " Warning: this region is very plain, so it may match other flat areas; include some border or text." : ""));
            }
        }
        finally { if (wasLive) _live.IsChecked = true; }   // live detect pauses while picking; cancel and blank names must not leave it off
    }

    void Rename()
    {
        if (One is not Item i) { SetStatus("Select one target to rename it."); return; }
        var name = Prompt.Ask(_owner, "Rename target", i.T.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        i.T.Name = name.Trim(); _store.Save(i.T);
        Refresh(i.T.Id); Changed?.Invoke();
    }

    void SetGroup()
    {
        var all = Picked;
        if (all.Count == 0) return;
        var g = Prompt.Ask(_owner, all.Count == 1 ? "Group for this target (blank = none)" : $"Group for these {all.Count} targets (blank = none)", all[0].T.Group);
        if (g == null) return;
        foreach (var i in all) { i.T.Group = g.Trim(); _store.Save(i.T); }
        Refresh(all[0].T.Id); Changed?.Invoke();
    }

    void Delete()
    {
        var all = Picked;
        if (all.Count == 0) return;
        string what = all.Count == 1 ? $"target '{all[0].T.Name}'" : $"{all.Count} targets ({string.Join(", ", all.Take(4).Select(x => x.T.Name))}{(all.Count > 4 ? ", …" : "")})";
        if (MessageBox.Show(_owner, $"Delete {what}? Macro steps that use {(all.Count == 1 ? "it" : "them")} will report {(all.Count == 1 ? "it" : "them")} as missing.", "Macro Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var i in all) _store.Delete(i.T.Id);
        Refresh(null); Changed?.Invoke();
    }

    // ---- live detection (the shared watcher does the work; this only draws its answers) ----

    void SetLive(bool on)
    {
        if (on)
        {
            _overlay = new DetectionOverlay(OverlayCapturable());
            _watcher.Updated += OnWatcherUpdated;
            Resubscribe();
            SetStatus("Live detect on. Looking for " + (Picked.Count > 0 ? (Picked.Count == 1 ? "the selected target…" : "the selected targets…") : "all targets…"));
        }
        else
        {
            _watcher.Updated -= OnWatcherUpdated;
            _sub?.Dispose(); _sub = null;
            _overlay?.Dispose(); _overlay = null;
            SetStatus("Live detect off.");
        }
    }

    /// <summary>Tell the watcher which targets to watch (the selected one, or all) with their current settings.</summary>
    void Resubscribe()
    {
        var old = _sub;
        _watched = Picked.Count > 0 ? Picked.Select(x => x.T).ToList() : Visible();
        _sub = _watcher.Watch(_watched);
        old?.Dispose();
    }

    /// <summary>Called on the watcher thread; coalesce to one UI refresh at a time.</summary>
    void OnWatcherUpdated()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Render);
    }

    void Render()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        if (_overlay == null) return;
        var boxes = new List<(ScreenRect, string)>();
        var lines = new List<string>();
        foreach (var t in _watched)
        {
            if (_watcher.Get(t.Id) is not { Hit: { } x })
            {
                lines.Add($"{t.Name}: " + (_watcher.Get(t.Id)?.Note ?? "starting…"));
                continue;
            }
            string how = x.Method == "template" ? "" : x.Method == "patches" ? $"  partly hidden, {x.Visible:P0} visible" : "  by features (rotated / resized / hidden)";
            boxes.Add((new ScreenRect(x.Rect.X, x.Rect.Y, x.Rect.Width, x.Rect.Height), $"{t.Name}  {x.Score:0.00}" + (x.Method == "template" ? "" : $" · {x.Visible:P0}")));
            lines.Add($"{t.Name}: ({x.Center.X}, {x.Center.Y})  score {x.Score:0.00}" + (Math.Abs(x.Scale - 1) > 0.02 ? $"  scale {x.Scale:0.00}" : "") + how);
        }
        _overlay.Set(boxes);
        var st = _watcher.Stats;
        SetStatus(string.Join("\n", lines) + $"\nwatcher: {st.Tracked} tracked, {st.Searching} searching · CPU ≈ {st.Duty:P0} of one core · {st.WorkMs:0} ms/tick");
    }

    public void Dispose()
    {
        _watcher.Updated -= OnWatcherUpdated;
        _sub?.Dispose();
        _overlay?.Dispose();
    }
}
