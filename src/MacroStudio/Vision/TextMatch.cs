using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// Text-backed target matching: a target with <see cref="Target.Text"/> set is found by reading the screen with
/// <see cref="TextRecognizer"/> (Windows OCR, offline) instead of comparing pixels — the label is found again even
/// when the button around it is redrawn, re-themed or moved. Rectangles come back in frame coordinates; the caller
/// shifts them by the frame's <see cref="FrameContext.Origin"/> into desktop pixels.
/// </summary>
public static class TextMatch
{
    /// <summary>Where screen words come from. Swappable in tests, so the text-target plumbing runs on machines without an OS OCR engine. Default: Windows OCR.</summary>
    internal static Func<Mat, List<List<TextRecognizer.Word>>> Reader = TextRecognizer.Read;

    // OCR runs once per captured frame, not once per target: the words of a Mat are cached on the Mat itself, so every
    // text target — and the tracked-target check — sharing one monitor capture in a tick shares one OCR pass.
    static readonly ConditionalWeakTable<Mat, List<List<TextRecognizer.Word>>> Words = new();

    /// <summary>Words of this frame, one list per text line (cached on the frame's pixels; OCR is not run twice for the same capture).</summary>
    public static List<List<TextRecognizer.Word>> Lines(Mat bgr) => Words.GetValue(bgr, m => Reader(m));

    /// <summary>Forget cached OCR results — tests call this after swapping <see cref="Reader"/>.</summary>
    internal static void Reset() => Words.Clear();

    /// <summary>Everywhere <paramref name="text"/> is read in this frame, best first (frame coordinates).</summary>
    public static List<Detection> Find(FrameContext ctx, string text, double minSimilarity = 0.85) =>
        TextRecognizer.Find(Lines(ctx.Bgr), text, minSimilarity);
}
