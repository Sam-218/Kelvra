using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Checks alert rules against live sensor values and raises notifications.</summary>
public sealed class AlertService
{
    private readonly Dictionary<string, DateTime> _conditionSince = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();

    /// <summary>Most recent alerts first (kept to 50).</summary>
    public ObservableCollection<AlertEvent> Recent { get; } = new();

    public event Action<AlertEvent>? Fired;

    public void Evaluate(AppSettings settings, SensorStore store)
    {
        var now = DateTime.Now;
        foreach (var rule in settings.Alerts)
        {
            if (!store.ById.TryGetValue(rule.SensorId, out var sensor))
            {
                if (store.Loaded) rule.CurrentText = "Sensor not found";
                rule.IsActive = false;
                continue;
            }

            rule.SensorName = sensor.OverlayLabel;
            rule.HardwareName = sensor.GroupName;
            rule.SensorType = sensor.Type;
            rule.CurrentText = sensor.ValueText;

            bool hit = rule.Enabled && sensor.Value is float v && !float.IsNaN(v) &&
                       (rule.Condition == AlertCondition.Above ? v > rule.Threshold : v < rule.Threshold);
            rule.IsActive = hit;

            if (!hit)
            {
                _conditionSince.Remove(rule.Id);
                continue;
            }

            if (!_conditionSince.TryGetValue(rule.Id, out var since)) _conditionSince[rule.Id] = since = now;
            if ((now - since).TotalSeconds < rule.DurationSeconds) continue;
            if (_lastFired.TryGetValue(rule.Id, out var last) && (now - last).TotalMinutes < Math.Max(rule.CooldownMinutes, 0.1)) continue;

            _lastFired[rule.Id] = now;
            rule.LastFiredText = $"Last triggered {now:HH:mm:ss}";
            Fire(rule, sensor, now);
        }
    }

    private void Fire(AlertRule rule, SensorVm sensor, DateTime now)
    {
        string limit = SensorFormat.Format(sensor.Type, rule.Threshold);
        string word = rule.Condition == AlertCondition.Above ? "above" : "below";
        var e = new AlertEvent(now,
            $"{sensor.OverlayLabel} is {sensor.ValueText}",
            $"{sensor.GroupName}: {sensor.OverlayLabel} has been {word} {limit} for {rule.DurationSeconds}s.");

        Recent.Insert(0, e);
        while (Recent.Count > 50) Recent.RemoveAt(Recent.Count - 1);
        Fired?.Invoke(e);
    }

    /// <summary>A sensible starting threshold for a new rule.</summary>
    public static float DefaultThreshold(SensorVm sensor) => sensor.Type switch
    {
        SensorType.Temperature => 85,
        SensorType.Load => 95,
        SensorType.Level => sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase) ? 90 : 10,
        _ => sensor.Max is float m && m > 0 ? MathF.Round(m) : 0,
    };
}
