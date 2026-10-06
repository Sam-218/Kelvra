using System.Collections.ObjectModel;
using System.Text.Json;

namespace Kelvra;

/// <summary>
/// Overlay layouts as shareable .kelvra-overlay.json files. Imported files come from other people, so everything in
/// them is checked and clamped before it reaches the app: sizes, colours, text lengths and the number of items.
/// </summary>
public static class OverlayExchange
{
    public const string Extension = ".kelvra-overlay.json";
    public const string DialogFilter = "Kelvra overlay (*.kelvra-overlay.json)|*.kelvra-overlay.json|JSON files (*.json)|*.json";

    internal const long MaxFileBytes = 512 * 1024;
    internal const int MaxItems = 64;
    private const int MaxText = 80;
    private const int FormatVersion = 1;

    private sealed class Envelope
    {
        public string Kind { get; set; } = "";
        public int Version { get; set; }
        public OverlayProfile? Overlay { get; set; }
    }

    public static string Export(OverlayProfile profile)
    {
        var copy = profile.Clone(profile.Name);
        copy.X = profile.X;
        copy.Y = profile.Y;
        return JsonSerializer.Serialize(new Envelope { Kind = "kelvra-overlay", Version = FormatVersion, Overlay = copy }, AppSettings.Json);
    }

    public static void ExportFile(OverlayProfile profile, string path) => File.WriteAllText(path, Export(profile));

    /// <summary>Reads and checks an overlay file. Throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static OverlayProfile ImportFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("This file is too big to be a Kelvra overlay.");
        return Import(File.ReadAllText(path));
    }

    public static OverlayProfile Import(string json)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, AppSettings.Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("This isn't a Kelvra overlay file (it couldn't be read as one).");
        }
        if (envelope is not { Kind: "kelvra-overlay", Overlay: { } p })
            throw new InvalidDataException("This isn't a Kelvra overlay file.");
        if (envelope.Version > FormatVersion)
            throw new InvalidDataException("This overlay was made by a newer Kelvra. Update Kelvra to import it.");
        return Sanitize(p);
    }

    /// <summary>
    /// Makes an imported profile safe to use. Numbers were already clamped by the property setters while reading;
    /// here the texts, colours and enums are checked, the item list is limited, and it gets a new id (unlocked, so it can be placed).
    /// </summary>
    internal static OverlayProfile Sanitize(OverlayProfile p)
    {
        var d = new OverlayProfile();
        static string Color(string? value, string fallback) => ColorUtil.IsValidHex(value) ? value!.Trim() : fallback;
        static string Text(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : new string(value.Trim().Where(c => !char.IsControl(c)).Take(MaxText).ToArray());
        static T Known<T>(T value, T fallback) where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;

        var items = (p.Items ?? new ObservableCollection<OverlayItem>())
            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.SensorId) && i.SensorId.Length <= 512)
            .Take(MaxItems)
            .Select(i => new OverlayItem
            {
                SensorId = i.SensorId,
                Label = i.Label is null ? null : Text(i.Label, ""),
                ShowBar = i.ShowBar,
                ShowGraph = i.ShowGraph,
                HideLabel = i.HideLabel,
                ValueColor = i.ValueColor, // the setter drops anything that isn't #RRGGBB
                Decimals = i.Decimals,
                WarnAt = i.WarnAt,
                HotAt = i.HotAt,
            })
            .ToList();

        p.Id = Guid.NewGuid().ToString("N");
        p.Name = Text(p.Name, "Imported overlay");
        p.Enabled = true;
        p.Locked = false;
        p.Width = Math.Min(p.Width, 4000); // OverlayWindow moves off-screen positions back on screen
        p.Height = Math.Min(p.Height, 4000);
        p.FontFamily = Text(p.FontFamily, d.FontFamily);
        p.ValueFontFamily = Text(p.ValueFontFamily, "");
        p.Monitor = Text(p.Monitor, "");
        p.BackgroundColor = Color(p.BackgroundColor, d.BackgroundColor);
        p.AccentColor = Color(p.AccentColor, d.AccentColor);
        p.LabelColor = Color(p.LabelColor, d.LabelColor);
        p.ValueColor = Color(p.ValueColor, d.ValueColor);
        p.BorderColor = Color(p.BorderColor, d.BorderColor);
        p.WarmColor = Color(p.WarmColor, d.WarmColor);
        p.HotColor = Color(p.HotColor, d.HotColor);
        p.Layout = Known(p.Layout, d.Layout);
        p.LabelWeight = Known(p.LabelWeight, d.LabelWeight);
        p.ValueWeight = Known(p.ValueWeight, d.ValueWeight);
        p.TextEffect = Known(p.TextEffect, d.TextEffect);
        p.BarColor = Known(p.BarColor, d.BarColor);
        p.Items = new ObservableCollection<OverlayItem>(items);
        return p;
    }
}
