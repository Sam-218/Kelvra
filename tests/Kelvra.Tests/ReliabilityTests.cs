using System.Reflection;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

public class ReliabilityTests
{
    // ---------- REL-01: settings file ----------

    [Fact]
    public void Missing_settings_give_defaults()
    {
        using var dir = new TempDir();
        var s = AppSettings.Load(Path.Combine(dir.Path, "settings.json"));
        Assert.Equal(1000, s.RefreshMs);
        Assert.Null(s.RecoveredFrom);
    }

    [Fact]
    public void Settings_save_and_load_round_trip()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sub", "settings.json");
        var s = new AppSettings { RefreshMs = 750, Theme = "Light" };
        s.Overlays.Add(new OverlayProfile { Name = "Mine" });
        s.Save(path);

        var back = AppSettings.Load(path);
        Assert.Equal((750, "Light", "Mine"), (back.RefreshMs, back.Theme, back.Overlays[0].Name));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupt_settings_are_kept_aside_not_overwritten()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json", "{ \"Overlays\": [ { \"Name\": \"Hours of work\" "u8.ToArray()); // truncated JSON

        var s = AppSettings.Load(path);

        Assert.Empty(s.Overlays);
        Assert.NotNull(s.RecoveredFrom);
        Assert.False(File.Exists(path));
        Assert.Contains("Hours of work", File.ReadAllText(s.RecoveredFrom!));
        s.Save(path); // the next save no longer destroys the old data
        Assert.True(File.Exists(s.RecoveredFrom));
    }

    [Theory]
    [InlineData(-5, 250)]
    [InlineData(0, 250)]
    [InlineData(750, 750)]
    [InlineData(99999, 5000)]
    public void Refresh_rate_is_clamped_on_load(int stored, int expected)
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json", System.Text.Encoding.UTF8.GetBytes($"{{ \"RefreshMs\": {stored} }}"));
        Assert.Equal(expected, AppSettings.Load(path).RefreshMs);
    }

    [Fact]
    public void Null_lists_and_unknown_choices_are_repaired()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json",
            "{ \"Alerts\": null, \"Overlays\": [ null, { \"Name\": \"A\" } ], \"HistoryMinutes\": 7, \"LogIntervalSeconds\": 0, \"GroupOrder\": null }"u8.ToArray());
        var s = AppSettings.Load(path);
        Assert.Empty(s.Alerts);
        Assert.Equal("A", Assert.Single(s.Overlays).Name);
        Assert.Equal((5, 1), (s.HistoryMinutes, s.LogIntervalSeconds));
        Assert.Empty(s.GroupOrder);
        Assert.Null(s.RecoveredFrom);
    }

    // ---------- REL-02 / PERF-01: polling loop ----------

    private static List<SensorReading> OneReading(float value) =>
        new() { new SensorReading("/cpu/0/load/0", "/cpu/0", "CPU", HardwareType.Cpu, "Total", SensorType.Load, value, value, value) };

    [Fact]
    public async Task Polling_survives_errors_and_recovers()
    {
        int calls = 0;
        var store = new SensorStore(() => ++calls == 2 ? throw new IOException("driver hiccup") : OneReading(calls));
        var errors = new List<string?>();
        store.Updated += () => errors.Add(store.Error);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));

        await store.RunAsync(() => 100, cts.Token);

        Assert.True(calls >= 4, $"only {calls} polls");
        Assert.Contains("driver hiccup", errors);
        Assert.Null(store.Error); // cleared by the next good poll
        Assert.Equal(calls, store.ById["/cpu/0/load/0"].Value);
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_polling()
    {
        int calls = 0;
        var store = new SensorStore(() => OneReading(++calls));
        store.Updated += () => throw new InvalidOperationException("page bug");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await store.RunAsync(() => 100, cts.Token);
        Assert.True(calls >= 3, $"only {calls} polls");
    }

    [Fact]
    public async Task Refresh_rate_is_poll_to_poll_not_poll_plus_wait()
    {
        // Each poll takes 300 ms. At 400 ms per poll that's ~7 polls in 2.8 s; the old "poll, then wait 400 ms" gave ~4.
        int calls = 0;
        var store = new SensorStore(() => { Thread.Sleep(300); return OneReading(++calls); });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.8));
        await store.RunAsync(() => 400, cts.Token);
        Assert.InRange(calls, 6, 8);
    }

    // ---------- FS-03: CSV interval in time ----------

    [Fact]
    public void Csv_rows_follow_the_interval_even_when_samples_are_slower()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var logger = new CsvLogger { Folder = dir.Path, Clock = () => now };
        logger.Start(new[] { Data.Sensor("s", SensorType.Load, 50) }, intervalSeconds: 10);

        for (int t = 0; t <= 60; t += 2) // refresh every 2 s
        {
            now = new DateTime(2026, 1, 1, 12, 0, 0).AddSeconds(t);
            logger.OnSample();
        }
        logger.Stop();

        var rows = File.ReadAllLines(logger.FilePath!).Skip(1).Select(l => l[..19]).ToList();
        Assert.Equal(new[] { "00", "10", "20", "30", "40", "50", "60" }.Select(s => $"2026-01-01 12:{(s == "60" ? "01:00" : "00:" + s)}"), rows);
    }

    [Fact]
    public void Csv_writes_every_sample_at_one_second_and_restarts_after_a_pause()
    {
        using var dir = new TempDir();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var now = t0;
        var logger = new CsvLogger { Folder = dir.Path, Clock = () => now };
        logger.Start(new[] { Data.Sensor("s", SensorType.Load, 50) }, intervalSeconds: 1);
        foreach (double s in new[] { 0, 0.97, 1.95, 3.0, 300.0, 301.0 }) // samples ~1 s apart, then the PC slept
        {
            now = t0.AddSeconds(s);
            logger.OnSample();
        }
        logger.Stop();
        Assert.Equal(6, logger.Rows);
    }

    // ---------- FS-04: native struct layout ----------

    [Fact]
    public void File_information_struct_matches_the_native_layout()
    {
        var type = typeof(DuplicateFinder).GetNestedType("BY_HANDLE_FILE_INFORMATION", BindingFlags.NonPublic)!;
        Assert.Equal(52, Marshal.SizeOf(type));
        Assert.Equal(28, (int)Marshal.OffsetOf(type, "VolumeSerialNumber"));
        Assert.Equal(48, (int)Marshal.OffsetOf(type, "FileIndexLow"));
    }
}
