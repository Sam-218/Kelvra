using System.Windows.Media;

namespace Kelvra.Tests;

/// <summary>The colour picker's maths, and custom accent colours that stay readable on both themes.</summary>
public class ColorTests
{
    private static Color C(string hex) => ColorUtil.Parse(hex, Colors.Magenta);

    private static readonly Color DarkPanel = C("#14171B"), White = Colors.White;

    [Theory]
    [InlineData("#FF0000")]
    [InlineData("#F5A524")]
    [InlineData("#2DD4BF")]
    [InlineData("#123456")]
    [InlineData("#808080")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void Colours_survive_the_trip_through_hsv_and_hsl(string hex)
    {
        var c = C(hex);
        var (h, s, v) = ColorMath.ToHsv(c);
        Assert.Equal(c, ColorMath.FromHsv(h, s, v));
        var (h2, s2, l) = ColorMath.ToHsl(c);
        Assert.Equal(c, ColorMath.FromHsl(h2, s2, l));
        Assert.Equal(hex, ColorMath.ToHex(c));
    }

    [Fact]
    public void Contrast_follows_the_wcag_formula()
    {
        Assert.Equal(21, ColorMath.Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1, ColorMath.Contrast(C("#7AA2FF"), C("#7AA2FF")), 2);
        Assert.Equal(4.54, ColorMath.Contrast(C("#767676"), Colors.White), 2); // the classic "just passes" grey
    }

    [Fact]
    public void Too_dark_or_too_light_colours_are_shifted_just_enough_and_keep_their_hue()
    {
        var navy = C("#1E2A78");
        var lifted = ColorMath.EnsureContrast(navy, DarkPanel, 4.5);
        Assert.InRange(ColorMath.Contrast(lifted, DarkPanel), 4.5, 4.8); // enough, not more than needed
        Assert.Equal(ColorMath.ToHsv(navy).H, ColorMath.ToHsv(lifted).H, 0);

        var lemon = C("#FFF59D");
        var deepened = ColorMath.EnsureContrast(lemon, White, 4.5);
        Assert.InRange(ColorMath.Contrast(deepened, White), 4.5, 4.8);

        var fine = C("#F5A524");
        Assert.Equal(fine, ColorMath.EnsureContrast(fine, DarkPanel, 4.5)); // already readable: untouched
    }

    [Theory]
    [InlineData("#1E2A78")] // navy
    [InlineData("#FFF59D")] // pale lemon
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#E11D48")]
    public void Any_custom_accent_stays_readable_on_both_themes(string hex)
    {
        foreach (bool dark in new[] { true, false })
        {
            var (line, fill, text, soft) = Accents.Shades(hex, dark);
            var panel = dark ? DarkPanel : White;
            Assert.True(ColorMath.Contrast(line, panel) >= 4.5, $"{hex} lines on the {(dark ? "dark" : "light")} panel");
            Assert.True(ColorMath.Contrast(text, fill) >= 4.5, $"{hex} button text");
            if (dark) Assert.True(ColorMath.Contrast(fill, panel) >= 3, $"{hex} buttons on the dark panel");
            Assert.Equal((fill.R, fill.G, fill.B), (soft.R, soft.G, soft.B));
        }
    }

    [Fact]
    public void Presets_keep_their_hand_tuned_shades_and_are_saved_by_name()
    {
        var (line, fill, _, _) = Accents.Shades("Amber", dark: false);
        Assert.Equal((C("#B45309"), C("#F5A524")), (line, fill));
        Assert.Equal("Amber", Accents.SettingFor(C("#F5A524")));
        Assert.Equal("#12AB34", Accents.SettingFor(C("#12AB34")));
        Assert.Equal(C("#12AB34"), Accents.ColorOf("#12AB34"));
        Assert.Equal(C("#2DD4BF"), Accents.ColorOf("Teal"));
    }

    [Fact]
    public void Graphite_buttons_are_the_text_colour_and_readable_on_both_themes()
    {
        foreach (bool dark in new[] { true, false })
        {
            var (line, fill, text, _) = Accents.Shades("Graphite", dark);
            var panel = dark ? DarkPanel : White;
            Assert.Equal(line, fill); // white buttons on graphite, near-black ones on paper: no colour of their own
            Assert.True(ColorMath.Contrast(line, panel) >= 4.5);
            Assert.True(ColorMath.Contrast(text, fill) >= 4.5);
        }
        Assert.Equal("Graphite", Accents.Default);
    }

    [Theory]
    [InlineData("#12AB34", "#12AB34")]
    [InlineData("Teal", "Teal")]
    [InlineData("Chartreuse", "Graphite")] // not a preset, not a colour
    [InlineData("#12AB3", "Graphite")]
    [InlineData("#8012AB34", "Graphite")]  // no see-through accents
    public void Accent_setting_takes_any_rgb_colour(string saved, string loaded)
    {
        using var dir = new TempDir();
        string path = dir.File("s.json", System.Text.Encoding.UTF8.GetBytes($"{{ \"Accent\": \"{saved}\" }}"));
        Assert.Equal(loaded, AppSettings.Load(path).Accent);
    }
}
