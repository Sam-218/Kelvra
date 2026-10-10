using System.Windows.Media;

namespace Kelvra;

/// <summary>Colour maths for the colour picker and custom accents: HSV / HSL ↔ RGB and WCAG contrast.</summary>
internal static class ColorMath
{
    /// <summary>Hue 0–360°, saturation and value 0–1.</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>Hue 0–360°, saturation and lightness 0–1.</summary>
    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double l = (max + min) / 2;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (ToHsv(c).H, Math.Clamp(s, 0, 1), l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        double v = l + Math.Clamp(s, 0, 1) * Math.Min(l, 1 - l);
        return FromHsv(h, v == 0 ? 0 : 2 * (1 - l / v), v);
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    public static double Luminance(Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>WCAG contrast ratio, 1–21 (4.5 is enough for normal text, 3 for large text and lines).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// <paramref name="c"/> as it is if it stands out from <paramref name="background"/> by <paramref name="ratio"/>;
    /// otherwise lighter (on a dark background) or darker (on a light one), by just enough, keeping its hue.
    /// </summary>
    public static Color EnsureContrast(Color c, Color background, double ratio)
    {
        if (Contrast(c, background) >= ratio) return c;
        var (h, s, l) = ToHsl(c);
        double ok = Luminance(background) < 0.5 ? 1 : 0, bad = l; // white / black always passes
        for (int i = 0; i < 24; i++)
        {
            double mid = (ok + bad) / 2;
            if (Contrast(FromHsl(h, s, mid), background) >= ratio) ok = mid;
            else bad = mid;
        }
        return FromHsl(h, s, ok);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
