using System.Text;

namespace Kelvra;

/// <summary>
/// Sensor history for the graphs and CSV logging, in fixed ring buffers:
/// the last hour at one sample per second (about 14 KB per sensor), and the last 24 hours as one-minute averages
/// (about 6 KB per sensor). The minute data can be saved to disk so graphs survive a restart.
/// Times are UTC, so summer/winter time changes don't stop the recording.
/// </summary>
public sealed class HistoryService
{
    public const int Capacity = 3600;
    public const int MinuteCapacity = 1440;
    public static readonly TimeSpan SecondsSpan = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxSpan = TimeSpan.FromHours(24);

    private const int FileMagic = 0x3148564B; // "KVH1"
    private const int MaxSensorsInFile = 20_000;

    private readonly Tier _seconds = new(Capacity);
    private readonly Tier _minutes = new(MinuteCapacity);
    private readonly Dictionary<string, (double Sum, int Count)> _minute = new();
    private DateTime _minuteStart;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Current UTC time (tests use a fake clock).</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Raised after each stored sample (about once per second).</summary>
    public event Action? Sampled;

    public void Record(SensorStore store)
    {
        var now = Clock();
        var gap = now - _last;
        // Fast refresh rates still store 1 sample/s. A clock set backwards by hand restarts the cadence instead of pausing it.
        if (gap.TotalMilliseconds < 950 && gap > TimeSpan.FromSeconds(-2)) return;
        _last = now;

        int head = _seconds.Head;
        _seconds.Times[head] = now;
        foreach (var s in store.All)
            _seconds.Buffer(s.Id)[head] = s.Value ?? float.NaN;
        _seconds.Advance();

        // One-minute averages for the long ranges
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (_minuteStart != default && minute != _minuteStart) FlushMinute();
        _minuteStart = minute;
        foreach (var s in store.All)
        {
            var acc = _minute.GetValueOrDefault(s.Id);
            if (s.Value is float v && !float.IsNaN(v)) acc = (acc.Sum + v, acc.Count + 1);
            _minute[s.Id] = acc;
        }

        Sampled?.Invoke();
    }

    private void FlushMinute()
    {
        int head = _minutes.Head;
        _minutes.Times[head] = _minuteStart.AddSeconds(30); // plotted in the middle of its minute
        foreach (var buffer in _minutes.Values.Values) buffer[head] = float.NaN;
        foreach (var (id, acc) in _minute)
            _minutes.Buffer(id)[head] = acc.Count > 0 ? (float)(acc.Sum / acc.Count) : float.NaN;
        _minutes.Advance();
        _minute.Clear();
    }

    /// <summary>
    /// Samples of one sensor within the last <paramref name="window"/>, oldest first, with UTC times.
    /// Up to an hour: one per second. Longer: one per minute (up to 24 h).
    /// </summary>
    public void GetSeries(string sensorId, TimeSpan window, List<(DateTime Time, float Value)> into)
    {
        into.Clear();
        var tier = window <= SecondsSpan ? _seconds : _minutes;
        tier.Read(sensorId, Clock() - window, into);
    }

    // ---------- saving the minute history ----------

    public static string DefaultFile => Path.Combine(AppSettings.Dir, "history.bin");

    /// <summary>Writes the 24 h minute history. Returns false if it couldn't (logged).</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                var order = _minutes.Order().ToList(); // ring slots, oldest first
                w.Write(FileMagic);
                w.Write(order.Count);
                foreach (int i in order) w.Write(_minutes.Times[i].Ticks);
                w.Write(_minutes.Values.Count);
                foreach (var (id, buffer) in _minutes.Values)
                {
                    byte[] name = Encoding.UTF8.GetBytes(id);
                    w.Write(name.Length);
                    w.Write(name);
                    foreach (int i in order) w.Write(buffer[i]);
                }
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving history", ex);
            return false;
        }
    }

    /// <summary>
    /// Loads a file written by <see cref="Save"/>, keeping only the last 24 h. A damaged or foreign file is ignored
    /// (never trusted for sizes: every count is checked against what's left in the file).
    /// </summary>
    public bool Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            long Left() => stream.Length - stream.Position;

            if (Left() < 8 || r.ReadInt32() != FileMagic) return false;
            int n = r.ReadInt32();
            if (n < 0 || n > MinuteCapacity || Left() < n * 8L + 4) return false;
            var times = new DateTime[n];
            for (int i = 0; i < n; i++)
            {
                long ticks = r.ReadInt64();
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
                times[i] = new DateTime(ticks, DateTimeKind.Utc);
            }

            int sensors = r.ReadInt32();
            if (sensors < 0 || sensors > MaxSensorsInFile) return false;
            var values = new Dictionary<string, float[]>();
            for (int s = 0; s < sensors; s++)
            {
                if (Left() < 4) return false;
                int len = r.ReadInt32();
                if (len <= 0 || len > 1024 || Left() < len + n * 4L) return false;
                string id = Encoding.UTF8.GetString(r.ReadBytes(len));
                var v = new float[n];
                for (int i = 0; i < n; i++) v[i] = r.ReadSingle();
                values[id] = v;
            }

            // Replay into the ring, oldest first, skipping anything older than 24 h or from the future
            var now = Clock();
            for (int i = 0; i < n; i++)
            {
                if (times[i] < now - MaxSpan || times[i] > now) continue;
                int head = _minutes.Head;
                _minutes.Times[head] = times[i];
                foreach (var buffer in _minutes.Values.Values) buffer[head] = float.NaN;
                foreach (var (id, v) in values) _minutes.Buffer(id)[head] = v[i];
                _minutes.Advance();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Log.Error("Loading history", ex);
            return false;
        }
    }

    /// <summary>One ring of timestamps plus one value buffer per sensor.</summary>
    private sealed class Tier
    {
        private readonly int _capacity;

        public Tier(int capacity)
        {
            _capacity = capacity;
            Times = new DateTime[capacity];
        }

        public DateTime[] Times { get; }
        public Dictionary<string, float[]> Values { get; } = new();
        public int Head { get; private set; }  // next slot to write
        public int Count { get; private set; } // filled slots

        public float[] Buffer(string id)
        {
            if (!Values.TryGetValue(id, out var buffer))
            {
                buffer = new float[_capacity];
                Array.Fill(buffer, float.NaN);
                Values[id] = buffer;
            }
            return buffer;
        }

        public void Advance()
        {
            Head = (Head + 1) % _capacity;
            if (Count < _capacity) Count++;
        }

        /// <summary>Filled slot indexes, oldest first.</summary>
        public IEnumerable<int> Order()
        {
            for (int i = Count; i > 0; i--) yield return (Head - i + _capacity) % _capacity;
        }

        public void Read(string id, DateTime from, List<(DateTime, float)> into)
        {
            if (!Values.TryGetValue(id, out var buffer) || Count == 0) return;
            int start = Head - 1;
            int n = 0;
            // walk back to find how many samples fall inside the window
            for (int i = 0; i < Count; i++)
            {
                int idx = (start - i + _capacity) % _capacity;
                if (Times[idx] < from) break;
                n++;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                int idx = (start - i + _capacity) % _capacity;
                into.Add((Times[idx], buffer[idx]));
            }
        }
    }
}
