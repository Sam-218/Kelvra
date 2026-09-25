using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>Polls the hardware on a background thread and keeps a bindable list of sensors up to date.</summary>
public sealed class SensorStore : IDisposable
{
    private readonly HardwareService _hardware = new();
    private readonly Dictionary<string, int> _hardwareOrder = new();
    private readonly Dictionary<string, string> _groupNames = new();

    public ObservableCollection<SensorVm> All { get; } = new();
    public Dictionary<string, SensorVm> ById { get; } = new();

    public bool Loaded { get; private set; }
    public string? Error { get; private set; }

    public event Action? FirstLoad;
    public event Action? Updated;

    public async Task RunAsync(Func<int> refreshMs, CancellationToken ct)
    {
        try
        {
            await Task.Run(_hardware.Open, ct);
            while (!ct.IsCancellationRequested)
            {
                var readings = await Task.Run(_hardware.Poll, ct);
                Apply(readings);

                if (!Loaded)
                {
                    Loaded = true;
                    FirstLoad?.Invoke();
                }
                Updated?.Invoke();

                await Task.Delay(refreshMs(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Updated?.Invoke();
        }
    }

    /// <summary>Reconnects to the hardware so newly available sensors (e.g. CPU temps after PawnIO) show up.</summary>
    public Task ReopenAsync() => Task.Run(_hardware.Reopen);

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
        foreach (var r in fresh.OrderBy(r => _hardwareOrder[r.HardwareId]).ThenBy(r => TypeOrder(r.Type)))
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

    private string UniqueGroupName(string name)
    {
        if (!_groupNames.ContainsValue(name)) return name;
        int n = 2;
        while (_groupNames.ContainsValue($"{name} #{n}")) n++;
        return $"{name} #{n}";
    }

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
