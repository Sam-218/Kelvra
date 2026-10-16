using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace Kelvra;

/// <summary>
/// The accent colour (Settings → Appearance): one of the named presets, or any colour as "#RRGGBB". A preset has a
/// bright version for dark mode and a hand-picked darker one for text and lines in light mode; filled buttons use the
/// bright one with dark text. Contrast (checked): bright on the dark panel ≥ 6.6:1 · dark on white ≥ 5.0:1 ·
/// dark text on bright fill ≥ 7.1:1. A custom colour gets the same guarantees worked out (see <see cref="Shades"/>).
/// </summary>
public static class Accents
{
    public sealed record Accent(string Name, Color Bright, Color Deep);

    public static readonly Accent[] All =
    {
        // Kelvra 2's default: no colour at all. Buttons, tabs and selection are white on graphite (near-black on paper),
        // so the thermal scale is the only colour on screen and always means heat.
        new("Graphite", Rgb(0xE8EBEF), Rgb(0x15181C)),
        new("Amber", Rgb(0xF5A524), Rgb(0xB45309)),
        new("Teal", Rgb(0x2DD4BF), Rgb(0x0F766E)),
        new("Blue", Rgb(0x7AA2FF), Rgb(0x1D4ED8)),
        new("Violet", Rgb(0xA78BFA), Rgb(0x6D28D9)),
        new("Pink", Rgb(0xF472B6), Rgb(0xBE185D)),
        new("Orange", Rgb(0xFF8A4C), Rgb(0xC2410C)),
        new("Green", Rgb(0x4ADE80), Rgb(0x15803D)),
    };

    public const string Default = "Graphite";

    // The panels accents are drawn on (SurfaceBrush in Dark.xaml / Light.xaml)
    private static readonly Color DarkSurface = Rgb(0x14171B), LightSurface = Rgb(0xFFFFFF);
    private static readonly Color DarkText = Rgb(0x0D0F12), White = Rgb(0xFFFFFF);

    public static Accent Find(string? name) =>
        All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>A colour the user picked freely: "#RRGGBB".</summary>
    public static bool IsCustom(string? value) => value != null && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");

    public static bool IsKnown(string? value) =>
        IsCustom(value) || All.Any(a => string.Equals(a.Name, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour a setting stands for: a preset's bright colour, or the custom colour.</summary>
    public static Color ColorOf(string? value) => IsCustom(value) ? ColorUtil.Parse(value, All[0].Bright) : Find(value).Bright;

    /// <summary>What to save for a picked colour: a preset's name when it's one of them (keeps its light-mode shade), else "#RRGGBB".</summary>
    public static string SettingFor(Color c) =>
        All.FirstOrDefault(a => a.Bright.R == c.R && a.Bright.G == c.G && a.Bright.B == c.B)?.Name ?? ColorMath.ToHex(c);

    /// <summary>
    /// The accent's colours for one theme. <c>Line</c> (text, icons, outlines on a panel) always reaches 4.5:1 against
    /// the panel; <c>Fill</c> (buttons, selected chips) is the colour itself – in dark mode at least 3:1 against the
    /// panel; <c>Text</c> is whichever of near-black and white reads better on the fill.
    /// </summary>
    public static (Color Line, Color Fill, Color Text, Color Soft) Shades(string? value, bool dark)
    {
        if (!IsCustom(value))
        {
            var a = Find(value);
            // Graphite fills with the text colour of the theme: white buttons on graphite, near-black ones on paper
            if (a.Name == "Graphite")
                return dark ? (a.Bright, a.Bright, DarkText, Rgb(0x1B1F25)) : (a.Deep, a.Deep, White, Rgb(0xF0F2F5));
            return (dark ? a.Bright : a.Deep, a.Bright, dark ? DarkText : Rgb(0x1A1205), Soft(a.Bright, dark));
        }
        var c = ColorOf(value);
        var surface = dark ? DarkSurface : LightSurface;
        var fill = dark ? ColorMath.EnsureContrast(c, surface, 3) : c;
        var text = ColorMath.Contrast(fill, DarkText) >= ColorMath.Contrast(fill, White) ? DarkText : White;
        return (ColorMath.EnsureContrast(c, surface, 4.5), fill, text, Soft(fill, dark));
    }

    /// <summary>Overrides the theme dictionary's accent brushes (top-level resources win over merged dictionaries).</summary>
    public static void Apply(ResourceDictionary resources, string? value, bool dark)
    {
        var (line, fill, text, soft) = Shades(value, dark);
        resources["AccentBrush"] = Frozen(line);
        resources["AccentFillBrush"] = Frozen(fill);
        resources["AccentTextBrush"] = Frozen(text);
        resources["AccentSoftBrush"] = Frozen(soft);
        resources["AccentColor"] = line;
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Soft(Color c, bool dark) => Color.FromArgb(dark ? (byte)0x24 : (byte)0x1F, c.R, c.G, c.B);

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
