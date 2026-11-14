using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MacroStudio.Vision;

/// <summary>Reads text off the screen with Windows' own OCR (offline, no training, uses the languages installed for the user).</summary>
public static class TextRecognizer
{
    public readonly record struct Word(string Text, Rect Rect);

    static readonly Lazy<OcrEngine?> Engine = new(() => { try { return OcrEngine.TryCreateFromUserProfileLanguages(); } catch { return null; } });

    /// <summary>
    /// Kill-switch: set by the MACRO_NO_OCR=1 environment variable at startup, or by a test. While on, OCR counts as
    /// unavailable even though the engine exists — the app must run without it, not fall over.
    /// </summary>
    internal static volatile bool Disabled =
        Environment.GetEnvironmentVariable("MACRO_NO_OCR") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Test seam: pins <see cref="Available"/> either way so tests exercise both modes on any machine. Null = decide for real.</summary>
    internal static bool? ForceAvailable;

    /// <summary>True when the OS OCR engine is there <b>and</b> not switched off (<see cref="Disabled"/> / MACRO_NO_OCR).</summary>
    public static bool Available => ForceAvailable ?? (Engine.Value != null && !Disabled);

    /// <summary>One OCR pass may take at most this long: a stalled OCR service must never wedge the watcher thread. A timeout just means no text this tick.</summary>
    internal static int TimeoutMs = 2000;

    /// <summary>Words and where they are, one list per text line. Small images are enlarged first (OCR wants letters ~20+ px high); positions are mapped back.</summary>
    public static List<List<Word>> Read(Mat bgr)
    {
        var lines = new List<List<Word>>();
        if (bgr.Empty() || !Available || Engine.Value is not { } eng) return lines;
        double k = bgr.Height < 60 ? 3 : bgr.Height < 120 ? 2 : 1;
        k = Math.Min(k, Math.Max(1, OcrEngine.MaxImageDimension / (double)Math.Max(bgr.Width, bgr.Height)));
        using var big = new Mat(); using var bgra = new Mat();
        if (k > 1) Cv2.Resize(bgr, big, new Size((int)(bgr.Width * k), (int)(bgr.Height * k)), 0, 0, InterpolationFlags.Cubic); else bgr.CopyTo(big);
        Cv2.CvtColor(big, bgra, ColorConversionCodes.BGR2BGRA);
        var res = Recognize(eng, bgra);
        if (res == null) return lines;
        foreach (var l in res.Lines)
            lines.Add(l.Words.Select(w => new Word(w.Text, new Rect((int)(w.BoundingRect.X / k), (int)(w.BoundingRect.Y / k), Math.Max(1, (int)(w.BoundingRect.Width / k)), Math.Max(1, (int)(w.BoundingRect.Height / k))))).ToList());
        return lines;
    }

    /// <summary>OCR one prepared frame within <see cref="TimeoutMs"/>; null on timeout or engine error (both mean "no text", never a wedge).</summary>
    static OcrResult? Recognize(OcrEngine eng, Mat bgra)
    {
        SoftwareBitmap? bmp = null;
        try
        {
            bmp = ToBitmap(bgra);
            var task = eng.RecognizeAsync(bmp).AsTask();
            if (task.Wait(TimeoutMs)) return task.Result;
            var keep = bmp; bmp = null;   // the stalled call still holds the bitmap: free it when it finally ends, not now
            _ = task.ContinueWith(_ => keep.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            return null;
        }
        catch { return null; }
        finally { bmp?.Dispose(); }
    }

    /// <summary>
    /// BGRA frame → SoftwareBitmap. The pixel array comes from a pool (a 1080p frame is 8 MB — no LOH churn per call);
    /// GetArray&lt;byte&gt; is not used because it rejects CV_8UC4 Mats. Writing straight into the bitmap's buffer would
    /// need IMemoryBufferByteAccess, which the CsWinRT projection cannot cast to — one managed copy is the reliable path.
    /// </summary>
    static SoftwareBitmap ToBitmap(Mat bgra)
    {
        int len = checked((int)(bgra.Width * 4L * bgra.Height));
        var px = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
        try
        {
            long step = (long)bgra.Step(); int row = bgra.Width * 4, h = bgra.Height;
            for (long y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(bgra.Data, (int)(y * step)), px, (int)(y * row), row);   // row-wise: safe even if the Mat is not continuous
            return SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(0, len), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(px); }
    }

    public static string TextOf(Mat bgr) => string.Join(" ", Read(bgr).Select(l => string.Join(" ", l.Select(w => w.Text))));

    /// <summary>Lower case, letters and digits only: OCR is unreliable about spacing, case and punctuation.</summary>
    public static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// 0..1: how much of <paramref name="a"/> shows up in <paramref name="b"/>, as letters in order (1 = all of it is there).
    /// Extra letters in <paramref name="b"/> cost nothing — one misread letter on a 9-char word still scores ~0.89 — and a
    /// missing letter costs exactly its share of the needed length. Not symmetric on purpose: the first argument is the text we need.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0) return b.Length == 0 ? 1 : 0;
        var d = new int[b.Length + 1];                       // longest common subsequence of a and b, one rolling row
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = 0;
            for (int j = 1; j <= b.Length; j++) { int t = d[j]; d[j] = a[i - 1] == b[j - 1] ? prev + 1 : Math.Max(d[j], d[j - 1]); prev = t; }
        }
        return d[b.Length] / (double)a.Length;
    }

    /// <summary>Every run of words in one line that reads as <paramref name="wanted"/> (about 85% of it found), best first. The rectangle covers just those words.</summary>
    public static List<Detection> Find(List<List<Word>> lines, string wanted, double minSimilarity = 0.85)
    {
        var want = Norm(wanted); var found = new List<(Detection D, int Len)>();
        if (want.Length == 0) return [];
        foreach (var line in lines)
            for (int i = 0; i < line.Count; i++)
            {
                string acc = "";
                for (int j = i; j < line.Count && acc.Length < want.Length * 1.3 + 2; j++)
                {
                    acc += Norm(line[j].Text);
                    double s = Similarity(want, acc);
                    if (s >= minSimilarity)
                    {
                        var r = line.Skip(i).Take(j - i + 1).Select(w => w.Rect).Aggregate((a, b) => a | b);
                        found.Add((new Detection(r, s, 1, "text"), acc.Length));
                    }
                }
            }
        // best score first; a tie goes to the tighter run (the same words inside a longer read are the same find, but the snug box locates the label)
        return found.OrderByDescending(f => f.D.Score).ThenBy(f => f.Len).Select(f => f.D).ToList();
    }
}
