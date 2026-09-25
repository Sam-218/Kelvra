using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kelvra;

/// <summary>Sensor graphs: everything, one category, or your own selection. Also starts/stops CSV logging.</summary>
public partial class HistoryPage : UserControl
{
    private static readonly Dictionary<string, Color> CategoryColors = new()
    {
        ["CPU"] = Color.FromRgb(0x3B, 0x82, 0xF6),
        ["GPU"] = Color.FromRgb(0x22, 0xC5, 0x5E),
        ["Memory"] = Color.FromRgb(0xA8, 0x55, 0xF7),
        ["Motherboard"] = Color.FromRgb(0xEA, 0xB3, 0x08),
        ["Storage"] = Color.FromRgb(0xF9, 0x73, 0x16),
        ["Network"] = Color.FromRgb(0x06, 0xB6, 0xD4),
        ["Cooling"] = Color.FromRgb(0x38, 0xBD, 0xF8),
        ["Battery"] = Color.FromRgb(0x84, 0xCC, 0x16),
        ["Power supply"] = Color.FromRgb(0xEF, 0x44, 0x44),
    };

    private readonly App _app = App.Current;
    private readonly ObservableCollection<ChartItem> _items = new();
    private readonly Dictionary<string, ChartItem> _cache = new();
    private int _builtForCount = -1;
    private bool _loading = true;

    public HistoryPage()
    {
        InitializeComponent();
        Charts.ItemsSource = _items;

        var s = _app.Settings;
        foreach (RadioButton r in RangeButtons.Children)
            r.IsChecked = int.Parse((string)r.Tag) == s.HistoryMinutes;
        (s.HistoryMode switch { "All" => ModeAll, "Selected" => ModeSelected, _ => ModeCategory }).IsChecked = true;
        _loading = false;

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Rebuild();
        };
        _app.Sensors.Updated += () =>
        {
            if (IsVisible && _app.Sensors.All.Count != _builtForCount) Rebuild();
        };
        _app.Logger.StateChanged += UpdateRecording;
        UpdateRecording();
    }

    private TimeSpan Range => TimeSpan.FromMinutes(_app.Settings.HistoryMinutes);

    // ---------- building the chart list ----------

    private void Rebuild()
    {
        if (!_app.Sensors.Loaded) return;
        _builtForCount = _app.Sensors.All.Count;
        var s = _app.Settings;
        var selected = new HashSet<string>(s.HistorySelected);

        IEnumerable<SensorVm> sensors = s.HistoryMode switch
        {
            "All" => _app.Sensors.All,
            "Selected" => s.HistorySelected.Select(id => _app.Sensors.ById.GetValueOrDefault(id)).OfType<SensorVm>(),
            _ => InCategory(s.HistoryCategory),
        };

        string q = SearchBox.Text.Trim();
        if (q.Length > 0)
            sensors = sensors.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || x.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || x.TypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase));

        _items.Clear();
        foreach (var sensor in sensors)
        {
            if (!_cache.TryGetValue(sensor.Id, out var item))
                _cache[sensor.Id] = item = new ChartItem(sensor, CategoryColors.GetValueOrDefault(sensor.Category, Color.FromRgb(0x94, 0xA3, 0xB8)));
            item.Window = Range;
            item.IsPinned = selected.Contains(sensor.Id);
            _items.Add(item);
        }

        Charts.ItemTemplate = (DataTemplate)FindResource(s.HistoryMode == "Selected" ? "LargeCard" : "SmallCard");
        CategoryPanel.Visibility = s.HistoryMode == "Category" ? Visibility.Visible : Visibility.Collapsed;
        ChooseButton.Visibility = s.HistoryMode == "Selected" ? Visibility.Visible : Visibility.Collapsed;
        if (s.HistoryMode == "Category") BuildChips();

        EmptyText.Text = s.HistoryMode == "Selected" && s.HistorySelected.Count == 0
            ? "No sensors selected yet.\nClick “Choose sensors”, or the ☆ on any graph in All / Categories."
            : "No graphs match.";
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<SensorVm> InCategory(string key)
    {
        if (key.StartsWith("type:")) return _app.Sensors.All.Where(x => x.TypeCategory == key[5..]);
        string hw = key.StartsWith("hw:") ? key[3..] : key;
        return _app.Sensors.All.Where(x => x.Category == hw);
    }

    private void BuildChips()
    {
        HardwareChips.Children.Clear();
        TypeChips.Children.Clear();
        var all = _app.Sensors.All;
        foreach (var info in SensorCategories.Hardware)
        {
            int n = all.Count(x => x.Category == info.Name);
            if (n > 0) HardwareChips.Children.Add(Chip(info.Name, "hw:" + info.Name, n));
        }
        foreach (var info in SensorCategories.Types)
        {
            int n = all.Count(x => x.TypeCategory == info.Name);
            if (n > 0) TypeChips.Children.Add(Chip(info.Name, "type:" + info.Name, n));
        }
    }

    private RadioButton Chip(string label, string key, int count)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(new TextBlock { Text = count.ToString(), Margin = new Thickness(6, 0, 0, 0), Opacity = 0.6 });
        var chip = new RadioButton
        {
            Style = (Style)FindResource("Chip"),
            GroupName = "HistoryCategory",
            Content = content,
            IsChecked = _app.Settings.HistoryCategory == key,
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, label);
        chip.Checked += (_, _) =>
        {
            if (_app.Settings.HistoryCategory == key) return;
            _app.Settings.HistoryCategory = key;
            _app.MarkDirty();
            Rebuild();
        };
        return chip;
    }

    // ---------- controls ----------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        _app.Settings.HistoryMode = rb == ModeAll ? "All" : rb == ModeSelected ? "Selected" : "Category";
        _app.MarkDirty();
        Rebuild();
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        _app.Settings.HistoryMinutes = int.Parse((string)rb.Tag);
        _app.MarkDirty();
        foreach (var item in _items) item.Window = Range;
        foreach (var chart in FindCharts(Charts)) chart.Refresh();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && IsLoaded) Rebuild();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartItem item) return;
        var list = _app.Settings.HistorySelected;
        if (list.Remove(item.Sensor.Id)) item.IsPinned = false;
        else
        {
            list.Add(item.Sensor.Id);
            item.IsPinned = true;
        }
        _app.MarkDirty();
        if (_app.Settings.HistoryMode == "Selected") Rebuild();
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var selected = new HashSet<string>(_app.Settings.HistorySelected);
        var picker = new SensorPickerWindow(_app.Sensors.All, "Choose sensors to graph", selected.Contains, editable: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (picker.ShowDialog() != true) return;

        // Keep the existing order, append new ones
        var chosen = new HashSet<string>(picker.SelectedIds);
        var list = _app.Settings.HistorySelected.Where(chosen.Contains).ToList();
        list.AddRange(picker.SelectedIds.Where(id => !list.Contains(id)));
        _app.Settings.HistorySelected = list;
        _app.MarkDirty();
        Rebuild();
    }

    // ---------- CSV logging ----------

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        var logger = _app.Logger;
        if (logger.IsRecording)
        {
            logger.Stop();
            return;
        }
        try
        {
            logger.Start(_items.Select(i => i.Sensor), _app.Settings.LogIntervalSeconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Record CSV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateRecording()
    {
        var logger = _app.Logger;
        RecordText.Text = logger.IsRecording ? "Stop recording" : "Record CSV";
        RecordingBar.Visibility = logger.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        if (logger.IsRecording)
        {
            var elapsed = DateTime.Now - logger.Started;
            RecordingText.Text = $"● Recording {logger.ColumnCount} sensors to {Path.GetFileName(logger.FilePath)}  ·  " +
                                 $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}  ·  {logger.Rows:N0} rows";
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        string? file = _app.Logger.FilePath;
        var args = file != null && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{CsvLogger.DefaultFolder}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static IEnumerable<ChartControl> FindCharts(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ChartControl chart) yield return chart;
            foreach (var nested in FindCharts(child)) yield return nested;
        }
    }
}
