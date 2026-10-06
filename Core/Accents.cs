using System.Windows;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// The accent colours offered under Settings → Appearance. Each has a bright version for dark mode and a darker one
/// for text and lines in light mode; filled buttons always use the bright one with dark text.
/// Contrast (checked): bright on the dark panel ≥ 6.6:1 · dark on white ≥ 5.0:1 · dark text on bright fill ≥ 7.1:1.
/// </summary>
public static class Accents
{
    public sealed record Accent(string Name, Color Bright, Color Deep);

    public static readonly Accent[] All =
    {
        new("Amber", Rgb(0xF5A524), Rgb(0xB45309)),
        new("Teal", Rgb(0x2DD4BF), Rgb(0x0F766E)),
        new("Blue", Rgb(0x7AA2FF), Rgb(0x1D4ED8)),
        new("Violet", Rgb(0xA78BFA), Rgb(0x6D28D9)),
        new("Pink", Rgb(0xF472B6), Rgb(0xBE185D)),
        new("Orange", Rgb(0xFF8A4C), Rgb(0xC2410C)),
        new("Green", Rgb(0x4ADE80), Rgb(0x15803D)),
    };

    public const string Default = "Amber";

    public static Accent Find(string? name) =>
        All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool IsKnown(string? name) => All.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Overrides the theme dictionary's accent brushes (top-level resources win over merged dictionaries).</summary>
    public static void Apply(ResourceDictionary resources, string? name, bool dark)
    {
        var a = Find(name);
        var line = dark ? a.Bright : a.Deep;
        resources["AccentBrush"] = Frozen(line);
        resources["AccentFillBrush"] = Frozen(a.Bright);
        resources["AccentTextBrush"] = Frozen(dark ? Rgb(0x0C0E11) : Rgb(0x1A1205));
        resources["AccentSoftBrush"] = Frozen(Color.FromArgb(dark ? (byte)0x24 : (byte)0x1F, a.Bright.R, a.Bright.G, a.Bright.B));
        resources["AccentColor"] = line;
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
