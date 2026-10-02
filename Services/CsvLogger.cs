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
    private int _intervalSeconds = 1;
    private int _tick;

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

        Directory.CreateDirectory(DefaultFolder);
        FilePath = Path.Combine(DefaultFolder, $"Kelvra {DateTime.Now:yyyy-MM-dd HH-mm-ss}.csv");
        _writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        _intervalSeconds = Math.Max(1, intervalSeconds);
        _tick = 0;
        Rows = 0;
        Started = DateTime.Now;

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
        // Counts history samples, not seconds: with a refresh rate slower than 1 s the rows are further apart than the interval says
        if (_tick++ % _intervalSeconds != 0) return;

        var line = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
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

    /// <summary>RFC 4180 quoting: wrap in quotes and double inner quotes when the text has a comma, quote or newline.</summary>
    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose() => Stop();
}
