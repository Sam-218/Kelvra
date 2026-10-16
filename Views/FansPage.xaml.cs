using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One fan output in the Fans page list.</summary>
public sealed class FanRow : ObservableObject
{
    private string _modeText = "Automatic (BIOS)";
    private string _liveText = "";
    private string _rpm = "–";
    private string _drift = "";
    private bool _pending;

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
    /// <summary>"RPM · 49 %": the unit under the big number, and the output.</summary>
    public string LiveText { get => _liveText; set => Set(ref _liveText, value); }
    public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
    /// <summary>"9 % slower than 1–4 months ago" when the fan turns slower at the same duty than it used to, else "".</summary>
    public string Drift { get => _drift; set => Set(ref _drift, value); }
    /// <summary>Its settings were changed on the page but not applied yet.</summary>
    public bool Pending { get => _pending; set => Set(ref _pending, value); }
}

/// <summary>
/// Fan control: every controllable fan output with automatic / fixed / curve modes and the safety limits. Changes are
/// made to a draft of each fan's settings and take effect on Apply (Revert goes back to what the fan does now).
/// </summary>
public partial class FansPage : UserControl
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<FanRow> _rows = new();
    private readonly Dictionary<string, FanProfile> _drafts = new(); // by fan output, kept while switching fans
    private FanRow? _row;
    private FanProfile? _profile; // the live settings the controller uses
    private FanProfile? _draft;   // what the page edits
    private int _builtForCount = -1;
    private bool _syncing;

    public FansPage()
    {
        InitializeComponent();
        FanList.ItemsSource = _rows;
        Curve.Edited += UpdatePending;
        foreach (var (name, _) in FanCurve.Presets)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = name, Tag = name, GroupName = "FanPreset", Margin = new Thickness(0, 0, 8, 6) };
            chip.Click += Preset_Click;
            PresetChips.Children.Add(chip);
        }
        _app.Sensors.Updated += OnUpdated;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            OnUpdated();
            _ = LoadDriftAsync();
        };
    }

    /// <summary>Marks fans that turn clearly slower at the same duty than 1–4 months ago (from the history, see <see cref="FanDrift"/>).</summary>
    private async Task LoadDriftAsync()
    {
        if (_app.Recorder is not { } recorder || !_app.Settings.LongHistory) return;
        Dictionary<string, FanSlowdown> drift;
        try
        {
            drift = await recorder.ReadAsync(db =>
            {
                var keys = db.Sensors().ToDictionary(s => s.Id, s => s.Key);
                var found = FanDrift.Find(db.FanDuty(HistoryDb.DayOf(DateTime.Today.AddDays(-130)), HistoryDb.DayOf(DateTime.Today.AddDays(1))), DateTime.Today);
                return found.Where(f => keys.ContainsKey(f.Fan)).GroupBy(f => keys[f.Fan]).ToDictionary(g => g.Key, g => g.MaxBy(f => f.Percent)!);
            });
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.Error("Reading fan speeds", ex);
            return;
        }
        foreach (var row in _rows)
            row.Drift = row.Fan != null && drift.TryGetValue(row.Fan.Id, out var f) ? $"{f.Percent:0} % slower than 1–4 months ago" : "";
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
            row.ModeText = profile?.Mode switch
            {
                FanMode.Fixed => $"Fixed · {profile!.FixedPercent:0} %",
                FanMode.Curve => $"Curve · {(_app.Sensors.ById.TryGetValue(profile!.SourceId, out var src) ? src.Name : "no sensor")}",
                _ => row.Control.IsGpu ? "Automatic (driver)" : "Automatic (BIOS)",
            };
            // The speed big when there's a speed sensor, else the output
            row.Rpm = row.Fan?.Value is float rpm ? rpm.ToString("N0") : row.Control.Value is float pct ? $"{pct:0} %" : "–";
            row.LiveText = row.Fan?.Value is not float ? "output" : row.Control.Value is float duty ? $"RPM · {duty:0} %" : "RPM";
        }

        if (_row != null && _draft != null)
        {
            // Where the fan is now, on the curve being edited
            _app.Sensors.ById.TryGetValue(_draft.SourceId, out var source);
            Curve.LiveTemp = source?.Value is float t ? t : double.NaN;
            Curve.LivePercent = _row.Control.Value is float v ? v : double.NaN;
            Curve.InvalidateVisual();
        }
    }

    /// <summary>
    /// Makes the selected fan's curve follow a temperature (the first fan's, when none is selected), as a change to apply;
    /// its mode stays as it was, so nothing starts driving a fan by surprise. False when there's no fan Kelvra can control.
    /// </summary>
    public bool FollowSensor(string sensorId)
    {
        if (_rows.Count == 0 && _app.Sensors.Loaded) Rebuild(_app.Sensors.All);
        if (_draft == null && _rows.Count > 0) FanList.SelectedItem = _rows[0];
        if (_draft == null) return false;
        _draft.SourceId = sensorId;
        return true;
    }

    private void Rebuild(IReadOnlyList<SensorVm> sensors)
    {
        string? selected = _row?.Control.Id;
        _rows.Clear();
        foreach (var control in sensors.Where(s => s.Controllable))
        {
            var fan = sensors.FirstOrDefault(s => s.HardwareId == control.HardwareId && s.Type == SensorType.Fan && s.Name == control.Name);
            _rows.Add(new FanRow(control, fan) { Pending = _drafts.TryGetValue(control.Id, out var d) && IsPending(control.Id, d) });
        }
        NoFansText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FanCount.Text = _rows.Count == 1 ? "1 FAN OUTPUT" : $"{_rows.Count} FAN OUTPUTS";

        string? warning = !SecureFiles.IsElevated
            ? "Kelvra isn't running as administrator, so only some fans (e.g. graphics cards) can be controlled. Motherboard fans need admin rights and the PawnIO driver."
            : _rows.Count == 0
                ? "No controllable fans were found. Many motherboards need the PawnIO driver (Settings → Sensor driver); some laptops and ready-made PCs don't allow software fan control at all."
                : null;
        Banner.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = warning ?? "";

        // Temperature sensors the curve can follow (the box shows each one's reading live)
        // A new list clears the box's selection, which its binding would write back as "no sensor": keep the draft's
        string? source = _draft?.SourceId;
        SourceBox.ItemsSource = sensors.Where(s => s.Type == SensorType.Temperature).ToList();
        if (_draft != null && source != null)
        {
            _draft.SourceId = source;
            SourceBox.SelectedValue = source;
        }

        FanList.SelectedItem = _rows.FirstOrDefault(r => r.Control.Id == selected) ?? _rows.FirstOrDefault();
    }

    private void FanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_draft != null) Unhook(_draft);
        _row = FanList.SelectedItem as FanRow;
        _profile = _row == null ? null : ProfileFor(_row);
        _draft = _row == null || _profile == null ? null : DraftFor(_row.Control.Id, _profile);
        if (_draft != null) Hook(_draft);

        Editor.DataContext = SafetyCard.DataContext = _draft;
        Editor.Visibility = _draft == null ? Visibility.Collapsed : Visibility.Visible;
        FanTitle.Text = _row?.Name ?? "";
        FanTitle.ToolTip = _row?.Device;
        if (_draft != null) SourceBox.SelectedValue = _draft.SourceId;
        UpdateModeParts();
        UpdatePending();
        OnUpdated();
    }

    /// <summary>The fan's live settings, created (on Automatic, so nothing changes yet) the first time it's opened.</summary>
    private FanProfile ProfileFor(FanRow row)
    {
        var profile = _app.Settings.Fans.FirstOrDefault(f => f.ControlId == row.Control.Id);
        if (profile != null) return profile;
        profile = new FanProfile { ControlId = row.Control.Id, SourceId = DefaultSource(row) };
        _app.Settings.Fans.Add(profile);
        _app.MarkDirty();
        return profile;
    }

    private FanProfile DraftFor(string controlId, FanProfile profile)
    {
        if (!_drafts.TryGetValue(controlId, out var draft)) _drafts[controlId] = draft = profile.Draft();
        return draft;
    }

    /// <summary>A GPU fan follows the GPU; anything else follows the CPU.</summary>
    private string DefaultSource(FanRow row)
    {
        var sensors = _app.Sensors.All;
        if (row.Control.IsGpu)
            return Overview.Pick(sensors.Where(s => s.HardwareId == row.Control.HardwareId).ToList(), SensorType.Temperature, "GPU Core")?.Id ?? "";
        return Overview.CpuTemperature(sensors.Where(Overview.IsCpu).ToList())?.Id ?? "";
    }

    // ---------- draft, apply, revert ----------

    private void Hook(FanProfile draft)
    {
        draft.PropertyChanged += OnDraftChanged;
        draft.Points.CollectionChanged += OnDraftPointsChanged;
    }

    private void Unhook(FanProfile draft)
    {
        draft.PropertyChanged -= OnDraftChanged;
        draft.Points.CollectionChanged -= OnDraftPointsChanged;
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanProfile.Status)) return;
        if (e.PropertyName == nameof(FanProfile.Points) && sender is FanProfile d)
        {
            // a preset or a revert replaced the list: listen to the new one
            d.Points.CollectionChanged -= OnDraftPointsChanged;
            d.Points.CollectionChanged += OnDraftPointsChanged;
        }
        if (e.PropertyName is nameof(FanProfile.Mode) or nameof(FanProfile.Points) or nameof(FanProfile.Hysteresis) or nameof(FanProfile.RampDown))
            UpdateModeParts();
        UpdatePending();
    }

    private void OnDraftPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdatePending();

    private bool IsPending(string controlId, FanProfile draft) =>
        _app.Settings.Fans.FirstOrDefault(f => f.ControlId == controlId) is { } live && !draft.SameAs(live);

    private void UpdatePending()
    {
        bool pending = _draft != null && _profile != null && !_draft.SameAs(_profile);
        ApplyButton.IsEnabled = RevertButton.IsEnabled = pending;
        if (_row != null) _row.Pending = pending;
        ApplyText.Text = _draft?.Mode switch
        {
            FanMode.Curve => "Apply curve",
            FanMode.Fixed => "Apply speed",
            _ => "Hand back to BIOS",
        };
        SyncPresetChips();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || _profile == null) return;
        _profile.CopyFrom(_draft);
        _app.MarkDirty();
        UpdatePending();
        OnUpdated();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || _profile == null) return;
        _draft.CopyFrom(_profile);
        SourceBox.SelectedValue = _draft.SourceId;
        UpdatePending();
    }

    // ---------- the parts that depend on the mode ----------

    private void UpdateModeParts()
    {
        bool visible = _draft is { Mode: not FanMode.Auto };
        SafetyCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(Editor, visible ? 1 : 3); // on Automatic there's nothing to set: the editor takes the room
        SyncSlowDown();
        SyncPresetChips();
    }

    private void SyncPresetChips()
    {
        string? preset = _draft == null ? null : FanCurve.PresetOf(_draft.Points);
        foreach (var chip in PresetChips.Children.OfType<RadioButton>()) chip.IsChecked = (string)chip.Tag == preset;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || sender is not RadioButton { Tag: string name }) return;
        var points = FanCurve.Presets.First(p => p.Name == name).Points;
        _draft.Points = new ObservableCollection<FanPoint>(points.Select(p => new FanPoint { Temp = p.Temp, Percent = p.Percent }));
    }

    /// <summary>The "slow down gently" choices, plus the profile's own values when they're none of them.</summary>
    private void SyncSlowDown()
    {
        if (_draft == null) return;
        _syncing = true;
        SlowDownBox.Items.Clear();
        foreach (var s in FanCurve.SlowDowns) SlowDownBox.Items.Add(new ComboBoxItem { Content = s.Label, Tag = (s.Hysteresis, s.RampDown) });
        string label = FanCurve.SlowDownLabel(_draft.Hysteresis, _draft.RampDown);
        var match = SlowDownBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == label);
        if (match == null)
        {
            match = new ComboBoxItem { Content = label, Tag = (_draft.Hysteresis, _draft.RampDown) };
            SlowDownBox.Items.Add(match);
        }
        SlowDownBox.SelectedItem = match;
        _syncing = false;
    }

    private void SlowDown_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _draft == null || SlowDownBox.SelectedItem is not ComboBoxItem { Tag: (float hysteresis, float rampDown) }) return;
        (_draft.Hysteresis, _draft.RampDown) = (hysteresis, rampDown);
    }

    // ---------- the selected point ----------

    private void Point_LostFocus(object sender, RoutedEventArgs e) => UpdatePending();

    private void PreviousPoint_Click(object sender, RoutedEventArgs e) => StepPoint(-1);
    private void NextPoint_Click(object sender, RoutedEventArgs e) => StepPoint(+1);

    private void StepPoint(int delta)
    {
        if (_draft is not { Points.Count: > 0 }) return;
        var sorted = _draft.Points.OrderBy(p => p.Temp).ToList();
        int i = Curve.SelectedPoint is { } current ? sorted.IndexOf(current) : -1;
        Curve.SelectedPoint = sorted[Math.Clamp(i + delta, 0, sorted.Count - 1)];
    }
}
