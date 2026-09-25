using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One tile on the Categories page.</summary>
public sealed class CategoryVm : ObservableObject
{
    private int _count;
    private string _headline = "";
    private string _detail = "";

    public CategoryVm(SensorCategories.Info info, bool isType)
    {
        Name = info.Name;
        Icon = info.Icon;
        IsType = isType;
    }

    public string Name { get; }
    public string Icon { get; }
    public bool IsType { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => Count == 1 ? "1 sensor" : $"{Count} sensors";
    public string Headline { get => _headline; set => Set(ref _headline, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>Tiles for each hardware and sensor-type category; clicking one filters the Sensors page.</summary>
public partial class CategoriesPage : UserControl
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<CategoryVm> _hardware = new();
    private readonly ObservableCollection<CategoryVm> _types = new();

    /// <summary>(hardware category, sensor-type category) – one of them is null.</summary>
    public event Action<string?, string?>? CategoryChosen;

    public CategoriesPage()
    {
        InitializeComponent();
        HardwareTiles.ItemsSource = _hardware;
        TypeTiles.ItemsSource = _types;

        _app.Sensors.Updated += () =>
        {
            if (IsVisible || _hardware.Count == 0) Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
        };
    }

    private void Refresh()
    {
        var all = _app.Sensors.All;
        LoadingText.Visibility = _app.Sensors.Loaded ? Visibility.Collapsed : Visibility.Visible;

        Sync(_hardware, SensorCategories.Hardware, isType: false, all.ToLookup(s => s.Category));
        Sync(_types, SensorCategories.Types, isType: true, all.ToLookup(s => s.TypeCategory));
    }

    private static void Sync(ObservableCollection<CategoryVm> tiles, SensorCategories.Info[] order, bool isType,
                             ILookup<string, SensorVm> sensors)
    {
        var present = order.Where(i => sensors[i.Name].Any()).ToList();
        if (!tiles.Select(t => t.Name).SequenceEqual(present.Select(p => p.Name)))
        {
            tiles.Clear();
            foreach (var info in present) tiles.Add(new CategoryVm(info, isType));
        }

        foreach (var tile in tiles)
        {
            var list = sensors[tile.Name].ToList();
            tile.Count = list.Count;
            if (isType) DescribeType(tile, list);
            else DescribeHardware(tile, list);
        }
    }

    // ---------- tile text ----------

    private static void DescribeHardware(CategoryVm tile, List<SensorVm> list)
    {
        var parts = new List<string>();
        // Prefer physical RAM over Windows' virtual memory (commit charge) for the headline
        var physical = list.Where(s => !s.HardwareName.Contains("Virtual")).ToList();
        if (physical.Count > 0 && physical.Count < list.Count) list = physical;

        if (tile.Name == "Network")
        {
            var down = Prefer(list, SensorType.Throughput, "Download");
            var up = Prefer(list, SensorType.Throughput, "Upload");
            if (down != null) parts.Add("↓ " + down.ValueText);
            if (up != null) parts.Add("↑ " + up.ValueText);
        }
        else
        {
            var temp = Prefer(list, SensorType.Temperature, "Package", "Tctl", "Core", "");
            var load = Prefer(list, SensorType.Load, "Total", "Core", "Memory", "Used", "");
            if (temp != null) parts.Add(temp.ValueText);
            if (load != null) parts.Add(load.ValueText);
        }
        if (parts.Count == 0 && list.FirstOrDefault(s => s.Value > 0) is { } any) parts.Add(any.ValueText);

        tile.Headline = parts.Count > 0 ? string.Join("  ·  ", parts) : "–";
        tile.Detail = string.Join(", ", list.Select(s => s.GroupName).Distinct());
    }

    private static void DescribeType(CategoryVm tile, List<SensorVm> list)
    {
        var top = list.Where(s => s.Value is > 0).OrderByDescending(s => s.Value).FirstOrDefault();
        string prefix = tile.Name switch
        {
            "Temperatures" => "Hottest",
            "Load" => "Busiest",
            "Clocks" => "Fastest",
            "Fans" => "Fastest",
            _ => "Highest",
        };
        int devices = list.Select(s => s.HardwareId).Distinct().Count();

        tile.Headline = top != null ? $"{prefix} {top.ValueText}" : "–";
        tile.Detail = top != null
            ? $"{top.Name} · {top.GroupName}"
            : devices == 1 ? "on 1 device" : $"on {devices} devices";
    }

    private static SensorVm? Prefer(List<SensorVm> list, SensorType type, params string[] nameHints)
    {
        foreach (var hint in nameHints)
        {
            var hit = list.FirstOrDefault(s => s.Type == type && s.Value is > 0 && s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryVm tile) return;
        if (tile.IsType) CategoryChosen?.Invoke(null, tile.Name);
        else CategoryChosen?.Invoke(tile.Name, null);
    }
}
