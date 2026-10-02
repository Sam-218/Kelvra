using System.Globalization;
using System.Text;

namespace Kelvra;

/// <summary>Writes selected sensors to a CSV file while recording (one row per interval).</summary>
public sealed class CsvLogger : IDisposable
{
    public static readonly string DefaultFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kelvra Logs");

    private StreamWriter? _writer;
    private List<SensorVm> _columns = new();
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private DateTime _nextRow;

    // Samples arrive about once a second but not exactly (>= 950 ms apart), so "due" allows half a second of slack
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(500);

    /// <summary>Where new logs go (tests use a temp folder).</summary>
    internal string Folder { get; init; } = DefaultFolder;
    /// <summary>Current time (tests use a fake clock).</summary>
    internal Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public bool IsRecording => _writer != null;
    public string? FilePath { get; private set; }
    public DateTime Started { get; private set; }
    public long Rows { get; private set; }
    public int ColumnCount => _columns.Count;

    public event Action? StateChanged;

    /// <summary>Starts a new file in Documents\Kelvra Logs with one column per sensor. Throws if there's nothing to record.</summary>
    public void Start(IEnumerable<SensorVm> sensors, int intervalSeconds)
    {
        Stop();
        _columns = sensors.ToList();
        if (_columns.Count == 0) throw new InvalidOperationException("Nothing to record – no sensors are shown.");

        Directory.CreateDirectory(Folder);
        FilePath = Path.Combine(Folder, $"Kelvra {Clock():yyyy-MM-dd HH-mm-ss}.csv");
        _writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        _interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        _nextRow = default;
        Rows = 0;
        Started = Clock();

        var header = new StringBuilder("Time");
        foreach (var s in _columns)
            header.Append(',').Append(Escape($"{s.GroupName} - {s.Name} [{SensorFormat.Unit(s.Type)}]"));
        _writer.WriteLine(header.ToString());
        StateChanged?.Invoke();
    }

    /// <summary>Called once per second by the history sampler.</summary>
    public void OnSample()
    {
        if (_writer == null) return;
        // Time-based, not "every n-th sample": with a refresh slower than 1 s the rows still come at the chosen interval
        var now = Clock();
        if (_nextRow != default && now + Slack < _nextRow) return;
        _nextRow = (_nextRow == default ? now : _nextRow) + _interval;
        if (_nextRow <= now) _nextRow = now + _interval; // fell behind (PC slept, long pause): restart the schedule

        var line = new StringBuilder(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var s in _columns)
        {
            line.Append(',');
            if (s.Value is float v && !float.IsNaN(v))
                line.Append(SensorFormat.ToDisplay(s.Type, v).ToString("0.###", CultureInfo.InvariantCulture));
        }
        try
        {
            _writer.WriteLine(line.ToString());
            Rows++;
        }
        catch (IOException)
        {
            Stop(); // disk full or file removed
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_writer == null) return;
        _writer.Dispose();
        _writer = null;
        StateChanged?.Invoke();
    }

    /// <summary>RFC 4180 quoting: wrap in quotes and double inner quotes when the text has a comma, quote, CR or LF.</summary>
    private static string Escape(string s) =>
        s.IndexOfAny(CsvSpecial) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static readonly char[] CsvSpecial = { ',', '"', '\n', '\r' };

    public void Dispose() => Stop();
}
