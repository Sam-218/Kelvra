using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One fan output in the Fans page list.</summary>
public sealed class FanRow : ObservableObject
{
    private string _modeText = "AUTOMATIC";
    private string _liveText = "";

    public FanRow(SensorVm control, SensorVm? fan)
    {
        Control = control;
        Fan = fan;
    }

    public SensorVm Control { get; }
    /// <summary>The matching speed sensor (RPM), when there is one with the same name.</summary>
    public SensorVm? Fan { get; }
    public string Name => Control.Name;
    public string Device => Control.GroupName;
    public string ModeText { get => _modeText; set => Set(ref _modeText, value); }
    public string LiveText { get => _liveText; set => Set(ref _liveText, value); }
}

/// <summary>Fan control: every controllable fan output, with automatic / fixed / curve modes and the safety limits.</summary>
public partial class FansPage : UserControl
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<FanRow> _rows = new();
    private FanRow? _row;
    private FanProfile? _profile;
    private int _builtForCount = -1;

    public FansPage()
    {
        InitializeComponent();
        FanList.ItemsSource = _rows;
        Curve.Edited += _app.MarkDirty;
        _app.Sensors.Updated += OnUpdated;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) OnUpdated();
        };
    }

    private void OnUpdated()
    {
        if (!IsVisible || !_app.Sensors.Loaded) return;
        var sensors = _app.Sensors.All;
        if (sensors.Count != _builtForCount)
        {
            _builtForCount = sensors.Count;
            Rebuild(sensors);
        }

        foreach (var row in _rows)
        {
            var profile = _app.Settings.Fans.FirstOrDefault(f => f.ControlId == row.Control.Id);
            row.ModeText = (profile?.Mode ?? FanMode.Auto) switch
            {
                FanMode.Fixed => "FIXED",
                FanMode.Curve => "CURVE",
                _ => "AUTOMATIC",
            };
            row.LiveText = $"{Fmt(row.Fan)} · {Fmt(row.Control)}";
        }

        if (_row != null && _profile != null)
        {
            RpmText.Text = Fmt(_row.Fan);
            OutputText.Text = Fmt(_row.Control);
            _app.Sensors.ById.TryGetValue(_profile.SourceId, out var source);
            Curve.LiveTemp = source?.Value is float t ? t : double.NaN;
            Curve.LivePercent = _row.Control.Value is float v ? v : double.NaN;
            Curve.InvalidateVisual();
        }
    }

    private static string Fmt(SensorVm? s) => s?.ValueText ?? "–";

    private void Rebuild(IReadOnlyList<SensorVm> sensors)
    {
        string? selected = _row?.Control.Id;
        _rows.Clear();
        foreach (var control in sensors.Where(s => s.Controllable))
        {
            var fan = sensors.FirstOrDefault(s => s.HardwareId == control.HardwareId && s.Type == SensorType.Fan && s.Name == control.Name);
            _rows.Add(new FanRow(control, fan));
        }
        NoFansText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        string? warning = !SecureFiles.IsElevated
            ? "Kelvra isn't running as administrator, so only some fans (e.g. graphics cards) can be controlled. Motherboard fans need admin rights and the PawnIO driver."
            : _rows.Count == 0
                ? "No controllable fans were found. Many motherboards need the PawnIO driver (Settings → Sensor driver); some laptops and ready-made PCs don't allow software fan control at all."
                : null;
        Banner.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = warning ?? "";

        // Temperature sensors the curve can follow
        string? source = _profile?.SourceId;
        SourceBox.Items.Clear();
        foreach (var t in sensors.Where(s => s.Type == SensorType.Temperature))
            SourceBox.Items.Add(new ComboBoxItem { Content = $"{t.GroupName} · {t.Name}", Tag = t.Id });
        if (source != null) SourceBox.SelectedValue = source;

        FanList.SelectedItem = _rows.FirstOrDefault(r => r.Control.Id == selected) ?? _rows.FirstOrDefault();
    }

    private void FanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_profile != null) _profile.PropertyChanged -= OnProfileChanged;
        _row = FanList.SelectedItem as FanRow;
        _profile = _row == null ? null : ProfileFor(_row);
        if (_profile != null) _profile.PropertyChanged += OnProfileChanged;

        Editor.DataContext = _profile;
        Editor.Visibility = _profile == null ? Visibility.Collapsed : Visibility.Visible;
        FanTitle.Text = _row == null ? "" : $"{_row.Name} · {_row.Device}";
        if (_profile != null) SourceBox.SelectedValue = _profile.SourceId;
        UpdateSafetyVisibility();
        OnUpdated();
    }

    /// <summary>The fan's settings, created (on Automatic, so nothing changes yet) the first time it's opened.</summary>
    private FanProfile ProfileFor(FanRow row)
    {
        var profile = _app.Settings.Fans.FirstOrDefault(f => f.ControlId == row.Control.Id);
        if (profile != null) return profile;
        profile = new FanProfile { ControlId = row.Control.Id, SourceId = DefaultSource(row) };
        _app.Settings.Fans.Add(profile);
        _app.MarkDirty();
        return profile;
    }

    /// <summary>A GPU fan follows the GPU; anything else follows the CPU.</summary>
    private string DefaultSource(FanRow row)
    {
        var sensors = _app.Sensors.All;
        if (row.Control.IsGpu)
            return Overview.Pick(sensors.Where(s => s.HardwareId == row.Control.HardwareId).ToList(), SensorType.Temperature, "GPU Core")?.Id ?? "";
        return Overview.CpuTemperature(sensors.Where(Overview.IsCpu).ToList())?.Id ?? "";
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanProfile.Status)) return;
        _app.MarkDirty();
        if (e.PropertyName == nameof(FanProfile.Mode)) UpdateSafetyVisibility();
    }

    private void UpdateSafetyVisibility() =>
        SafetyCard.Visibility = _profile is { Mode: not FanMode.Auto } ? Visibility.Visible : Visibility.Collapsed;

    private void Point_LostFocus(object sender, RoutedEventArgs e) => _app.MarkDirty();

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || sender is not Button { Tag: string kind }) return;
        (float, float)[] points = kind switch
        {
            "silent" => new[] { (40f, 20f), (60f, 35f), (75f, 60f), (88f, 100f) },
            "performance" => new[] { (30f, 40f), (50f, 60f), (65f, 85f), (78f, 100f) },
            _ => FanCurve.DefaultPoints().Select(p => (p.Temp, p.Percent)).ToArray(),
        };
        _profile.Points = new ObservableCollection<FanPoint>(points.Select(p => new FanPoint { Temp = p.Item1, Percent = p.Item2 }));
        _app.MarkDirty();
    }
}
