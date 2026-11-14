using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MacroStudio.Model;

namespace MacroStudio.Views;

/// <summary>Colors and text helpers shared by the custom-drawn views.</summary>
public static class Theme
{
    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly Brush Bg = Brush("#FFFFFF"), PanelBg = Brush("#F6F7F9"), Line = Brush("#E3E6EB"),
        Text = Brush("#1F2937"), Muted = Brush("#6B7280"), Accent = Brush("#2563EB"), AccentSoft = Brush("#DBEAFE"),
        Select = Brush("#F59E0B"), Playhead = Brush("#EF4444"), Warn = Brush("#DC2626"), Monitor = Brush("#EEF2F7"),
        Scroll = Brush("#0891B2"), Key = Brush("#6366F1"), White = Brush("#FFFFFF");

    public static readonly Pen LinePen = Pen(Line, 1), SelectPen = Pen(Select, 2), PlayheadPen = Pen(Playhead, 1.5), WarnPen = Pen(Warn, 1.5);

    public static Pen Pen(Brush b, double w)
    {
        var p = new Pen(b, w) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    public static Brush ButtonBrush(MouseBtn b) => b switch
    {
        MouseBtn.Left => Accent,
        MouseBtn.Right => Warn,
        MouseBtn.Middle => ButtonGreen,
        _ => ButtonPurple,
    };
    static readonly Brush ButtonGreen = Brush("#16A34A"), ButtonPurple = Brush("#9333EA");

    static readonly Color[] Stops = [(Color)ColorConverter.ConvertFromString("#2563EB"), (Color)ColorConverter.ConvertFromString("#9333EA"), (Color)ColorConverter.ConvertFromString("#F97316")];

    /// <summary>Early (blue) to late (orange) along the recording.</summary>
    public static Color TimeColor(double f)
    {
        f = Math.Clamp(f, 0, 1) * (Stops.Length - 1);
        int i = Math.Min((int)f, Stops.Length - 2);
        double k = f - i;
        Color a = Stops[i], b = Stops[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    public static readonly Typeface UiFont = new("Segoe UI"), MonoFont = new("Consolas");

    public static FormattedText Label(Visual v, string text, double size, Brush brush, bool mono = false, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? new Typeface(new FontFamily(mono ? "Consolas" : "Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : (mono ? MonoFont : UiFont),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    /// <summary>BGR Mat to WPF bitmap. Copies first: a sub-region Mat has the parent's row stride, so step*height would read past the end of the parent buffer.</summary>
    public static System.Windows.Media.Imaging.BitmapSource ToBitmap(OpenCvSharp.Mat m)
    {
        using var c = m.Clone();
        var b = System.Windows.Media.Imaging.BitmapSource.Create(c.Width, c.Height, 96, 96, PixelFormats.Bgr24, null, c.Data, (int)(c.Step() * c.Height), (int)c.Step());
        b.Freeze();
        return b;
    }

    public static string Time(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.###", CultureInfo.InvariantCulture) + "s" : ms.ToString("0.#", CultureInfo.InvariantCulture) + "ms";
}
