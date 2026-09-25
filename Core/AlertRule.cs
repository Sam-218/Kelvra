using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

public enum AlertCondition { Above, Below }

/// <summary>"Tell me when &lt;sensor&gt; is above/below &lt;value&gt; for &lt;n&gt; seconds."</summary>
public sealed class AlertRule : ObservableObject
{
    private string _sensorId = "";
    private AlertCondition _condition = AlertCondition.Above;
    private float _threshold;
    private int _durationSeconds = 5;
    private int _cooldownMinutes = 5;
    private bool _enabled = true;

    // runtime-only
    private string _sensorName = "";
    private string _hardwareName = "";
    private SensorType? _sensorType;
    private string _currentText = "–";
    private bool _isActive;
    private string _lastFiredText = "Never triggered";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SensorId { get => _sensorId; set => Set(ref _sensorId, value); }
    public AlertCondition Condition { get => _condition; set => Set(ref _condition, value); }

    /// <summary>Threshold in the sensor's raw unit (°C for temperatures).</summary>
    public float Threshold
    {
        get => _threshold;
        set
        {
            if (Set(ref _threshold, value)) OnPropertyChanged(nameof(ThresholdDisplay));
        }
    }

    public int DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, Math.Clamp(value, 0, 3600)); }
    public int CooldownMinutes { get => _cooldownMinutes; set => Set(ref _cooldownMinutes, Math.Clamp(value, 0, 1440)); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    [JsonIgnore] public string SensorName { get => _sensorName; set => Set(ref _sensorName, value); }
    [JsonIgnore] public string HardwareName { get => _hardwareName; set => Set(ref _hardwareName, value); }

    [JsonIgnore]
    public SensorType? SensorType
    {
        get => _sensorType;
        set
        {
            if (!Set(ref _sensorType, value)) return;
            OnPropertyChanged(nameof(ThresholdDisplay));
            OnPropertyChanged(nameof(UnitText));
        }
    }

    /// <summary>Threshold in the unit shown in the UI (°F when Fahrenheit is on).</summary>
    [JsonIgnore]
    public float ThresholdDisplay
    {
        get => SensorType is { } t ? MathF.Round(SensorFormat.ToDisplay(t, Threshold), 2) : Threshold;
        set => Threshold = SensorType is { } t ? SensorFormat.FromDisplay(t, value) : value;
    }

    [JsonIgnore] public string UnitText => SensorType is { } t ? SensorFormat.Unit(t) : "";
    [JsonIgnore] public string CurrentText { get => _currentText; set => Set(ref _currentText, value); }
    [JsonIgnore] public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    [JsonIgnore] public string LastFiredText { get => _lastFiredText; set => Set(ref _lastFiredText, value); }

    /// <summary>Re-reads unit-dependent text after the °C/°F setting changes.</summary>
    public void RefreshUnits()
    {
        OnPropertyChanged(nameof(ThresholdDisplay));
        OnPropertyChanged(nameof(UnitText));
    }
}

public sealed record AlertEvent(DateTime Time, string Title, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}
