using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Kelvra;

public enum OverlayLayout { Vertical, Horizontal }

/// <summary>One sensor shown on an overlay.</summary>
public sealed class OverlayItem : ObservableObject
{
    private string _sensorId = "";
    private string? _label;
    private bool _showBar;
    private string _sensorName = "";
    private string _hardwareName = "";

    public string SensorId { get => _sensorId; set => Set(ref _sensorId, value); }

    /// <summary>Optional user label; falls back to the sensor's own name.</summary>
    public string? Label { get => _label; set => Set(ref _label, string.IsNullOrWhiteSpace(value) ? null : value); }

    public bool ShowBar { get => _showBar; set => Set(ref _showBar, value); }

    // Resolved at runtime for the editor list
    [JsonIgnore] public string SensorName { get => _sensorName; set => Set(ref _sensorName, value); }
    [JsonIgnore] public string HardwareName { get => _hardwareName; set => Set(ref _hardwareName, value); }
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
    private int _backgroundOpacity = 75;
    private int _cornerRadius = 10;
    private string _backgroundColor = "#101014";
    private string _accentColor = "#FFAA28";
    private string _labelColor = "#E6E6EB";
    private string _valueColor = "#00FF90";
    private OverlayLayout _layout = OverlayLayout.Vertical;
    private bool _showHeaders = true;
    private bool _warnColors = true;
    private bool _clockSeconds;
    private ObservableCollection<OverlayItem> _items = new();

    public OverlayProfile() => HookItems(_items);

    /// <summary>Raised for any change (own property, item list, or an item's property). Arg = property name.</summary>
    public event EventHandler<string>? Changed;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool Locked { get => _locked; set => Set(ref _locked, value); }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    /// <summary>User-dragged size; 0 = fit to content.</summary>
    public double Width { get => _width; set => Set(ref _width, Math.Max(0, value)); }
    public double Height { get => _height; set => Set(ref _height, Math.Max(0, value)); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 8, 32)); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public int BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 100)); }
    public int CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 24)); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    public string LabelColor { get => _labelColor; set => Set(ref _labelColor, value); }
    public string ValueColor { get => _valueColor; set => Set(ref _valueColor, value); }
    public OverlayLayout Layout { get => _layout; set => Set(ref _layout, value); }
    public bool ShowHeaders { get => _showHeaders; set => Set(ref _showHeaders, value); }
    public bool WarnColors { get => _warnColors; set => Set(ref _warnColors, value); }
    /// <summary>Time zone clocks show seconds too.</summary>
    public bool ClockSeconds { get => _clockSeconds; set => Set(ref _clockSeconds, value); }

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

    public bool ContainsSensor(string id) => Items.Any(i => i.SensorId == id);

    public OverlayProfile Clone(string newName)
    {
        return new OverlayProfile
        {
            Name = newName,
            Enabled = Enabled,
            Locked = Locked,
            X = X + 30,
            Y = Y + 30,
            Width = Width,
            Height = Height,
            FontSize = FontSize,
            FontFamily = FontFamily,
            BackgroundOpacity = BackgroundOpacity,
            CornerRadius = CornerRadius,
            BackgroundColor = BackgroundColor,
            AccentColor = AccentColor,
            LabelColor = LabelColor,
            ValueColor = ValueColor,
            Layout = Layout,
            ShowHeaders = ShowHeaders,
            WarnColors = WarnColors,
            ClockSeconds = ClockSeconds,
            Items = new ObservableCollection<OverlayItem>(Items.Select(i => new OverlayItem
            {
                SensorId = i.SensorId, Label = i.Label, ShowBar = i.ShowBar,
                SensorName = i.SensorName, HardwareName = i.HardwareName,
            })),
        };
    }

    protected override void OnPropertyChanged(string? name = null)
    {
        base.OnPropertyChanged(name);
        Changed?.Invoke(this, name ?? "");
    }

    private void HookItems(ObservableCollection<OverlayItem> items)
    {
        items.CollectionChanged += OnItemsChanged;
        foreach (var i in items) i.PropertyChanged += OnItemPropertyChanged;
    }

    private void UnhookItems(ObservableCollection<OverlayItem> items)
    {
        items.CollectionChanged -= OnItemsChanged;
        foreach (var i in items) i.PropertyChanged -= OnItemPropertyChanged;
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
        // Name resolution isn't a user change
        if (e.PropertyName is nameof(OverlayItem.SensorName) or nameof(OverlayItem.HardwareName)) return;
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
