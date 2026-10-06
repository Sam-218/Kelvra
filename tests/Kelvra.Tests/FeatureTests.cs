using System.Windows.Input;
using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

/// <summary>Configurable hotkeys, settings save errors, 24 h history, overlay sharing, duplicate re-check, Overview picks.</summary>
public class FeatureTests
{
    // ---------- hotkeys ----------

    [Theory]
    [InlineData("Ctrl+Alt+F10", ModifierKeys.Control | ModifierKeys.Alt, Key.F10)]
    [InlineData("shift + ctrl + o", ModifierKeys.Control | ModifierKeys.Shift, Key.O)]
    [InlineData("Win+Alt+5", ModifierKeys.Windows | ModifierKeys.Alt, Key.D5)]
    public void Hotkeys_parse_and_print_in_a_fixed_order(string text, ModifierKeys mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal((mods, key), (h.Modifiers, h.Key));
        Assert.Equal(h, Hotkey.Parse(h.ToString())); // round trip
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]   // no key
    [InlineData("Ctrl+O+P")]     // two keys
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+LeftShift")]
    public void Invalid_hotkeys_are_rejected(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Hotkey_warnings_flag_shortcuts_other_apps_need()
    {
        Assert.Contains("Chrome", Hotkey.Parse("Ctrl+Shift+O")!.Value.Warning());     // the old default
        Assert.Contains("Excel", Hotkey.Parse("Ctrl+Shift+L")!.Value.Warning());      // the old default
        Assert.Contains("Ctrl or Alt", Hotkey.Parse("Shift+F2")!.Value.Warning());
        Assert.Contains("Most programs", Hotkey.Parse("Ctrl+K")!.Value.Warning());
        Assert.Contains("AltGr", Hotkey.Parse("Ctrl+Alt+L")!.Value.Warning());
        Assert.Null(Hotkey.Parse(Hotkey.DefaultOverlays)!.Value.Warning());
        Assert.Null(Hotkey.Parse(Hotkey.DefaultLock)!.Value.Warning());
    }

    [Fact]
    public void Hotkey_native_flags_match_RegisterHotKey()
    {
        var h = Hotkey.Parse("Ctrl+Alt+Shift+Win+F10")!.Value;
        Assert.Equal(0x1u | 0x2u | 0x4u | 0x8u, h.NativeModifiers);
        Assert.Equal(0x79u, h.VirtualKey); // VK_F10
    }

    // ---------- settings ----------

    [Fact]
    public void Failed_settings_save_is_reported_not_swallowed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "settings.json")); // a folder where the file should go
        var s = new AppSettings();

        Assert.False(s.Save(Path.Combine(dir.Path, "settings.json")));
        Assert.False(string.IsNullOrEmpty(s.LastSaveError));
        Assert.True(s.Save(Path.Combine(dir.Path, "ok.json")));
        Assert.Null(s.LastSaveError);
    }

    [Fact]
    public void Bad_hotkeys_and_new_ranges_are_repaired_on_load()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json",
            "{ \"HotkeyOverlays\": \"Ctrl+Banana\", \"HotkeyLock\": \"\", \"HistoryMinutes\": 1440 }"u8.ToArray());
        var s = AppSettings.Load(path);
        Assert.Equal(Hotkey.DefaultOverlays, s.HotkeyOverlays);
        Assert.Equal("", s.HotkeyLock); // "" = turned off on purpose
        Assert.Equal(1440, s.HistoryMinutes);
    }

    [Fact]
    public void Older_settings_get_the_new_hotkey_defaults()
    {
        using var dir = new TempDir();
        var s = AppSettings.Load(dir.File("settings.json", "{ \"RefreshMs\": 1000 }"u8.ToArray()));
        Assert.Equal(("Ctrl+Alt+F10", "Ctrl+Alt+F11", false), (s.HotkeyOverlays, s.HotkeyLock, s.MinimizeToTray));
    }

    // ---------- history ----------

    private static (SensorStore Store, SensorVm Sensor) OneSensor(float value)
    {
        var store = new SensorStore();
        var s = Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, value);
        store.All.Add(s);
        store.ById[s.Id] = s;
        return (store, s);
    }

    private static void Feed(HistoryService history, SensorStore store, ref DateTime clock, TimeSpan duration, TimeSpan step)
    {
        var end = clock + duration;
        while (clock < end)
        {
            history.Record(store);
            clock += step;
        }
    }

    [Fact]
    public void History_keeps_minute_averages_for_24_hours()
    {
        var (store, s) = OneSensor(50);
        var clock = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var history = new HistoryService { Clock = () => clock };

        Feed(history, store, ref clock, TimeSpan.FromHours(3), TimeSpan.FromSeconds(1));

        var series = new List<(DateTime Time, float Value)>();
        history.GetSeries(s.Id, TimeSpan.FromHours(24), series);
        Assert.InRange(series.Count, 178, 180); // one point per finished minute
        Assert.All(series, p => Assert.Equal(50f, p.Value));
        history.GetSeries(s.Id, TimeSpan.FromMinutes(5), series);
        Assert.InRange(series.Count, 299, 301); // short ranges still per second
    }

    [Fact]
    public void History_minute_points_are_averages()
    {
        var (store, s) = OneSensor(0);
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var history = new HistoryService { Clock = () => clock };
        for (int i = 0; i < 120; i++)
        {
            s.Update(new SensorReading(s.Id, s.HardwareId, "CPU", HardwareType.Cpu, "T", SensorType.Temperature, i < 60 ? 40 : 60, 0, 0));
            history.Record(store);
            clock = clock.AddSeconds(1);
        }
        history.Record(store); // starts minute 3, which closes minute 2
        var series = new List<(DateTime Time, float Value)>();
        history.GetSeries(s.Id, TimeSpan.FromHours(2), series);
        Assert.Equal(new[] { 40f, 60f }, series.Select(p => p.Value));
    }

    [Fact]
    public void History_survives_a_restart_when_saved()
    {
        using var dir = new TempDir();
        var (store, s) = OneSensor(42);
        var clock = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var before = new HistoryService { Clock = () => clock };
        Feed(before, store, ref clock, TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(1));
        string file = Path.Combine(dir.Path, "history.bin");
        Assert.True(before.Save(file));

        var after = new HistoryService { Clock = () => clock };
        Assert.True(after.Load(file));
        var series = new List<(DateTime Time, float Value)>();
        after.GetSeries(s.Id, TimeSpan.FromHours(6), series);
        Assert.InRange(series.Count, 28, 30);
        Assert.All(series, p => Assert.Equal(42f, p.Value));

        // A day later the saved points are too old to show
        clock = clock.AddHours(25);
        var stale = new HistoryService { Clock = () => clock };
        stale.Load(file);
        stale.GetSeries(s.Id, TimeSpan.FromHours(24), series);
        Assert.Empty(series);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData(new byte[] { 0x4B, 0x56, 0x48, 0x31, 0xFF, 0xFF, 0xFF, 0x7F })] // right magic, absurd count
    public void Damaged_history_files_are_ignored(byte[] content)
    {
        using var dir = new TempDir();
        Assert.False(new HistoryService().Load(dir.File("history.bin", content)));
    }

    [Fact]
    public void Csv_logging_keeps_going_when_the_clock_goes_back_an_hour()
    {
        // End of summer time: 03:00 becomes 02:00 again. This used to stop the log for a whole hour.
        using var dir = new TempDir();
        var now = new DateTime(2026, 10, 25, 2, 59, 50);
        var logger = new CsvLogger { Folder = dir.Path, Clock = () => now };
        logger.Start(new[] { Data.Sensor("s", SensorType.Load, 50) }, intervalSeconds: 1);
        for (int i = 0; i < 5; i++) { logger.OnSample(); now = now.AddSeconds(1); }
        now = now.AddHours(-1);
        for (int i = 0; i < 60; i++) { logger.OnSample(); now = now.AddSeconds(1); }
        logger.Stop();
        Assert.InRange(logger.Rows, 64, 65);
    }

    // ---------- overlay sharing ----------

    [Fact]
    public void Overlays_round_trip_through_export()
    {
        var p = new OverlayProfile { Name = "Mine", FontSize = 18, AccentColor = "#112233", Layout = OverlayLayout.Horizontal, X = 300 };
        p.Items.Add(new OverlayItem { SensorId = "/gpu/0/load/0", Label = "GPU", ShowBar = true });

        var back = OverlayExchange.Import(OverlayExchange.Export(p));

        Assert.Equal(("Mine", 18d, "#112233", OverlayLayout.Horizontal, 300d), (back.Name, back.FontSize, back.AccentColor, back.Layout, back.X));
        var item = Assert.Single(back.Items);
        Assert.Equal(("/gpu/0/load/0", "GPU", true), (item.SensorId, item.Label, item.ShowBar));
        Assert.NotEqual(p.Id, back.Id);
        Assert.False(back.Locked); // so it can be placed
    }

    [Fact]
    public void Imported_overlays_are_cleaned_up()
    {
        string items = string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"SensorId\":\"/x/{i}\"}}"));
        string json = "{\"Kind\":\"kelvra-overlay\",\"Version\":1,\"Overlay\":{" +
                      "\"Name\":\"" + new string('A', 5000) + "\",\"FontSize\":900,\"BackgroundOpacity\":-50,\"CornerRadius\":999," +
                      "\"AccentColor\":\"red; drop table\",\"ValueColor\":null,\"Width\":1e300,\"X\":\"NaN\"," +
                      "\"Items\":[null," + items + "]}}";
        var p = OverlayExchange.Import(json.Replace("\"X\":\"NaN\",", ""));

        Assert.Equal(80, p.Name.Length);
        Assert.Equal((40d, 0, 24), (p.FontSize, p.BackgroundOpacity, p.CornerRadius));
        Assert.Equal(new OverlayProfile().AccentColor, p.AccentColor);
        Assert.Equal(new OverlayProfile().ValueColor, p.ValueColor);
        Assert.Equal(4000, p.Width);
        Assert.Equal(OverlayExchange.MaxItems, p.Items.Count);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Kind\":\"something-else\",\"Version\":1,\"Overlay\":{}}")]
    [InlineData("{\"Kind\":\"kelvra-overlay\",\"Version\":1}")]
    [InlineData("{\"Kind\":\"kelvra-overlay\",\"Version\":99,\"Overlay\":{}}")]
    public void Foreign_or_future_files_are_refused_with_a_reason(string json) =>
        Assert.False(string.IsNullOrWhiteSpace(Assert.Throws<InvalidDataException>(() => OverlayExchange.Import(json)).Message));

    [Fact]
    public void Huge_overlay_files_are_refused_before_reading()
    {
        using var dir = new TempDir();
        string path = dir.File("big.kelvra-overlay.json", new byte[OverlayExchange.MaxFileBytes + 1]);
        Assert.Throws<InvalidDataException>(() => OverlayExchange.ImportFile(path));
    }

    // ---------- duplicates ----------

    [Fact]
    public void A_duplicate_edited_after_the_search_is_not_recycled()
    {
        using var dir = new TempDir();
        var data = Data.Random(200_000, 3);
        dir.File(@"a\copy.bin", data, new DateTime(2025, 1, 1));
        string b = dir.File(@"b\copy.bin", data, new DateTime(2025, 1, 2));
        var root = new DiskScanner().Scan(dir.Path, CancellationToken.None).Root;
        var group = Assert.Single(new DuplicateFinder().Find(root, 0, true, CancellationToken.None));
        Assert.All(group.Files, f => Assert.True(DuplicateFinder.IsUnchanged(f)));

        File.WriteAllBytes(b, Data.Flip(data, 10)); // edited: same size, new content and time
        var edited = group.Files.Single(f => f.Path == b);
        Assert.False(DuplicateFinder.IsUnchanged(edited));

        File.Delete(b);
        Assert.False(DuplicateFinder.IsUnchanged(edited));
    }

    // ---------- overview / diagnostics / toasts ----------

    [Fact]
    public void Overview_picks_the_cpu_temperature_people_expect()
    {
        var cores = Data.Sensor("/cpu/0/temperature/1", SensorType.Temperature, 70, "Core #1");
        var tctl = Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 55, "Core (Tctl/Tdie)");
        Assert.Same(tctl, Overview.CpuTemperature(new[] { cores, tctl }));
        Assert.Same(cores, Overview.CpuTemperature(new[] { cores, Data.Sensor("x", SensorType.Temperature, 40, "CCD1") }));
        Assert.Null(Overview.CpuTemperature(new[] { Data.Sensor("y", SensorType.Load, 5, "CPU Total") }));
    }

    [Fact]
    public void Overview_panels_show_one_card_per_cpu_and_gpu()
    {
        var sensors = new[]
        {
            Data.Sensor("/cpu/0/load/0", SensorType.Load, 6, "CPU Total"),
            Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 48, "Core (Tctl/Tdie)"),
            Data.Sensor("/gpu/0/temperature/0", SensorType.Temperature, 46, "GPU Core", HardwareType.GpuNvidia),
            Data.Sensor("/ram/load/0", SensorType.Load, 39, "Memory", HardwareType.Memory),
        };
        var panels = Overview.Panels(sensors);
        Assert.Equal(new[] { "CPU", "GPU" }, panels.Select(p => p.Kind));
        panels[0].Update(sensors);
        Assert.Equal(("48.0", "6"), (panels[0].Stats[0].Value, panels[0].Stats[1].Value));
        Assert.Equal("/cpu/0/temperature/0", panels[0].TempId);
    }

    [Fact]
    public void Cpu_clock_averages_real_core_clocks_only()
    {
        var sensors = new[]
        {
            Data.Sensor("/cpu/0/load/0", SensorType.Load, 8, "CPU Total"),
            Data.Sensor("/cpu/0/clock/1", SensorType.Clock, 4100, "Core #1"),
            Data.Sensor("/cpu/0/clock/2", SensorType.Clock, 4080, "Core #2"),
            Data.Sensor("/cpu/0/clock/3", SensorType.Clock, 300, "Core #1 (Effective)"),
            Data.Sensor("/cpu/0/clock/4", SensorType.Clock, 500, "Cores (Average Effective)"),
            Data.Sensor("/cpu/0/clock/5", SensorType.Clock, 100, "Bus Speed"),
        };
        var panel = Overview.Panels(sensors)[0];
        panel.Update(sensors);
        Assert.Equal(("4090", "4080 – 4100"), (panel.Stats[2].Value, panel.Stats[2].Range));
    }

    [Fact]
    public void Diagnostics_hide_the_user_name()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string text = Diagnostics.Anonymize($"Settings: {profile}\\AppData\\Roaming\\Kelvra");
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text);
    }

    [Fact]
    public void Toast_text_is_escaped() =>
        Assert.Contains("&lt;b&gt; &amp; &quot;", Toasts.BuildXml("t", "<b> & \""));
}
