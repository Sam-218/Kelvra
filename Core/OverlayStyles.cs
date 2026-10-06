using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One-click looks for an overlay. They change only appearance and layout, never the sensors or position.</summary>
public static class OverlayStyles
{
    public sealed record Style(string Name, string Description, Action<OverlayProfile> Apply);

    public static readonly Style[] All =
    {
        new("Instrument", "Kelvra's own look: graphite panel, amber headers, monospaced numbers", p =>
        {
            Common(p);
            (p.BackgroundColor, p.BackgroundOpacity, p.CornerRadius, p.BorderColor, p.BorderThickness) = ("#0C0E11", 86, 6, "#22262E", 1);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#F5A524", "#98A1AE", "#E7E9EC");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight, p.LabelWeight) = ("Segoe UI", "Cascadia Mono", OverlayWeight.Normal, OverlayWeight.Normal);
            (p.Padding, p.TextEffect, p.BarColor) = (10, OverlayTextEffect.None, OverlayBarColor.Accent);
        }),
        new("Minimal", "No panel, outlined white text: as little of the game hidden as possible", p =>
        {
            Common(p);
            (p.BackgroundOpacity, p.BorderThickness, p.Padding) = (0, 0, 4);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#FFFFFF", "#D9DDE3", "#FFFFFF");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight, p.LabelWeight) = ("Segoe UI", "", OverlayWeight.Bold, OverlayWeight.SemiBold);
            (p.TextEffect, p.ShowHeaders) = (OverlayTextEffect.Outline, false);
        }),
        new("Classic", "The well-known on-screen-display look: orange labels, white numbers, Consolas", p =>
        {
            Common(p);
            (p.BackgroundOpacity, p.BorderThickness, p.Padding) = (0, 0, 4);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#FF9F1A", "#FFB347", "#FFFFFF");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight, p.LabelWeight) = ("Consolas", "", OverlayWeight.Bold, OverlayWeight.Bold);
            p.TextEffect = OverlayTextEffect.Shadow;
        }),
        new("Glass", "A light, frosted panel with a thin white edge", p =>
        {
            Common(p);
            (p.BackgroundColor, p.BackgroundOpacity, p.CornerRadius, p.BorderColor, p.BorderThickness) = ("#FFFFFF", 16, 14, "#FFFFFF", 1);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#A5F3FC", "#F2F4F8", "#FFFFFF");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight) = ("Segoe UI Variable Display", "", OverlayWeight.SemiBold);
            (p.Padding, p.TextEffect) = (14, OverlayTextEffect.Shadow);
        }),
        new("Neon", "Dark panel, magenta edge, glowing green numbers", p =>
        {
            Common(p);
            (p.BackgroundColor, p.BackgroundOpacity, p.CornerRadius, p.BorderColor, p.BorderThickness) = ("#0A0A12", 72, 10, "#FF2BD6", 1);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#FF2BD6", "#7DF9FF", "#39FF14");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight) = ("Bahnschrift", "", OverlayWeight.SemiBold);
            p.TextEffect = OverlayTextEffect.None;
        }),
        new("Kelvra classic", "The original Kelvra overlay", p =>
        {
            Common(p);
            (p.BackgroundColor, p.BackgroundOpacity, p.CornerRadius, p.BorderThickness) = ("#101014", 75, 10, 0);
            (p.AccentColor, p.LabelColor, p.ValueColor) = ("#FFAA28", "#E6E6EB", "#00FF90");
            (p.FontFamily, p.ValueFontFamily, p.ValueWeight, p.LabelWeight) = ("Segoe UI", "", OverlayWeight.SemiBold, OverlayWeight.Normal);
            p.TextEffect = OverlayTextEffect.Auto;
        }),
    };

    /// <summary>Settings every style resets, so switching styles never leaves a mix behind.</summary>
    private static void Common(OverlayProfile p)
    {
        p.Opacity = 100;
        p.ValueScale = 1;
        p.ShowHeaders = true;
        p.BarColor = OverlayBarColor.Value;
        (p.WarmColor, p.HotColor) = ("#FFD23C", "#FF5046");
    }

    public static Style? Find(string name) => All.FirstOrDefault(s => s.Name == name);
}

/// <summary>How an overlay item's value is written: Kelvra's normal formatting, or a chosen number of decimals and no unit.</summary>
public static class OverlayFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Value(SensorType type, float? raw, int decimals, bool units)
    {
        if (raw is not float v || float.IsNaN(v)) return "-";
        if (decimals < 0)
        {
            string text = SensorFormat.Format(type, v);
            return units ? text : StripUnit(text);
        }
        string number = SensorFormat.ToDisplay(type, v).ToString("F" + decimals, Inv);
        string unit = SensorFormat.Unit(type);
        return units && unit.Length > 0 ? $"{number} {unit}" : number;
    }

    /// <summary>"47 °C" → "47", "1.2 MB/s" → "1.2"; durations ("1:02:03") have no unit and stay as they are.</summary>
    internal static string StripUnit(string text)
    {
        int space = text.LastIndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>0 = normal, 1 = warm, 2 = hot, from raw value and limits.</summary>
    public static int Level(float? raw, (float Warm, float Hot)? limits)
    {
        if (raw is not float v || float.IsNaN(v) || limits is not { } l) return 0;
        return v >= l.Hot ? 2 : v >= l.Warm ? 1 : 0;
    }
}
