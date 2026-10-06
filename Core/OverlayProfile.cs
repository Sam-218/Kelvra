using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvra;

/// <summary>Vertical list, one line, or a grid of tiles.</summary>
public enum OverlayLayout { Vertical, Horizontal, Grid }

/// <summary>Where an overlay sits: dragged freely (Custom) or pinned to an edge/corner of a monitor.</summary>
public enum OverlayAnchor { Custom, TopLeft, TopCenter, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Keeps text readable over bright game scenes. Auto = a shadow when the background is mostly see-through.</summary>
public enum OverlayTextEffect { Auto, None, Shadow, Outline }

public enum OverlayWeight { Normal, SemiBold, Bold }

/// <summary>Bar colour: the value's colour (turns yellow/red with warnings) or the header colour.</summary>
public enum OverlayBarColor { Value, Accent }

/// <summary>One sensor shown on an overlay. Everything beyond the sensor itself is optional styling.</summary>
public sealed class OverlayItem : ObservableObject
{
    private string _sensorId = "";
    private string? _label;
    private bool _showBar;
    private bool _showGraph;
    private bool _hideLabel;
    private string? _valueColor;
    private int _decimals = -1;
    private float? _warnAt, _hotAt;
    private string _sensorName = "";
    private string _hardwareName = "";
    private bool _isExpanded;

    public string SensorId { get => _sensorId; set => Set(ref _sensorId, value); }

    /// <summary>Optional user label; falls back to the sensor's own name.</summary>
    public string? Label { get => _label; set => Set(ref _label, string.IsNullOrWhiteSpace(value) ? null : value); }

    public bool ShowBar { get => _showBar; set => Set(ref _showBar, value); }
    /// <summary>A small line graph of the last <see cref="OverlayProfile.GraphSeconds"/> next to the value.</summary>
    public bool ShowGraph { get => _showGraph; set => Set(ref _showGraph, value); }
    /// <summary>Only the value (e.g. a big clock).</summary>
    public bool HideLabel { get => _hideLabel; set => Set(ref _hideLabel, value); }
    /// <summary>"#RRGGBB" for this value only; null = the overlay's value colour.</summary>
    public string? ValueColor { get => _valueColor; set => Set(ref _valueColor, ColorUtil.IsValidHex(value) ? value!.Trim() : null); }
    /// <summary>-1 = Kelvra's default precision for the sensor type, else 0–3 decimal places.</summary>
    public int Decimals { get => _decimals; set => Set(ref _decimals, Math.Clamp(value, -1, 3)); }
    /// <summary>Own warning limits in the sensor's raw unit (°C, %, W …); null = the overlay's limits for temperature/load.</summary>
    public float? WarnAt { get => _warnAt; set => Set(ref _warnAt, value is float f && float.IsFinite(f) ? f : null); }
    public float? HotAt { get => _hotAt; set => Set(ref _hotAt, value is float f && float.IsFinite(f) ? f : null); }

    // Resolved at runtime for the editor list
    [JsonIgnore] public string SensorName { get => _sensorName; set => Set(ref _sensorName, value); }
    [JsonIgnore] public string HardwareName { get => _hardwareName; set => Set(ref _hardwareName, value); }
    /// <summary>Editor only: the item's detail options are unfolded.</summary>
    [JsonIgnore] public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>The editor's "Value colour" field: "" when the overlay colour is used.</summary>
    [JsonIgnore]
    public string ValueColorText
    {
        get => _valueColor ?? "";
        set
        {
            ValueColor = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
        }
    }
}

/// <summary>A user-defined overlay window: which sensors, where, and how it looks.</summary>
public sealed class OverlayProfile : ObservableObject
{
    private string _name = "Overlay";
    private bool _enabled = true;
    private bool _locked = true;
    private double _x = 20, _y = 20;
    private double _width, _height;
    private double _fontSize = 13;
    private string _fontFamily = "Segoe UI";
    private string _valueFontFamily = "";
    private double _valueScale = 1;
    private OverlayWeight _labelWeight = OverlayWeight.Normal;
    private OverlayWeight _valueWeight = OverlayWeight.SemiBold;
    private int _backgroundOpacity = 75;
    private int _opacity = 100;
    private int _cornerRadius = 10;
    private int _padding = 12;
    private int _rowSpacing = 2;
    private int _itemSpacing = 14;
    private int _columns = 2;
    private int _borderThickness;
    private string _backgroundColor = "#101014";
    private string _accentColor = "#FFAA28";
    private string _labelColor = "#E6E6EB";
    private string _valueColor = "#00FF90";
    private string _borderColor = "#FFAA28";
    private string _warmColor = "#FFD23C";
    private string _hotColor = "#FF5046";
    private OverlayLayout _layout = OverlayLayout.Vertical;
    private OverlayTextEffect _textEffect = OverlayTextEffect.Auto;
    private OverlayBarColor _barColor = OverlayBarColor.Value;
    private int _barHeight;
    private bool _showHeaders = true;
    private bool _showUnits = true;
    private bool _valuesRight = true;
    private bool _warnColors = true;
    private bool _clockSeconds;
    private float _warmTemp = 70, _hotTemp = 85, _warmLoad = 80, _hotLoad = 95;
    private int _graphSeconds = 60;
    private int _graphWidth = 60;
    private OverlayAnchor _anchor = OverlayAnchor.Custom;
    private int _edgeMargin = 20;
    private string _monitor = "";
    private ObservableCollection<OverlayItem> _items = new();

    public OverlayProfile() => HookItems(_items);

    /// <summary>Raised for any change (own property, item list, or an item's property). Arg = property name.</summary>
    public event EventHandler<string>? Changed;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool Locked { get => _locked; set => Set(ref _locked, value); }
    public double X { get => _x; set => Set(ref _x, Finite(value, 20)); }
    public double Y { get => _y; set => Set(ref _y, Finite(value, 20)); }
    /// <summary>User-dragged size; 0 = fit to content.</summary>
    public double Width { get => _width; set => Set(ref _width, Math.Clamp(Finite(value, 0), 0, 8000)); }
    public double Height { get => _height; set => Set(ref _height, Math.Clamp(Finite(value, 0), 0, 8000)); }

    // ----- text -----
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(Finite(value, 13), 8, 40)); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value); }
    /// <summary>Font for the numbers; "" = same as <see cref="FontFamily"/>.</summary>
    public string ValueFontFamily { get => _valueFontFamily; set => Set(ref _valueFontFamily, value?.Trim() ?? ""); }
    /// <summary>Values relative to label size (1 = same size).</summary>
    public double ValueScale { get => _valueScale; set => Set(ref _valueScale, Math.Round(Math.Clamp(Finite(value, 1), 0.7, 3), 2)); }
    public OverlayWeight LabelWeight { get => _labelWeight; set => Set(ref _labelWeight, value); }
    public OverlayWeight ValueWeight { get => _valueWeight; set => Set(ref _valueWeight, value); }
    public OverlayTextEffect TextEffect { get => _textEffect; set => Set(ref _textEffect, value); }
    public bool ShowUnits { get => _showUnits; set => Set(ref _showUnits, value); }

    // ----- colours and shape -----
    public int BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 100)); }
    /// <summary>Opacity of the whole overlay, text included.</summary>
    public int Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 20, 100)); }
    public int CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 24)); }
    public int BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, Math.Clamp(value, 0, 4)); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    /// <summary>Hardware headers (and the unlocked hint).</summary>
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string LabelColor { get => _labelColor; set => Set(ref _labelColor, value); }
    public string ValueColor { get => _valueColor; set => Set(ref _valueColor, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    public string WarmColor { get => _warmColor; set => Set(ref _warmColor, value); }
    public string HotColor { get => _hotColor; set => Set(ref _hotColor, value); }

    // ----- layout -----
    public OverlayLayout Layout { get => _layout; set => Set(ref _layout, value); }
    /// <summary>Tiles per row in the Grid layout.</summary>
    public int Columns { get => _columns; set => Set(ref _columns, Math.Clamp(value, 1, 6)); }
    public int Padding { get => _padding; set => Set(ref _padding, Math.Clamp(value, 0, 32)); }
    /// <summary>Extra space between rows (vertical) in px.</summary>
    public int RowSpacing { get => _rowSpacing; set => Set(ref _rowSpacing, Math.Clamp(value, 0, 16)); }
    /// <summary>Space between items (one line / grid) in px.</summary>
    public int ItemSpacing { get => _itemSpacing; set => Set(ref _itemSpacing, Math.Clamp(value, 0, 40)); }
    public bool ShowHeaders { get => _showHeaders; set => Set(ref _showHeaders, value); }
    /// <summary>Values right-aligned in a column (vertical layout).</summary>
    public bool ValuesRight { get => _valuesRight; set => Set(ref _valuesRight, value); }
    /// <summary>Bar thickness in px; 0 = a quarter of the font size.</summary>
    public int BarHeight { get => _barHeight; set => Set(ref _barHeight, Math.Clamp(value, 0, 12)); }
    public OverlayBarColor BarColor { get => _barColor; set => Set(ref _barColor, value); }
    public int GraphSeconds { get => _graphSeconds; set => Set(ref _graphSeconds, Math.Clamp(value, 15, 600)); }
    public int GraphWidth { get => _graphWidth; set => Set(ref _graphWidth, Math.Clamp(value, 30, 200)); }

    // ----- warnings -----
    public bool WarnColors { get => _warnColors; set => Set(ref _warnColors, value); }
    /// <summary>Warning limits in °C (always Celsius internally) and %.</summary>
    public float WarmTemp { get => _warmTemp; set => Set(ref _warmTemp, ClampF(value, 0, 150, 70)); }
    public float HotTemp { get => _hotTemp; set => Set(ref _hotTemp, ClampF(value, 0, 150, 85)); }
    public float WarmLoad { get => _warmLoad; set => Set(ref _warmLoad, ClampF(value, 0, 100, 80)); }
    public float HotLoad { get => _hotLoad; set => Set(ref _hotLoad, ClampF(value, 0, 100, 95)); }
    /// <summary>Time zone clocks show seconds too.</summary>
    public bool ClockSeconds { get => _clockSeconds; set => Set(ref _clockSeconds, value); }

    // ----- position -----
    public OverlayAnchor Anchor { get => _anchor; set => Set(ref _anchor, Enum.IsDefined(value) ? value : OverlayAnchor.Custom); }
    /// <summary>Distance from the screen edge when anchored, in px.</summary>
    public int EdgeMargin { get => _edgeMargin; set => Set(ref _edgeMargin, Math.Clamp(value, 0, 400)); }
    /// <summary>Monitor device name (e.g. \\.\DISPLAY2) for anchoring; "" = the primary monitor.</summary>
    public string Monitor { get => _monitor; set => Set(ref _monitor, value ?? ""); }

    public ObservableCollection<OverlayItem> Items
    {
        get => _items;
        set
        {
            UnhookItems(_items);
            _items = value ?? new();
            HookItems(_items);
            OnPropertyChanged();
        }
    }

    [JsonIgnore] public string Summary => Items.Count == 1 ? "1 sensor" : $"{Items.Count} sensors";

    /// <summary>Warning limits (raw units) for an item: its own, else the overlay's for temperature/load; null = never warns.</summary>
    public (float Warm, float Hot)? Limits(OverlayItem item, LibreHardwareMonitor.Hardware.SensorType type)
    {
        if (item.WarnAt is not null || item.HotAt is not null)
            return (item.WarnAt ?? float.MaxValue, item.HotAt ?? float.MaxValue);
        return type switch
        {
            LibreHardwareMonitor.Hardware.SensorType.Temperature => (WarmTemp, HotTemp),
            LibreHardwareMonitor.Hardware.SensorType.Load => (WarmLoad, HotLoad),
            _ => null,
        };
    }

    public bool ContainsSensor(string id) => Items.Any(i => i.SensorId == id);

    /// <summary>A full copy (every setting, all items) with a new id and name, placed a little further down-right.</summary>
    public OverlayProfile Clone(string newName)
    {
        var copy = JsonSerializer.Deserialize<OverlayProfile>(JsonSerializer.Serialize(this, AppSettings.Json), AppSettings.Json)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = newName;
        copy.X = X + 30;
        copy.Y = Y + 30;
        for (int i = 0; i < copy.Items.Count && i < Items.Count; i++)
        {
            copy.Items[i].SensorName = Items[i].SensorName;
            copy.Items[i].HardwareName = Items[i].HardwareName;
        }
        return copy;
    }

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
    private static float ClampF(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    protected override void OnPropertyChanged(string? name = null)
    {
        base.OnPropertyChanged(name);
        Changed?.Invoke(this, name ?? "");
    }

    // A hand-edited or imported file can contain null items: skip them here (the importer drops them)
    private void HookItems(ObservableCollection<OverlayItem> items)
    {
        items.CollectionChanged += OnItemsChanged;
        foreach (var i in items) if (i != null) i.PropertyChanged += OnItemPropertyChanged;
    }

    private void UnhookItems(ObservableCollection<OverlayItem> items)
    {
        items.CollectionChanged -= OnItemsChanged;
        foreach (var i in items) if (i != null) i.PropertyChanged -= OnItemPropertyChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (OverlayItem i in e.OldItems) i.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null) foreach (OverlayItem i in e.NewItems) i.PropertyChanged += OnItemPropertyChanged;
        base.OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, nameof(Items));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Name resolution and folding the editor row aren't user changes to the overlay
        if (e.PropertyName is nameof(OverlayItem.SensorName) or nameof(OverlayItem.HardwareName) or nameof(OverlayItem.IsExpanded)
            or nameof(OverlayItem.ValueColorText)) return;
        Changed?.Invoke(this, nameof(Items));
    }
}

/// <summary>
/// Non-sensor things an overlay can show (clock, date, other time zones, …).
/// Stored as items with an "extra:" id; time zones are "extra:tz:&lt;Windows time zone id&gt;".
/// </summary>
public static class OverlayExtras
{
    public const string Group = "Clock & system";
    public const string ZoneGroup = "Time zones";
    private const string ZonePrefix = "extra:tz:";

    public static readonly (string Id, string Name)[] All =
    {
        ("extra:time", "Time"),
        ("extra:timesec", "Time (seconds)"),
        ("extra:date", "Date"),
        ("extra:day", "Weekday"),
        ("extra:uptime", "Uptime"),
    };

    /// <summary>Default overlay label, or null if the id isn't an extra.</summary>
    public static string? NameOf(string id) =>
        id.StartsWith(ZonePrefix) ? id[ZonePrefix.Length..].Replace(" Standard Time", "")
        : All.FirstOrDefault(e => e.Id == id).Name;

    public static string GroupOf(string id) => id.StartsWith(ZonePrefix) ? ZoneGroup : Group;

    public static string ZoneId(string windowsZoneId) => ZonePrefix + windowsZoneId;

    public static string Text(string id, bool zoneSeconds = false)
    {
        var now = DateTime.Now;
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (id.StartsWith(ZonePrefix))
        {
            try
            {
                return TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(id[ZonePrefix.Length..])).ToString(zoneSeconds ? "T" : "t");
            }
            catch (TimeZoneNotFoundException)
            {
                return "-";
            }
        }
        return id switch
        {
            "extra:time" => now.ToString("t"),
            "extra:timesec" => now.ToString("T"),
            "extra:date" => now.ToString("d"),
            "extra:day" => now.ToString("dddd"),
            "extra:uptime" => up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m",
            _ => "-",
        };
    }

    /// <summary>Stand-in sensors so the sensor picker can list the clock extras (time zones are added by place name instead).</summary>
    public static List<SensorVm> PickerItems() => All.Select(e => new SensorVm(
        new SensorReading(e.Id, "extra", Group, LibreHardwareMonitor.Hardware.HardwareType.Motherboard, e.Name,
                          LibreHardwareMonitor.Hardware.SensorType.Factor, null, null, null), Group)).ToList();
}
