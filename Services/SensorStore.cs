using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Polls the hardware on a background thread and keeps a bindable list of sensors up to date.</summary>
public sealed class SensorStore : IDisposable
{
    private readonly HardwareService _hardware = new();
    private readonly Action _open;
    private readonly Func<List<SensorReading>> _poll;
    private readonly Dictionary<string, int> _hardwareOrder = new();
    private readonly Dictionary<string, string> _groupNames = new();

    public ObservableCollection<SensorVm> All { get; } = new();
    public Dictionary<string, SensorVm> ById { get; } = new();

    public bool Loaded { get; private set; }
    public string? Error { get; private set; }

    public event Action? FirstLoad;
    public event Action? Updated;

    private readonly Func<string, float?, bool> _setFan;
    private readonly Dictionary<string, float?> _fanRequests = new();
    private bool _fanWorkerRunning;

    public SensorStore()
    {
        _open = _hardware.Open;
        _poll = _hardware.Poll;
        _setFan = _hardware.SetFan;
    }

    /// <summary>For tests: reads from <paramref name="poll"/> instead of the real hardware.</summary>
    internal SensorStore(Func<List<SensorReading>> poll, Func<string, float?, bool>? setFan = null)
    {
        _open = () => { };
        _poll = poll;
        _setFan = setFan ?? ((_, _) => false);
    }

    /// <summary>
    /// Asks for a fan output to be set (percent) or handed back to automatic (null). Applied on a worker thread
    /// (the hardware lock may be held by a poll); only the newest request per fan counts.
    /// </summary>
    public void RequestFan(string controlId, float? percent)
    {
        lock (_fanRequests)
        {
            _fanRequests[controlId] = percent;
            if (_fanWorkerRunning) return;
            _fanWorkerRunning = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                KeyValuePair<string, float?>[] batch;
                lock (_fanRequests)
                {
                    if (_fanRequests.Count == 0)
                    {
                        _fanWorkerRunning = false;
                        return;
                    }
                    batch = _fanRequests.ToArray();
                    _fanRequests.Clear();
                }
                foreach (var (id, percent) in batch) _setFan(id, percent);
            }
        });
    }

    /// <summary>Hands every fan back to the BIOS right now (exit, crash, errors).</summary>
    public void ReleaseFans()
    {
        lock (_fanRequests) _fanRequests.Clear();
        _hardware.ReleaseFans();
    }

    /// <summary>Sensors that don't come from the hardware (the "Game" device), added to every poll on the UI thread.</summary>
    public Func<IEnumerable<SensorReading>?>? ExtraReadings { get; set; }

    /// <summary>Longest wait between retries while polling keeps failing.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The polling loop: open the hardware, then poll → apply → raise <see cref="Updated"/> → wait, until cancelled.
    /// Called from the UI thread; polling runs on the thread pool and events are raised back on the UI thread.
    /// Errors don't end it: <see cref="Error"/> is shown and the loop retries (slower while it keeps failing).
    /// </summary>
    public async Task RunAsync(Func<int> refreshMs, CancellationToken ct)
    {
        bool opened = false;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            try
            {
                if (!opened)
                {
                    await Task.Run(_open, ct);
                    opened = true;
                }
                var readings = await Task.Run(_poll, ct);
                if (ExtraReadings?.Invoke() is { } extra) readings.AddRange(extra);
                Apply(readings);
                Error = null;
                failures = 0;

                if (!Loaded)
                {
                    Loaded = true;
                    FirstLoad?.Invoke();
                }
                Updated?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                // A driver hiccup or an unplugged device mustn't freeze every reading, overlay and alert until restart
                if (Error != ex.Message) Log.Error("Sensor polling", ex); // once per new error, not every retry
                Error = ex.Message;
                failures++;
                try { Updated?.Invoke(); } catch { /* the banner will update on the next good poll */ }
            }

            // Fixed cadence: the refresh rate is the time from poll to poll, so slow devices don't stretch it
            int interval = Math.Clamp(refreshMs(), 100, 60_000);
            long wait = failures == 0
                ? interval - (Environment.TickCount64 - started)
                : (long)Math.Min(interval * Math.Pow(2, failures - 1), MaxRetryDelay.TotalMilliseconds);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(wait, 20)), ct); // tiny minimum: let the UI breathe
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Reconnects to the hardware so newly available sensors (e.g. CPU temps after PawnIO) show up.</summary>
    public Task ReopenAsync() => Task.Run(_hardware.Reopen);

    /// <summary>Resets every sensor's min/max; the new values show after the next poll.</summary>
    public Task ResetMinMaxAsync() => Task.Run(_hardware.ResetMinMax);

    /// <summary>Updates known sensors in place (bindings just see property changes) and appends newly found ones.</summary>
    private void Apply(List<SensorReading> readings)
    {
        var fresh = new List<SensorReading>();
        foreach (var r in readings)
        {
            if (ById.TryGetValue(r.Id, out var vm)) vm.Update(r);
            else fresh.Add(r);
        }
        if (fresh.Count == 0) return;

        foreach (var r in fresh)
        {
            if (_hardwareOrder.TryAdd(r.HardwareId, _hardwareOrder.Count))
                _groupNames[r.HardwareId] = UniqueGroupName(r.HardwareName);
        }

        // AIDA-style order: hardware first, then sensor type
        // The Game device keeps its own order (FPS first); OrderBy is stable
        foreach (var r in fresh.OrderBy(r => _hardwareOrder[r.HardwareId]).ThenBy(r => r.HardwareType == GameSensors.Hardware ? 0 : TypeOrder(r.Type)))
        {
            var vm = new SensorVm(r, _groupNames[r.HardwareId])
            {
                Order = All.Count,
                HardwareOrder = _hardwareOrder[r.HardwareId],
            };
            ById[r.Id] = vm;
            All.Add(vm);
        }
    }

    /// <summary>"GeForce RTX 4060", then "GeForce RTX 4060 #2" for an identical second card.</summary>
    private string UniqueGroupName(string name)
    {
        if (!_groupNames.ContainsValue(name)) return name;
        int n = 2;
        while (_groupNames.ContainsValue($"{name} #{n}")) n++;
        return $"{name} #{n}";
    }

    /// <summary>Order of sensor types within a device: temperatures first, then load, clocks …; the rest by enum value.</summary>
    private static int TypeOrder(SensorType t) => t switch
    {
        SensorType.Temperature => 0,
        SensorType.Load => 1,
        SensorType.Clock => 2,
        SensorType.Power => 3,
        SensorType.Voltage => 4,
        SensorType.Current => 5,
        SensorType.Fan => 6,
        SensorType.Control => 7,
        _ => 10 + (int)t,
    };

    public void Dispose() => _hardware.Dispose();
}
