using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Editing;
using MacroStudio.Model;
using MacroStudio.Views;

namespace MacroStudio.Tests;

public class InspectorTargetTests
{
    static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw new Exception("inspector failed", err);
    }

    [Fact]
    public void Inspector_shows_every_adaptive_step_kind_and_the_anchor_picker_edits_the_step()
    {
        Sta(() =>
        {
            var s = new EditSession();
            var opts = new StepOptions { RelX = 0.5, RelY = 0.5, MinConfidence = 0.9, Retries = 3, OnFail = FailAction.UseRecordedPosition, FallbackX = 10, FallbackY = 20,
                Verify = VerifyMode.OtherTargetAppears, VerifyTarget = "b", RecoveryKey = 0x1B, RecoveryTarget = "b" };
            s.Load(new Macro
            {
                Events =
                [
                    new MacroEvent(0, EventKind.TargetClick, Delta: 5000, Target: "a", Step: opts),
                    new MacroEvent(100, EventKind.TargetClick, Delta: 5000, Target: "a", Extended: true),      // legacy step without options
                    new MacroEvent(200, EventKind.WaitTarget, Delta: 3000, Target: "a"),
                    new MacroEvent(300, EventKind.WaitGone, Delta: 3000, Target: "missing"),                   // target no longer in the library
                ],
            }, null);

            var bmp = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgr24, null, new byte[40 * 20 * 3], 40 * 3); bmp.Freeze();
            var panel = new InspectorPanel(s, () => [("a", "Play"), ("b", "Close")], id => id == "a" ? bmp : null);
            foreach (var e in s.Events)
            {
                s.SelectOne(e.Id);
                panel.Measure(new Size(320, 1400)); panel.Arrange(new Rect(0, 0, 320, 1400)); panel.UpdateLayout();
                var rt = new RenderTargetBitmap(320, 1400, 96, 96, PixelFormats.Pbgra32);
                rt.Render(panel);                                                                                // must not throw for any kind
            }

            // dragging the anchor picker edits the selected click through the session (one undo step) and keeps its other options
            s.SelectOne(s.Events[0].Id);
            var picker = FindDescendant<AnchorPicker>(panel)!;
            picker.Measure(new Size(300, 140)); picker.Arrange(new Rect(0, 0, 300, 140));
            var before = s.Events[0].Step!;
            picker.RaiseSetForTest(-0.5, 0.25);
            Assert.Equal((-0.5, 0.25), (s.Events[0].Step!.RelX, s.Events[0].Step!.RelY));
            Assert.Equal((before.Retries, before.OnFail, before.FallbackX), (s.Events[0].Step!.Retries, s.Events[0].Step!.OnFail, s.Events[0].Step!.FallbackX));
            s.Undo();
            Assert.Equal((0.5, 0.5), (s.Events[0].Step!.RelX, s.Events[0].Step!.RelY));
        });
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } d) return d;
        }
        return null;
    }
}
