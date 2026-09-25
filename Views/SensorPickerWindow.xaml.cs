using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Kelvra;

public sealed class PickItem
{
    public PickItem(SensorVm sensor, bool alreadyAdded, bool editable)
    {
        Sensor = sensor;
        IsEnabled = editable || !alreadyAdded;
        IsChecked = alreadyAdded;
    }

    public SensorVm Sensor { get; }
    public bool IsEnabled { get; }
    public bool IsChecked { get; set; }
    public string GroupName => Sensor.GroupName;
    public string Label => IsEnabled ? Sensor.Name : $"{Sensor.Name}  (already added)";
}

public partial class SensorPickerWindow : Window
{
    private readonly List<PickItem> _items;
    private readonly ICollectionView _view;

    private readonly bool _editable;

    public SensorPickerWindow(IEnumerable<SensorVm> sensors, OverlayProfile target)
        : this(sensors, $"Add sensors to {target.Name}", target.ContainsSensor, editable: false)
    {
    }

    /// <param name="included">Sensors that are already part of the target.</param>
    /// <param name="editable">
    /// true: included sensors start ticked and can be unticked; <see cref="SelectedIds"/> is the full new selection.
    /// false: included sensors are locked; <see cref="SelectedIds"/> only contains newly ticked ones.
    /// </param>
    public SensorPickerWindow(IEnumerable<SensorVm> sensors, string heading, Func<string, bool> included, bool editable)
    {
        InitializeComponent();
        Heading.Text = heading;
        _editable = editable;

        _items = sensors.Select(s => new PickItem(s, included(s.Id), editable)).ToList();
        _view = new ListCollectionView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickItem.GroupName)));
        _view.Filter = o =>
        {
            string q = SearchBox.Text.Trim();
            if (q.Length == 0) return true;
            var p = (PickItem)o;
            return p.Sensor.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || p.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || p.Sensor.TypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        List.ItemsSource = _view;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => SearchBox.Focus();
        UpdateCount();
    }

    public List<string> SelectedIds { get; } = new();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        int n = _items.Count(i => i.IsEnabled && i.IsChecked);
        CountText.Text = n == 0 ? "Nothing selected" : $"{n} selected";
        AddButton.IsEnabled = _editable || n > 0;
        AddButton.Content = _editable ? "Done" : n > 0 ? $"Add {n}" : "Add";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedIds.AddRange(_items.Where(i => i.IsEnabled && i.IsChecked).Select(i => i.Sensor.Id));
        DialogResult = true;
    }
}
