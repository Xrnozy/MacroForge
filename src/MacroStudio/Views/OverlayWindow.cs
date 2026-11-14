using System.Windows;
using System.Windows.Media;
using MacroStudio.Model;
using MacroStudio.Native;

namespace MacroStudio.Views;

/// <summary>Click-through, capture-excluded boxes drawn over the live screen, one window per monitor.</summary>
public sealed class DetectionOverlay : IDisposable
{
    readonly List<(Window Win, Layer Layer)> _wins = new();

    /// <param name="capturable">Visible to screenshots, screen recorders and screen sharing. Off: hidden from every capture (and from the detector).</param>
    public DetectionOverlay(bool capturable = false)
    {
        foreach (var mon in Screens.Current().Monitors)
        {
            var layer = new Layer(mon);
            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                Topmost = true, ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0, Width = 100, Height = 100, Content = layer, Focusable = false,
            };
            WindowPlacement.Pin(win, mon);
            win.SourceInitialized += (_, _) => { WindowPlacement.ClickThrough(win); if (!capturable) WindowPlacement.ExcludeFromCapture(win); };
            win.Show();
            _wins.Add((win, layer));
        }
    }

    /// <summary>Switch capture visibility while the boxes are showing.</summary>
    public bool Capturable { set { foreach (var (w, _) in _wins) WindowPlacement.ExcludeFromCapture(w, !value); } }

    public void Set(IReadOnlyList<(ScreenRect Rect, string Label)> items)
    {
        foreach (var (_, layer) in _wins) layer.Items = items;
    }

    public void Dispose()
    {
        foreach (var (w, _) in _wins) w.Close();
        _wins.Clear();
    }

    sealed class Layer : FrameworkElement
    {
        readonly ScreenRect _mon;
        IReadOnlyList<(ScreenRect Rect, string Label)> _items = [];
        public Layer(ScreenRect mon) { _mon = mon; IsHitTestVisible = false; }

        public IReadOnlyList<(ScreenRect Rect, string Label)> Items { set { _items = value; InvalidateVisual(); } }

        protected override void OnRender(DrawingContext dc)
        {
            double k = ActualWidth / _mon.Width; // DIPs per physical pixel
            var halo = Theme.Pen(Theme.White, 5);
            var pen = Theme.Pen(Theme.Playhead, 2.5);
            foreach (var (r, label) in _items)
            {
                if (!_mon.Contains(r.X + r.Width / 2, r.Y + r.Height / 2)) continue;
                double pad = 4;
                var box = new Rect((r.X - _mon.X) * k - pad, (r.Y - _mon.Y) * k - pad, r.Width * k + 2 * pad, r.Height * k + 2 * pad); // outside the pixels being matched
                dc.DrawRectangle(null, halo, box);
                dc.DrawRectangle(null, pen, box);
                var t = Theme.Label(this, label, 12, Theme.White, bold: true);
                dc.DrawRectangle(Theme.Playhead, null, new Rect(box.X, box.Y - t.Height - 4, t.Width + 10, t.Height + 4));
                dc.DrawText(t, new Point(box.X + 5, box.Y - t.Height - 2));
            }
        }
    }
}
