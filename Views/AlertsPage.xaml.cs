using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Create and manage alert rules; shows the most recent alerts.</summary>
public partial class AlertsPage : UserControl
{
    private readonly App _app = App.Current;

    public AlertsPage()
    {
        InitializeComponent();
        RuleList.ItemsSource = _app.Settings.Alerts;
        RecentList.ItemsSource = _app.Alerts.Recent;
        SoundSwitch.IsChecked = _app.Settings.AlertSound;

        _app.Settings.Alerts.CollectionChanged += (_, _) => UpdateEmpty();
        _app.Alerts.Recent.CollectionChanged += (_, _) =>
            NoRecent.Visibility = _app.Alerts.Recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) BuildPresets();
        };
        UpdateEmpty();
    }

    private void UpdateEmpty() =>
        EmptyState.Visibility = _app.Settings.Alerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- creating ----------

    private void NewAlert_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SensorPickerWindow(_app.Sensors.All, "New alert – pick one or more sensors", _ => false, editable: false)
        {
            Owner = Window.GetWindow(this),
        };
        if (picker.ShowDialog() != true) return;
        foreach (var id in picker.SelectedIds)
            if (_app.Sensors.ById.TryGetValue(id, out var sensor)) AddRule(sensor, AlertService.DefaultThreshold(sensor), AlertCondition.Above);
    }

    private void AddRule(SensorVm sensor, float threshold, AlertCondition condition)
    {
        _app.Settings.Alerts.Add(new AlertRule
        {
            SensorId = sensor.Id,
            Condition = condition,
            Threshold = threshold,
            SensorName = sensor.OverlayLabel,
            HardwareName = sensor.GroupName,
            SensorType = sensor.Type,
            CurrentText = sensor.ValueText,
        });
    }

    /// <summary>One-click starters based on the hardware that was actually detected.</summary>
    private void BuildPresets()
    {
        Presets.Children.Clear();
        var all = _app.Sensors.All;

        void Preset(string label, SensorVm? sensor, float threshold)
        {
            if (sensor == null) return;
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6) };
            b.Click += (_, _) => AddRule(sensor, threshold, AlertCondition.Above);
            Presets.Children.Add(b);
        }

        string deg = SensorFormat.UseFahrenheit ? "185 °F" : "85 °C";
        Preset($"CPU hotter than {deg}", all.FirstOrDefault(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature &&
                                                                (s.Name.Contains("Package") || s.Name.Contains("Tctl"))), 85);
        Preset($"GPU hotter than {deg}", all.FirstOrDefault(s => s.IsGpu && s.Type == SensorType.Temperature && s.Name.Contains("Core")), 85);
        Preset("RAM more than 90 % used", all.FirstOrDefault(s => s.HardwareType == HardwareType.Memory && !s.HardwareName.Contains("Virtual") &&
                                                                  s.Type == SensorType.Load), 90);
        foreach (var drive in all.Where(s => s.HardwareType == HardwareType.Storage && s.Type == SensorType.Load && s.Name.Contains("Used Space")))
            Preset($"{drive.GroupName} more than 90 % full", drive, 90);
    }

    // ---------- rule actions ----------

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlertRule rule) _app.Settings.Alerts.Remove(rule);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlertRule rule) return;
        _app.Tray.Notify($"Test: {rule.SensorName}", $"This is how the alert for {rule.SensorName} ({rule.HardwareName}) will look.");
        if (_app.Settings.AlertSound) System.Media.SystemSounds.Exclamation.Play();
    }

    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        _app.Settings.AlertSound = SoundSwitch.IsChecked == true;
        _app.MarkDirty();
    }
}
