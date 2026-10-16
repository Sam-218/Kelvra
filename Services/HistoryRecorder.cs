using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// Feeds the long-term history. After every poll (UI thread, at most once a second) it adds the recorded sensors' fresh
/// readings to the current minute, the fans' speed per duty, and the app in front; each finished minute is written to
/// <see cref="HistoryDb"/> on a worker thread, in order. Minute detail older than the chosen number of days is pruned
/// once a day. Also watches the fans for one that stops.
/// </summary>
public sealed class HistoryRecorder : IDisposable
{
    private sealed class Acc
    {
        public double Sum, Min = double.MaxValue, Max = double.MinValue;
        public int N;

        public void Add(double v)
        {
            Sum += v;
            N++;
            Min = Math.Min(Min, v);
            Max = Math.Max(Max, v);
        }
    }

    private readonly HistoryDb _db;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, Acc> _sensors = new();
    private readonly Dictionary<(string Fan, int Duty), Acc> _fans = new();
    private readonly FanWatch _fanWatch = new();
    private List<(SensorVm Sensor, string Role)> _recorded = new();
    private List<(SensorVm Fan, SensorVm? Control)> _fanPairs = new();
    private SensorVm? _cpuTemp, _gpuTemp;
    private int _builtFor = -1;
    private string _watchedKey = "";
    private long _minuteTs;
    private DateTime _lastSample = DateTime.MinValue;
    private long _prunedDay;
    private Task _writing = Task.CompletedTask;

    public HistoryRecorder(HistoryDb db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    public HistoryDb Db => _db;
    public AppUsageTracker Apps { get; } = new();

    /// <summary>Devices with a recorded sensor: the poll reads them every 5 s even while Kelvra is in the tray.</summary>
    public HashSet<string> Devices { get; private set; } = new();

    /// <summary>A fan stopped while it should be turning (title, message).</summary>
    public event Action<string, string>? FanStopped;

    /// <summary>A minute was written (Reports refreshes "today").</summary>
    public event Action? MinuteWritten;

    /// <summary>Kelvra's own fan curves: the duty they set (null = automatic), so fans they drive are watched too.</summary>
    public Func<string, float?> AppliedDuty { get; set; } = _ => null;

    public void Sample(SensorStore store, bool gameRunning, DateTime utcNow)
    {
        if (!_settings.LongHistory || !store.Loaded) return;
        if ((utcNow - _lastSample).TotalMilliseconds < 950 && utcNow > _lastSample) return;
        _lastSample = utcNow;

        string watchedKey = string.Join("|", Watched());
        if (store.All.Count != _builtFor || watchedKey != _watchedKey) Rebuild(store, watchedKey);

        long minute = HistoryDb.Unix(utcNow) / 60 * 60;
        if (_minuteTs != 0 && minute != _minuteTs) Flush();
        _minuteTs = minute;

        foreach (var (sensor, _) in _recorded)
            if (Plausible(sensor) is float v)
                Bucket(_sensors, sensor.Id).Add(v);

        foreach (var (fan, control) in _fanPairs)
        {
            if (fan.NewValue is not float rpm || !float.IsFinite(rpm)) continue;
            float? duty = AppliedDuty(control?.Id ?? "") ?? (control?.HardwareType == HardwareType.Motherboard ? control.NewValue : null);
            if (_fanWatch.Check(fan.Id, rpm, duty, utcNow))
                FanStopped?.Invoke($"{fan.Name} stopped", $"{fan.GroupName}: {fan.Name} reads 0 RPM although it's driven at {duty:0} %. Check that it isn't blocked or unplugged.");
            if (control?.NewValue is float d && rpm > 0)
                Bucket(_fans, (fan.Id, FanDrift.Bin(d))).Add(rpm);
        }

        Apps.Sample(_cpuTemp is null ? null : Plausible(_cpuTemp), _gpuTemp is null ? null : Plausible(_gpuTemp), gameRunning);
    }

    /// <summary>
    /// A new reading worth keeping. Without admin rights (or the PawnIO driver) CPU sensors read 0 instead of nothing;
    /// a running PC is never at 0 °C or 0 W, so those are left out rather than dragging averages down.
    /// </summary>
    internal static float? Plausible(SensorVm s) =>
        s.NewValue is float v && float.IsFinite(v) && !(v <= 0 && s.Type is SensorType.Temperature or SensorType.Power) ? v : null;

    private static Acc Bucket<TKey>(Dictionary<TKey, Acc> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var acc)) map[key] = acc = new Acc();
        return acc;
    }

    private IEnumerable<string> Watched() =>
        _settings.Alerts.Select(a => a.SensorId)
            .Concat(_settings.HistorySelected)
            .Concat(_settings.Fans.Select(f => f.SourceId))
            .Concat(_settings.Overlays.Where(p => p.Enabled).SelectMany(p => p.Items).Select(i => i.SensorId))
            .Where(id => id.Length > 0).Distinct().Order();

    private void Rebuild(SensorStore store, string watchedKey)
    {
        _builtFor = store.All.Count;
        _watchedKey = watchedKey;
        var roles = HistoryRoles.Pick(store.All, Watched());
        _recorded = roles.Select(r => (store.ById[r.Key], r.Value)).ToList();
        Devices = _recorded.Select(r => r.Sensor.HardwareId).ToHashSet();
        _cpuTemp = _recorded.FirstOrDefault(r => r.Role == HistoryRoles.CpuTemp).Sensor;
        _gpuTemp = _recorded.FirstOrDefault(r => r.Role == HistoryRoles.GpuTemp).Sensor;
        _fanPairs = _recorded.Where(r => r.Role == HistoryRoles.Fan)
            .Select(r => (r.Sensor, store.ById.GetValueOrDefault(HistoryRoles.ControlOf(r.Sensor.Id)))).ToList();
    }

    /// <summary>Hands the finished minute to the database (on a worker thread, after any write still running).</summary>
    public void Flush()
    {
        if (_minuteTs == 0) return;
        var roles = _recorded.ToDictionary(r => r.Sensor.Id, r => r);
        var sensors = new List<MinuteValue>();
        foreach (var (id, acc) in _sensors)
        {
            if (acc.N == 0 || !roles.TryGetValue(id, out var r)) continue;
            long dbId = _db.SensorId(id, r.Sensor.Name, r.Sensor.GroupName, (int)r.Sensor.Type, r.Role);
            sensors.Add(new MinuteValue(dbId, acc.Sum, acc.N, acc.Min, acc.Max));
        }
        var fans = new List<FanDutySample>();
        foreach (var ((fan, duty), acc) in _fans)
            if (roles.TryGetValue(fan, out var r))
                fans.Add(new FanDutySample(_db.SensorId(fan, r.Sensor.Name, r.Sensor.GroupName, (int)r.Sensor.Type, r.Role), duty, acc.Sum, acc.N));
        var (apps, foreground) = Apps.TakeMinute(_db.AppId);
        _sensors.Clear();
        _fans.Clear();

        var when = HistoryDb.Local(_minuteTs);
        var batch = new MinuteBatch(_minuteTs, HistoryDb.DayOf(when), sensors, apps, foreground, fans);
        long pruneBefore = 0;
        if (batch.Day != _prunedDay)
        {
            _prunedDay = batch.Day;
            pruneBefore = HistoryDb.Unix(when.Date.AddDays(-_settings.HistoryMinuteDays));
        }
        _writing = _writing.ContinueWith(_ =>
        {
            try
            {
                if (sensors.Count > 0 || apps.Count > 0) _db.WriteMinute(batch);
                if (pruneBefore > 0) _db.PruneMinutes(pruneBefore);
            }
            catch (Exception ex)
            {
                Log.Error("Writing history", ex);
            }
        }, TaskScheduler.Default);
        _writing.ContinueWith(_ => MinuteWritten?.Invoke(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Runs <paramref name="read"/> on a worker thread, after the writes queued before it.</summary>
    public Task<T> ReadAsync<T>(Func<HistoryDb, T> read)
    {
        var task = _writing.ContinueWith(_ => read(_db), TaskScheduler.Default);
        return task;
    }

    /// <summary>Writes what's left of the current minute and waits for it (exit, sign-out).</summary>
    public void Stop()
    {
        Flush();
        _minuteTs = 0;
        try { _writing.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* logged by the write itself */ }
    }

    public void Dispose()
    {
        Apps.Dispose();
        _db.Dispose();
    }
}
