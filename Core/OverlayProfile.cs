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
