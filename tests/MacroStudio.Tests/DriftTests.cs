using MacroStudio.Vision;
using OpenCvSharp;
namespace MacroStudio.Tests;
public class DriftTests
{
    [Fact]
    public void A_tracked_partly_covered_target_does_not_slide_along_a_stroke()
    {
        using var tpl = new Mat(60, 160, MatType.CV_8UC3, new Scalar(35, 35, 35));
        Cv2.Line(tpl, new Point(5, 14), new Point(120, 14), Scalar.All(220), 2);          // a long horizontal stroke (like a minimise icon)
        Cv2.Rectangle(tpl, new Rect(130, 8, 14, 14), Scalar.All(220), 1);                // a small square (maximise)
        Cv2.Line(tpl, new Point(30, 40), new Point(150, 40), Scalar.All(140), 2);
        using var scene = new Mat(200, 400, MatType.CV_8UC3, new Scalar(35, 35, 35));
        using (var d = new Mat(scene, new Rect(100, 80, 160, 60))) tpl.CopyTo(d);
        using (var cover = new Mat(scene, new Rect(100, 80, 40, 60))) cover.SetTo(new Scalar(80, 50, 40));   // the left part is covered by something else
        using var ctx = new FrameContext(scene);
        var r = PatchVoting.Recheck(ctx, tpl, 1, new Point(100, 80), 0.1);
        Assert.NotNull(r);
        Assert.Equal((100, 80), (r.Value.Rect.X, r.Value.Rect.Y));
    }
}
