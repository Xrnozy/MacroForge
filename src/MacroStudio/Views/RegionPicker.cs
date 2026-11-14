using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Model;
using MacroStudio.Native;
using MacroStudio.Vision;
using OpenCvSharp;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using CvRect = OpenCvSharp.Rect;
using Window = System.Windows.Window;

namespace MacroStudio.Views;

/// <summary>
/// Freezes every monitor into a dimmed full-screen overlay; the user drags a rectangle around a button.
/// Result is in virtual-desktop physical pixels. Caller owns (disposes) Frame and Crop.
/// </summary>
public static class RegionPicker
{
    public sealed record Result(ScreenRect Rect, Mat Crop, Mat Frame);

    public static Task<Result?> PickAsync()
    {
        var tcs = new TaskCompletionSource<Result?>();
        var desk = Screens.Current();
        var windows = new List<Window>();
        Mat? frame = null;
        bool done = false;

        void Finish(ScreenRect? picked)
        {
            if (done) return;
            done = true;
            foreach (var w in windows) try { w.Close(); } catch { }   // Finish can run inside a Closed handler: that window is already closing
            if (picked is { } r && frame != null)
            {
                var local = new CvRect(r.X - desk.Virtual.X, r.Y - desk.Virtual.Y, r.Width, r.Height);
                using var view = new Mat(frame, local);   // the view must not outlive its frame
                tcs.SetResult(new Result(r, view.Clone(), frame));
            }
            else { frame?.Dispose(); tcs.SetResult(null); }
        }

        try
        {
            frame = ScreenCapture.Capture(desk.Virtual);
            foreach (var mon in desk.Monitors)
            {
                var local = new CvRect(mon.X - desk.Virtual.X, mon.Y - desk.Virtual.Y, mon.Width, mon.Height);
                using var sub = new Mat(frame, local);
                var bmp = Theme.ToBitmap(sub);
                var surface = new Surface(bmp, mon, Finish);
                var win = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                    Left = 0, Top = 0, Width = 100, Height = 100, Content = surface, Background = Brushes.Black, Cursor = Cursors.Cross,
                };
                WindowPlacement.Pin(win, mon);
                win.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
                win.Closed += (_, _) => Finish(null);   // any window closing (Alt+F4, shutdown) cancels: the task must never hang with the frame undisposed
                windows.Add(win);
            }
        }
        catch (Exception ex)
        {
            frame?.Dispose();
            foreach (var w in windows) try { w.Close(); } catch { }
            tcs.SetException(ex);
            return tcs.Task;
        }
        foreach (var w in windows) w.Show();
        windows[0].Activate();
        Keyboard.Focus((IInputElement)windows[0].Content);   // Esc must reach the surface even when nothing in it took focus yet
        return tcs.Task;
    }

    sealed class Surface : FrameworkElement
    {
        readonly BitmapSource _img;
        readonly ScreenRect _monitor;
        readonly Action<ScreenRect?> _done;
        Point? _a, _b;

        public Surface(BitmapSource img, ScreenRect monitor, Action<ScreenRect?> done) { _img = img; _monitor = monitor; _done = done; Focusable = true; }

        double K => _monitor.Width / Math.Max(1, ActualWidth); // physical pixels per DIP

        Rect Sel => _a is { } a && _b is { } b ? new Rect(a, b) : Rect.Empty;

        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawImage(_img, full);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, full);
            var s = Sel;
            if (!s.IsEmpty && s.Width > 0 && s.Height > 0)
            {
                dc.PushClip(new RectangleGeometry(s));
                dc.DrawImage(_img, full);
                dc.Pop();
                dc.DrawRectangle(null, Theme.Pen(Theme.Accent, 2), s);
                var t = Theme.Label(this, $"{s.Width * K:0} × {s.Height * K:0}", 12, Theme.White, bold: true);
                dc.DrawRectangle(Theme.Accent, null, new Rect(s.X, Math.Max(0, s.Y - t.Height - 6), t.Width + 10, t.Height + 4));
                dc.DrawText(t, new Point(s.X + 5, Math.Max(0, s.Y - t.Height - 4)));
            }
            var hint = Theme.Label(this, "Drag a box around the button or icon   ·   Esc to cancel", 14, Theme.White, bold: true);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null, new Rect((ActualWidth - hint.Width) / 2 - 12, 16, hint.Width + 24, hint.Height + 12));
            dc.DrawText(hint, new Point((ActualWidth - hint.Width) / 2, 22));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { _a = _b = e.GetPosition(this); CaptureMouse(); InvalidateVisual(); }

        protected override void OnMouseMove(MouseEventArgs e) { if (_a != null) { _b = e.GetPosition(this); InvalidateVisual(); } }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            var s = Sel;
            if (s.IsEmpty) return;
            int x = (int)Math.Round(s.X * K), y = (int)Math.Round(s.Y * K), w = (int)Math.Round(s.Width * K), h = (int)Math.Round(s.Height * K);
            x = Math.Clamp(x, 0, _monitor.Width - 1); y = Math.Clamp(y, 0, _monitor.Height - 1);
            w = Math.Min(w, _monitor.Width - x); h = Math.Min(h, _monitor.Height - y);
            if (w < 8 || h < 8) { _a = _b = null; InvalidateVisual(); return; } // accidental click
            _done(new ScreenRect(_monitor.X + x, _monitor.Y + y, w, h));
        }
    }
}
