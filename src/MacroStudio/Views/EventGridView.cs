using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MacroStudio.Editing;
using MacroStudio.Model;

namespace MacroStudio.Views;

/// <summary>
/// Virtualized event table: one monospaced text run per visible row, so 100k+ events scroll instantly
/// and selection stays in sync with the other views. A When block can be opened like a folder: the macro it runs is listed
/// underneath, indented (read only; double-click it to open that macro for editing).
/// </summary>
public sealed class EventGridView : Grid
{
    sealed class Body : FrameworkElement
    {
        const double RowH = 26, HeaderH = 30, MaxChildRows = 500;

        /// <param name="Kind">0 an event of this macro, 1 the folder row of a connected macro, 2 an event of that macro, 3 "drop a macro here".</param>
        /// <param name="Event">The event of this macro the row belongs to (for rows 1-3: the When block that owns them).</param>
        /// <param name="K">For a kind-2 event row: the index in the connected macro, formatted only when drawn (-1 = use Text).</param>
        readonly record struct Row(int Event, byte Kind, string Text = "", string? Macro = null, int K = -1);

        readonly EditSession _s;
        readonly ScrollBar _bar;
        readonly Func<string, MacroEvent[]?> _load;
        readonly Dictionary<string, MacroEvent[]?> _children = new();
        Row[]? _list;                       // null = one row per event (the fast path for big recordings)
        int[] _mainEv = [], _mainRow = [];  // the rows that are events of this macro: event index -> row (ascending)
        bool _hideMoves;
        int _top, _anchor = -1, _hover = -1, _dropRow = -1;

        public event Action<int, string>? MacroDropped;
        public event Action<double>? WhenChipDropped;
        public event Action<string>? OpenMacroRequested;

        public Body(EditSession s, ScrollBar bar, Func<string, MacroEvent[]?> load)
        {
            _s = s; _bar = bar; _load = load;
            Focusable = true;
            AllowDrop = true;
            ClipToBounds = true;
            s.EventsChanged += () => { Rebuild(); InvalidateVisual(); };
            s.PlayheadChanged += InvalidateVisual;
            s.BreakpointsChanged += InvalidateVisual;
            s.SelectionChanged += () => { Reveal(); InvalidateVisual(); };
            bar.ValueChanged += (_, _) => { _top = (int)bar.Value; InvalidateVisual(); };
            SizeChanged += (_, _) => { Rebuild(); InvalidateVisual(); };
        }

        public bool HideMoves { get => _hideMoves; set { _hideMoves = value; Rebuild(); InvalidateVisual(); } }

        /// <summary>The library changed (a connected macro was edited, renamed or deleted): list it again.</summary>
        public void Invalidate() { _children.Clear(); Rebuild(); InvalidateVisual(); }

        public int RowCount => Count;
        int Count => _list?.Length ?? _s.Events.Length;
        Row RowAt(int r) => _list is { } l ? l[r] : new Row(r, 0);
        int EventAt(int row) => _list is { } l ? l[row].Event : row;
        int Visible => Math.Max(1, (int)((ActualHeight - HeaderH) / RowH));

        int RowOf(int eventIndex)
        {
            if (_list == null) return eventIndex;
            int r = Array.BinarySearch(_mainEv, eventIndex);
            return r < 0 ? -1 : _mainRow[r];
        }

        MacroEvent[]? Child(string id)
        {
            if (!_children.TryGetValue(id, out var c)) _children[id] = c = _load(id);
            return c;
        }

        void Rebuild()
        {
            var ev = _s.Events;
            bool nested = false;
            if (_s.Expanded.Count > 0) for (int i = 0; i < ev.Length && !nested; i++) nested = ev[i].IsWhen() && _s.Expanded.Contains(ev[i].Id);
            if (!_hideMoves && !nested) { _list = null; _mainEv = _mainRow = []; UpdateBar(); return; }
            var list = new List<Row>(); var mev = new List<int>(); var mrow = new List<int>();
            for (int i = 0; i < ev.Length; i++)
            {
                if (_hideMoves && ev[i].Kind is EventKind.MouseMove or EventKind.RelMove) continue;
                mev.Add(i); mrow.Add(list.Count); list.Add(new Row(i, 0));
                if (ev[i].IsWhen() && _s.Expanded.Contains(ev[i].Id)) AddChildren(list, i, ev[i]);
            }
            _list = list.ToArray(); _mainEv = mev.ToArray(); _mainRow = mrow.ToArray();
            UpdateBar();
        }

        void AddChildren(List<Row> list, int owner, MacroEvent e)
        {
            var f = e.Flow ?? new FlowOptions();
            if (f.Do != FlowDo.RunMacro) return;
            if (f.Macro == null) { list.Add(new Row(owner, 3, "Drop a macro here (drag one from the library on the left)")); return; }
            string name = EventInfo.MacroNameOf?.Invoke(f.Macro) ?? "(not in the library)";
            var ch = Child(f.Macro);
            if (ch == null) { list.Add(new Row(owner, 1, $"📁 {name}", f.Macro)); return; }
            list.Add(new Row(owner, 1, $"📁 {name}   {ch.Length:N0} events, {EventInfo.Seconds(ch.Length > 0 ? ch[^1].T : 0)}   (double-click to open)", f.Macro));
            for (int k = 0; k < ch.Length && k < MaxChildRows; k++) list.Add(new Row(owner, 2, "", f.Macro, k));
            if (ch.Length > MaxChildRows) list.Add(new Row(owner, 2, $"... {ch.Length - MaxChildRows:N0} more", f.Macro));
        }

        static string ChildCells(MacroEvent[] ch, int k)
        {
            var e = ch[k];
            double delta = e.T - (k > 0 ? ch[k - 1].T : 0);
            string name = e.IsKey() || e.IsButton() || e.IsMarker() ? e.Name() : "";
            string x = e.HasPos() ? e.X.ToString() : "", y = e.HasPos() ? e.Y.ToString() : "";
            return e.IsWhen() ? $"{k + 1,4} {e.T,9:0.0}  {e.WhenText()}" : $"{k + 1,4} {e.T,9:0.0} {delta,8:0.0}  {e.Kind,-10} {name,-12} {x,6} {y,6}";
        }

        void UpdateBar()
        {
            int max = Math.Max(0, Count - Visible + 1);
            _bar.Maximum = max;
            _bar.ViewportSize = Visible;
            _bar.LargeChange = Visible;
            _top = Math.Min(_top, max);
            _bar.Value = _top;
        }

        void Reveal()
        {
            int ix = _s.IndexOfId(_s.Primary);
            int r = ix < 0 ? -1 : RowOf(ix);
            if (r < 0) return;
            UpdateBar();
            if (r < _top || r >= _top + Visible - 1) { _top = Math.Max(0, r - Visible / 2); _bar.Value = Math.Min(_top, _bar.Maximum); }
        }

        string Cells(int i)
        {
            var e = _s.Events[i];
            var a = _s.Analysis;
            double delta = e.T - (i > 0 ? _s.Events[i - 1].T : 0);
            string issue = a.Issues.TryGetValue(i, out var m) ? m : "";
            if (e.IsWhen()) return $"{i + 1,6} {e.T,11:0.0} {delta,9:0.0}  {"When",-10} {e.WhenText()}{(issue.Length > 0 ? "   ⚠ " + issue : "")}";
            string hold = e.IsDown() && a.Partner[i] >= 0 ? (_s.Events[a.Partner[i]].T - e.T).ToString("0.0") : "";
            string scroll = e.Kind == EventKind.Wheel ? $"{(e.Delta >= 0 ? "up" : "down")} {Math.Abs(e.Delta) / 120.0:0.##} ({e.Delta})"
                          : e.Kind == EventKind.HWheel ? $"{(e.Delta >= 0 ? "right" : "left")} {Math.Abs(e.Delta) / 120.0:0.##} ({e.Delta})" : "";
            if (e.IsTarget()) scroll = $"{e.Button}, wait {(e.Delta > 0 ? e.Delta : 10000) / 1000.0:0.#}s{(e.Extended ? ", skip" : "")}"; // target click: details live in the scroll column
            string x = e.HasPos() || e.IsTarget() ? e.X.ToString() : "", y = e.HasPos() || e.IsTarget() ? e.Y.ToString() : ""; // for a target click these are the click offset
            string name = e.IsKey() || e.IsButton() || e.IsMarker() ? e.Name() : "";
            return $"{i + 1,6} {e.T,11:0.0} {delta,9:0.0}  {e.Kind,-10} {name,-12} {x,6} {y,6}  {scroll,-20} {hold,9}  {issue}";
        }

        static readonly Brush HeaderBg = Theme.Brush("#F8FAFC"), PrimaryBg = Theme.Brush("#E0E7FF"), SelectedBg = Theme.Brush("#EEF2FF"), HoverBg = Theme.Brush("#F3F5F9"), FolderBg = Theme.Brush("#F8FAFC"), DropBg = Theme.Brush("#DBEAFE");
        static readonly Pen GuidePen = Theme.Pen(Theme.Brush("#CBD5E1"), 1.5), DropPen = Theme.Pen(Theme.Accent, 2);

        protected override void OnMouseMove(MouseEventArgs e) { int r = RowAtY(e.GetPosition(this).Y); if (r != _hover) { _hover = r; InvalidateVisual(); } }
        protected override void OnMouseLeave(MouseEventArgs e) { if (_hover != -1) { _hover = -1; InvalidateVisual(); } }

        static readonly string Header = $"{"#",6} {"T (ms)",11} {"Δ (ms)",9}  {"Type",-10} {"Key/Button",-12} {"X",6} {"Y",6}  {"Scroll",-20} {"Hold ms",9}  Issue";

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(HeaderBg, null, new Rect(0, 0, w, HeaderH));
            dc.DrawText(Theme.Label(this, Header, 11, Theme.Muted, mono: true, bold: true), new Point(14, 8));
            dc.DrawLine(Theme.LinePen, new Point(0, HeaderH), new Point(w, HeaderH));

            var ev = _s.Events;
            int play = _s.PlayheadIndex, playRow = play < 0 ? -1 : RowOf(play);
            for (int k = 0; k <= Visible && _top + k < Count; k++)
            {
                int r = _top + k;
                var row = RowAt(r);
                double y = HeaderH + k * RowH;
                var band = new Rect(6, y + 1, Math.Max(0, w - 12), RowH - 2);
                if (r == _dropRow) { dc.DrawRoundedRectangle(DropBg, DropPen, band, 6, 6); }
                if (row.Kind != 0)
                {
                    // the connected macro: indented under its When block, muted, with a guide line like a folder tree
                    dc.DrawLine(GuidePen, new Point(26, y), new Point(26, y + RowH));
                    if (row.Kind == 1 && r != _dropRow) dc.DrawRoundedRectangle(FolderBg, null, new Rect(34, y + 1, Math.Max(0, w - 40), RowH - 2), 6, 6);
                    var fb = row.Kind == 1 ? Theme.Text : Theme.Muted;
                    dc.DrawText(Theme.Label(this, row.K >= 0 && Child(row.Macro!) is { } ch ? ChildCells(ch, row.K) : row.Text, 12, fb, mono: row.Kind == 2, bold: row.Kind == 1), new Point(row.Kind == 1 ? 42 : 44, y + 5));
                    continue;
                }
                int i = row.Event;
                var e = ev[i];
                bool sel = _s.Selected.Contains(e.Id);
                if (sel) dc.DrawRoundedRectangle(e.Id == _s.Primary ? PrimaryBg : SelectedBg, null, band, 6, 6);
                else if (r == _hover && r != _dropRow) dc.DrawRoundedRectangle(HoverBg, null, band, 6, 6);
                bool issue = _s.Analysis.Issues.ContainsKey(i);
                var brush = issue ? Theme.Warn : e.Kind is EventKind.MouseMove or EventKind.RelMove ? Theme.Muted : Theme.Text;
                dc.DrawText(Theme.Label(this, Cells(i), 12, brush, mono: true), new Point(14, y + 5));
                if (e.IsWhen() && (e.Flow?.Do ?? FlowDo.RunMacro) == FlowDo.RunMacro) dc.DrawText(Theme.Label(this, _s.Expanded.Contains(e.Id) ? "▾" : "▸", 14, Theme.Accent, bold: true), new Point(14, y + 3));   // the folder arrow
                if (r == playRow) dc.DrawRoundedRectangle(Theme.Playhead, null, new Rect(2, y + 4, 3, RowH - 8), 1.5, 1.5);
                if (_s.IsBreakpoint(e.Id)) dc.DrawEllipse(Theme.Warn, null, new Point(10, y + RowH / 2), 3.5, 3.5); // breakpoint: playback pauses before this event
            }
        }

        // ---- interaction ----

        int RowAtY(double y) => y < HeaderH || Count == 0 ? -1 : Math.Min(Count - 1, _top + (int)((y - HeaderH) / RowH));

        void SelectRange(int from, int to)
        {
            if (from > to) (from, to) = (to, from);
            _s.Select(Enumerable.Range(from, to - from + 1).Select(r => _s.Events[EventAt(r)].Id).Distinct().ToList());
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            var pos = e.GetPosition(this);
            int r = RowAtY(pos.Y);
            if (r < 0 || Count == 0) return;
            var row = RowAt(r);
            if (e.ClickCount == 2 && row.Macro != null) { OpenMacroRequested?.Invoke(row.Macro); return; }
            int id = _s.Events[EventAt(r)].Id;
            if (row.Kind == 0 && _s.Events[row.Event].IsWhen() && (_s.Events[row.Event].Flow?.Do ?? FlowDo.RunMacro) == FlowDo.RunMacro && pos.X >= 8 && pos.X <= 34)   // the folder arrow: open / close
            {
                if (!_s.Expanded.Remove(id)) _s.Expanded.Add(id);
                _s.SelectOne(id); _anchor = r;
                Rebuild(); InvalidateVisual();
                return;
            }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0) SelectRange(_anchor, r);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _s.Toggle(id); _anchor = r; }
            else { _s.SelectOne(id); _anchor = r; }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e) =>
            _bar.Value = Math.Clamp(_top - e.Delta / 120 * 3, 0, _bar.Maximum);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int cur = _s.IndexOfId(_s.Primary) is var ix && ix >= 0 ? RowOf(ix) : -1;
            int target = e.Key switch
            {
                Key.Down => NextMain(cur, 1),
                Key.Up => NextMain(cur, -1),
                Key.PageDown => cur + Visible,
                Key.PageUp => cur - Visible,
                Key.Home => 0,
                Key.End => Count - 1,
                _ => -2,
            };
            if (target == -2 || Count == 0) return;
            target = Math.Clamp(target, 0, Count - 1);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0) SelectRange(_anchor, target);
            else { _s.SelectOne(_s.Events[EventAt(target)].Id); _anchor = target; }
            e.Handled = true;
        }

        /// <summary>The row of the next / previous event of this macro (the rows of a connected macro belong to their block).</summary>
        int NextMain(int cur, int dir)
        {
            if (_list == null || cur < 0) return cur + dir;
            int p = Array.BinarySearch(_mainRow, cur);
            if (p < 0) return cur + dir;
            return _mainRow[Math.Clamp(p + dir, 0, _mainRow.Length - 1)];
        }

        // ---- drag and drop: a library macro onto a When block, or the When chip anywhere ----

        protected override void OnDragOver(DragEventArgs e)
        {
            int r = RowAtY(e.GetPosition(this).Y);
            int dropRow = -1;
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(MacroDrag.WhenChip)) e.Effects = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent(MacroDrag.Format) && r >= 0 && _s.Events[EventAt(r)].IsWhen()) { e.Effects = DragDropEffects.Link; dropRow = RowOf(EventAt(r)); }
            if (dropRow != _dropRow) { _dropRow = dropRow; InvalidateVisual(); }
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e) { if (_dropRow != -1) { _dropRow = -1; InvalidateVisual(); } }

        protected override void OnDrop(DragEventArgs e)
        {
            _dropRow = -1; InvalidateVisual();
            int r = RowAtY(e.GetPosition(this).Y);
            if (e.Data.GetData(MacroDrag.Format) is string macro && r >= 0 && _s.Events[EventAt(r)].IsWhen()) MacroDropped?.Invoke(EventAt(r), macro);
            else if (e.Data.GetDataPresent(MacroDrag.WhenChip)) WhenChipDropped?.Invoke(r >= 0 ? _s.Events[EventAt(r)].T + 0.5 : _s.Duration + 1);
            e.Handled = true;
        }
    }

    readonly Body _body;

    public EventGridView(EditSession s, Func<string, MacroEvent[]?>? loadMacro = null)
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bar = new ScrollBar { Orientation = Orientation.Vertical, SmallChange = 1 };
        _body = new Body(s, bar, loadMacro ?? (_ => null));
        SetColumn(bar, 1);
        Children.Add(_body);
        Children.Add(bar);
    }

    public bool HideMoves { get => _body.HideMoves; set => _body.HideMoves = value; }

    /// <summary>A library macro was dropped on the When block at this event index.</summary>
    public event Action<int, string>? MacroDropped { add => _body.MacroDropped += value; remove => _body.MacroDropped -= value; }
    /// <summary>The When chip was dropped; the value is the time to insert at.</summary>
    public event Action<double>? WhenChipDropped { add => _body.WhenChipDropped += value; remove => _body.WhenChipDropped -= value; }
    /// <summary>A connected macro was double-clicked: open it for editing.</summary>
    public event Action<string>? OpenMacroRequested { add => _body.OpenMacroRequested += value; remove => _body.OpenMacroRequested -= value; }
    public void Invalidate() => _body.Invalidate();
    /// <summary>Rows shown: the events plus the rows of opened connected macros.</summary>
    public int RowCount => _body.RowCount;
}
