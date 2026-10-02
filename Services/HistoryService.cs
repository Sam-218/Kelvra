namespace Kelvra;

/// <summary>
/// Keeps the last hour of every sensor (one sample per second) in fixed ring buffers,
/// for the History graphs and CSV logging. About 14 KB per sensor (3,600 floats) for the hour.
/// </summary>
public sealed class HistoryService
{
    public const int Capacity = 3600;

    private readonly Dictionary<string, float[]> _values = new();
    private readonly DateTime[] _times = new DateTime[Capacity];
    private int _head;   // next slot to write
    private int _count;  // filled slots
    private DateTime _last = DateTime.MinValue;

    /// <summary>Raised after each stored sample (about once per second).</summary>
    public event Action? Sampled;

    public void Record(SensorStore store)
    {
        var now = DateTime.Now;
        if ((now - _last).TotalMilliseconds < 950) return; // fast refresh rates still store 1 sample/s
        _last = now;

        _times[_head] = now;
        foreach (var s in store.All)
        {
            if (!_values.TryGetValue(s.Id, out var buffer))
            {
                buffer = new float[Capacity];
                Array.Fill(buffer, float.NaN);
                _values[s.Id] = buffer;
            }
            buffer[_head] = s.Value ?? float.NaN;
        }

        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        Sampled?.Invoke();
    }

    /// <summary>Samples of one sensor within the last <paramref name="window"/>, oldest first.</summary>
    public void GetSeries(string sensorId, TimeSpan window, List<(DateTime Time, float Value)> into)
    {
        into.Clear();
        if (!_values.TryGetValue(sensorId, out var buffer) || _count == 0) return;

        var from = DateTime.Now - window;
        int start = _head - 1;
        int n = 0;
        // walk back to find how many samples fall inside the window
        for (int i = 0; i < _count; i++)
        {
            int idx = (start - i + Capacity) % Capacity;
            if (_times[idx] < from) break;
            n++;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            int idx = (start - i + Capacity) % Capacity;
            into.Add((_times[idx], buffer[idx]));
        }
    }
}
