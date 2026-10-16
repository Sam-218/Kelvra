using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>
/// Decides which devices a poll reads. With the window (or mini mode, or a CSV log) open, every device every tick. Hidden in
/// the tray: devices whose sensors something uses right now (an overlay on screen, an alert, a fan curve) every tick, drives
/// at most every 10 s (SMART reads are slow); devices the long-term history records every 5 s; everything else once a minute,
/// so the 24 h graphs still get their minute averages.
/// </summary>
public sealed class PollScheduler
{
    public static readonly TimeSpan RecordEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StorageEvery = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, DateTime> _last = new();
    private readonly object _gate = new();

    /// <summary>
    /// The plan for one poll. Built on the UI thread from what's in use; the returned check runs on the polling thread
    /// and remembers when each device was last read.
    /// </summary>
    public Func<string, HardwareType, bool> Plan(bool everything, IReadOnlySet<string> live, IReadOnlySet<string> recorded, DateTime now) =>
        (id, type) =>
        {
            lock (_gate)
            {
                bool due = !_last.TryGetValue(id, out var last) || Due(everything, live.Contains(id), recorded.Contains(id), type, now - last);
                if (due) _last[id] = now;
                return due;
            }
        };

    internal static bool Due(bool everything, bool live, bool recorded, HardwareType type, TimeSpan age)
    {
        if (everything) return true;
        if (live) return type != HardwareType.Storage || age >= StorageEvery;
        if (recorded) return age >= RecordEvery;
        return age >= SweepEvery;
    }

    /// <summary>The top-level device a sensor belongs to, for each sensor id that exists (unknown ids are skipped).</summary>
    public static HashSet<string> DevicesOf(IEnumerable<string> sensorIds, IReadOnlyDictionary<string, SensorVm> byId)
    {
        var set = new HashSet<string>();
        foreach (var id in sensorIds)
            if (byId.TryGetValue(id, out var s)) set.Add(s.HardwareId);
        return set;
    }
}
