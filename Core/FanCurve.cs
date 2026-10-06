using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Kelvra;

/// <summary>Auto = the BIOS/driver controls the fan (Kelvra doesn't touch it). Fixed = one speed. Curve = speed follows a temperature.</summary>
public enum FanMode { Auto, Fixed, Curve }

/// <summary>One point of a fan curve: at <see cref="Temp"/> °C run at <see cref="Percent"/> %.</summary>
public sealed class FanPoint : ObservableObject
{
    private float _temp;
    private float _percent;

    public float Temp { get => _temp; set => Set(ref _temp, FanCurve.Clamp(value, 0, 120, 50)); }
    public float Percent { get => _percent; set => Set(ref _percent, FanCurve.Clamp(value, 0, 100, 50)); }
}

/// <summary>How Kelvra drives one fan output. Stored in settings; temperatures are always °C.</summary>
public sealed class FanProfile : ObservableObject
{
    private FanMode _mode;
    private float _fixedPercent = 50;
    private string _sourceId = "";
    private float _minPercent = 30;
    private float _hysteresis = 3;
    private float _rampDown = 5;
    private float _criticalTemp = 90;
    private ObservableCollection<FanPoint> _points = FanCurve.DefaultPoints();
    private string _status = "";

    /// <summary>The Control sensor this profile drives.</summary>
    public string ControlId { get; set; } = "";
    public FanMode Mode { get => _mode; set => Set(ref _mode, Enum.IsDefined(value) ? value : FanMode.Auto); }
    public float FixedPercent { get => _fixedPercent; set => Set(ref _fixedPercent, FanCurve.Clamp(value, 0, 100, 50)); }
    /// <summary>Temperature sensor the curve follows.</summary>
    public string SourceId { get => _sourceId; set => Set(ref _sourceId, value ?? ""); }
    /// <summary>Never slower than this (Fixed and Curve). Keeps air moving if Kelvra stops unexpectedly.</summary>
    public float MinPercent { get => _minPercent; set => Set(ref _minPercent, FanCurve.Clamp(value, 0, 100, 30)); }
    /// <summary>The temperature must drop this many °C before the fan slows down (stops it pulsing up and down).</summary>
    public float Hysteresis { get => _hysteresis; set => Set(ref _hysteresis, FanCurve.Clamp(value, 0, 15, 3)); }
    /// <summary>Slowing down is limited to this many % per second; speeding up is immediate.</summary>
    public float RampDown { get => _rampDown; set => Set(ref _rampDown, FanCurve.Clamp(value, 1, 100, 5)); }
    /// <summary>At or above this °C (source sensor) the fan runs at 100 % whatever the curve says.</summary>
    public float CriticalTemp { get => _criticalTemp; set => Set(ref _criticalTemp, FanCurve.Clamp(value, 50, 120, 90)); }

    public ObservableCollection<FanPoint> Points
    {
        get => _points;
        set => Set(ref _points, value is { Count: >= 2 } ? value : FanCurve.DefaultPoints());
    }

    /// <summary>What the controller is doing right now ("62 % · CPU 58 °C"), for the page.</summary>
    [JsonIgnore] public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>Curve maths and the defaults, kept free of hardware so they're easy to test.</summary>
public static class FanCurve
{
    public const int MaxPoints = 8;

    public static ObservableCollection<FanPoint> DefaultPoints() => new()
    {
        new FanPoint { Temp = 35, Percent = 30 },
        new FanPoint { Temp = 55, Percent = 40 },
        new FanPoint { Temp = 70, Percent = 65 },
        new FanPoint { Temp = 85, Percent = 100 },
    };

    internal static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>Speed for a temperature: straight lines between the points, flat before the first and after the last.</summary>
    public static float Evaluate(IReadOnlyList<FanPoint> points, float temp)
    {
        if (points.Count == 0) return 100;
        var sorted = points.OrderBy(p => p.Temp).ToList();
        if (temp <= sorted[0].Temp) return sorted[0].Percent;
        for (int i = 1; i < sorted.Count; i++)
        {
            var a = sorted[i - 1];
            var b = sorted[i];
            if (temp > b.Temp) continue;
            float span = b.Temp - a.Temp;
            return span <= 0 ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temp - a.Temp) / span;
        }
        return sorted[^1].Percent;
    }

    /// <summary>Settings from a hand-edited file: points sorted, 2–8 of them, everything in range.</summary>
    public static void Repair(FanProfile p)
    {
        var points = (p.Points ?? DefaultPoints()).Where(x => x != null).OrderBy(x => x.Temp).Take(MaxPoints).ToList();
        p.Points = points.Count >= 2 ? new ObservableCollection<FanPoint>(points) : DefaultPoints();
    }
}

/// <summary>
/// Runs the fan profiles after every sensor poll. Safety rules, in order:
/// 1. Sensor errors, a missing temperature sensor or a profile set to Auto → the fan goes back to BIOS/driver control.
/// 2. At or above the critical temperature → 100 %.
/// 3. Never below the profile's minimum.
/// 4. Speeding up is immediate; slowing down is gradual and only after the temperature fell by the hysteresis.
/// Kelvra hands every fan back to automatic on exit and on crashes (see App).
/// </summary>
public sealed class FanController
{
    private sealed class State
    {
        public float? Applied;   // what we last set (null = automatic)
        public float CurveTemp = float.NaN; // temperature the curve was last evaluated at (for hysteresis)
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly HashSet<string> _warned = new();

    /// <param name="seconds">Time since the previous tick (limits how fast fans slow down).</param>
    /// <param name="apply">Sets a fan (percent) or returns it to automatic (null).</param>
    public void Tick(IEnumerable<FanProfile> profiles, SensorStore store, double seconds, Action<string, float?> apply)
    {
        var active = new HashSet<string>();
        foreach (var p in profiles)
        {
            active.Add(p.ControlId);
            var state = _states.TryGetValue(p.ControlId, out var s) ? s : _states[p.ControlId] = new State();
            float? target = Target(p, store, state, out string status);

            if (target is float wanted && state.Applied is float prev && wanted < prev)
                target = Math.Max(wanted, prev - (float)(p.RampDown * Math.Max(seconds, 0.05))); // gentle slow-down

            bool changed = target is null != state.Applied is null || target is float a && state.Applied is float b && Math.Abs(a - b) >= 0.5f;
            if (changed)
            {
                apply(p.ControlId, target);
                state.Applied = target;
            }
            p.Status = target is float pct ? $"{pct:0} % · {status}" : status;
        }

        // Profiles that were deleted: give their fans back
        foreach (var id in _states.Keys.Where(id => !active.Contains(id)).ToList())
        {
            if (_states[id].Applied != null) apply(id, null);
            _states.Remove(id);
        }
    }

    private float? Target(FanProfile p, SensorStore store, State state, out string status)
    {
        if (p.Mode == FanMode.Auto)
        {
            status = "Automatic (BIOS / driver)";
            return null;
        }
        if (store.Error != null)
        {
            status = "Sensor error – handed back to automatic";
            return null;
        }
        if (!store.ById.TryGetValue(p.ControlId, out var control) || !control.Controllable)
        {
            status = "This fan can't be controlled right now (admin rights and the PawnIO driver are needed)";
            return null;
        }

        store.ById.TryGetValue(p.SourceId, out var source);
        float? temp = source?.Value is float v && !float.IsNaN(v) ? v : null;

        if (p.Mode == FanMode.Fixed)
        {
            status = temp is float ft ? $"fixed · {source!.Name} {SensorFormat.Format(source.Type, ft)}" : "fixed";
            if (temp >= p.CriticalTemp) return 100;
            return Math.Max(p.FixedPercent, p.MinPercent);
        }

        // Curve
        if (temp is not float t)
        {
            if (_warned.Add(p.ControlId)) Log.Warn($"Fan {p.ControlId}: temperature sensor {p.SourceId} missing, back to automatic");
            status = "Temperature sensor not found – handed back to automatic";
            state.CurveTemp = float.NaN;
            return null;
        }
        _warned.Remove(p.ControlId);
        status = $"{source!.Name} {SensorFormat.Format(source.Type, t)}";
        if (t >= p.CriticalTemp) return 100;

        // Hysteresis: only follow the curve down once the temperature dropped far enough
        if (!float.IsNaN(state.CurveTemp) && t < state.CurveTemp && state.CurveTemp - t < p.Hysteresis) t = state.CurveTemp;
        else state.CurveTemp = t;

        return Math.Clamp(Math.Max(FanCurve.Evaluate(p.Points, t), p.MinPercent), 0, 100);
    }
}
