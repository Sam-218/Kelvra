using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

/// <summary>Tiered polling, the long-term history store, reports, crashes, changes and fan health.</summary>
public class HistoryFeatureTests : IDisposable
{
    public void Dispose() => SensorFormat.UseFahrenheit = false;

    // ---------- tiered polling ----------

    [Fact]
    public void Polling_reads_everything_while_the_window_is_open_and_only_what_is_used_in_the_tray()
    {
        var second = TimeSpan.FromSeconds(1);
        Assert.True(PollScheduler.Due(everything: true, live: false, recorded: false, HardwareType.Network, second));
        Assert.True(PollScheduler.Due(false, live: true, recorded: false, HardwareType.Cpu, second));
        Assert.False(PollScheduler.Due(false, live: true, recorded: false, HardwareType.Storage, second));   // SMART: every 10 s
        Assert.True(PollScheduler.Due(false, live: true, recorded: false, HardwareType.Storage, TimeSpan.FromSeconds(10)));
        Assert.False(PollScheduler.Due(false, false, recorded: true, HardwareType.GpuNvidia, second));
        Assert.True(PollScheduler.Due(false, false, recorded: true, HardwareType.GpuNvidia, TimeSpan.FromSeconds(5)));
        Assert.False(PollScheduler.Due(false, false, false, HardwareType.Network, TimeSpan.FromSeconds(30)));
        Assert.True(PollScheduler.Due(false, false, false, HardwareType.Network, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_plan_reads_each_device_the_first_time_then_by_its_tier()
    {
        var scheduler = new PollScheduler();
        var t0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var empty = new HashSet<string>();
        var first = scheduler.Plan(false, empty, empty, t0);
        Assert.True(first("/nic/0", HardwareType.Network));
        var later = scheduler.Plan(false, empty, empty, t0.AddSeconds(2));
        Assert.False(later("/nic/0", HardwareType.Network));
        Assert.True(scheduler.Plan(true, empty, empty, t0.AddSeconds(3))("/nic/0", HardwareType.Network));
    }

    [Fact]
    public void Skipped_sensors_keep_their_value_but_are_not_new()
    {
        var s = Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 61);
        Assert.Equal(61f, s.NewValue);
        s.MarkSkipped();
        Assert.Equal(61f, s.Value);
        Assert.Null(s.NewValue);
        Assert.Equal(61f, s.FreshValue()); // still drawn for a few seconds
        Assert.Null(s.FreshValue(-1));
    }

    [Fact]
    public void Zero_degrees_and_zero_watts_are_not_recorded()
    {
        // Without admin rights CPU sensors read 0 instead of nothing
        Assert.Null(HistoryRecorder.Plausible(Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 0)));
        Assert.Null(HistoryRecorder.Plausible(Data.Sensor("/cpu/0/power/0", SensorType.Power, 0)));
        Assert.Equal(0f, HistoryRecorder.Plausible(Data.Sensor("/cpu/0/load/0", SensorType.Load, 0)));
        Assert.Equal(45f, HistoryRecorder.Plausible(Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 45)));
    }

    [Fact]
    public void Network_trace_structures_match_the_windows_x64_layout()
    {
        // Sizes and offsets from the Windows SDK (evntrace.h, evntcons.h) for 64-bit processes: a wrong field here would
        // make Windows read the session settings or the events in the wrong place
        Assert.Equal((120, 448, 112, 80), NetTrace.StructSizes());
        Assert.Equal((28, 424, 86, 96, 40), NetTrace.Offsets());
    }

    // ---------- history store ----------

    private static HistoryDb NewDb(TempDir dir) => HistoryDb.Open(Path.Combine(dir.Path, "history.db"));

    [Fact]
    public void Minutes_add_up_into_days_and_pruning_keeps_the_days()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        long sensor = db.SensorId("/cpu/0/temperature/0", "CPU Package", "Ryzen", (int)SensorType.Temperature, HistoryRoles.CpuTemp);
        var noon = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Local);
        long day = HistoryDb.DayOf(noon);
        for (int m = 0; m < 3; m++)
        {
            long ts = HistoryDb.Unix(noon.AddMinutes(m));
            db.WriteMinute(new MinuteBatch(ts, day, new() { new MinuteValue(sensor, 60 * (50 + m), 60, 50 + m - 2, 50 + m + 5) }, new(), null, new()));
        }

        var days = db.Days(sensor, day, day + 86400);
        Assert.Single(days);
        Assert.Equal(180, days[0].N);
        Assert.Equal(51, days[0].Avg, 3);
        Assert.Equal(48, days[0].Min);
        Assert.Equal(57, days[0].Max);
        Assert.Equal(3, db.OnDays(day, day + 86400).Single().Minutes);
        Assert.Equal(3, db.Minutes(sensor, 0, long.MaxValue).Count);

        db.PruneMinutes(HistoryDb.Unix(noon.AddDays(1)));
        Assert.Empty(db.Minutes(sensor, 0, long.MaxValue));
        Assert.Single(db.Days(sensor, day, day + 86400));
    }

    [Fact]
    public void A_reopened_history_keeps_its_data_and_schema()
    {
        using var dir = new TempDir();
        using (var db = NewDb(dir)) db.SetMeta("hello", "1");
        using (var db = NewDb(dir)) Assert.Equal("1", db.GetMeta("hello"));
    }

    [Fact]
    public void Apps_are_kept_apart_by_path_and_their_days_add_up()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        long a = db.AppId(@"C:\Games\A\launcher.exe", "Game A");
        long b = db.AppId(@"C:\Games\B\launcher.exe", "Game B");
        Assert.NotEqual(a, b);
        long day = HistoryDb.DayOf(DateTime.Today);
        long ts = HistoryDb.Unix(DateTime.Today.AddHours(20));
        for (int i = 0; i < 2; i++)
            db.WriteMinute(new MinuteBatch(ts + 60 * i, day, new(),
                new() { new AppSlice(a, 60, 60 * 70, 60, 75, 60 * 80, 60, 85 + i, 1000, 10) }, a, new()));
        var total = db.AppTotals(day, day + 86400).Single();
        Assert.Equal("Game A", total.Name);
        Assert.Equal(120, total.FgSec);
        Assert.Equal(80, total.GpuTempAvg!.Value, 3);
        Assert.Equal(86, total.GpuTempMax);
        Assert.Equal(2000, total.NetDown);
        Assert.Equal(ts + 60, db.LastMinuteBefore(ts + 3600));
    }

    [Fact]
    public void The_same_crash_is_recorded_once()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        var crash = new CrashRecord(1_700_000_000, CrashKinds.AppCrash, "game.exe", "nvwgf2umx.dll", "c0000005", null);
        Assert.Equal(1, db.InsertCrashes(new[] { crash }));
        Assert.Equal(0, db.InsertCrashes(new[] { crash }));
        Assert.Single(db.Crashes(0, long.MaxValue));
    }

    // ---------- roles ----------

    [Fact]
    public void Roles_pick_the_package_temperature_and_the_discrete_gpu()
    {
        var sensors = new List<SensorVm>
        {
            Data.Sensor("/cpu/0/temperature/1", SensorType.Temperature, 50, "Core #1"),
            Data.Sensor("/cpu/0/temperature/0", SensorType.Temperature, 55, "Core (Tctl/Tdie)"),
            Data.Sensor("/gpu-intel/0/temperature/0", SensorType.Temperature, 40, "GPU Core", HardwareType.GpuIntel),
            Data.Sensor("/gpu-nvidia/0/temperature/0", SensorType.Temperature, 60, "GPU Core", HardwareType.GpuNvidia),
            Data.Sensor("/lpc/nct/0/fan/1", SensorType.Fan, 900, "Fan #1", HardwareType.Motherboard),
            Data.Sensor("/nic/0/throughput/0", SensorType.Throughput, 1, "Download", HardwareType.Network),
        };
        var roles = HistoryRoles.Pick(sensors, new[] { "/nic/0/throughput/0", "/missing" });
        Assert.Equal(HistoryRoles.CpuTemp, roles["/cpu/0/temperature/0"]);
        Assert.Equal(HistoryRoles.GpuTemp, roles["/gpu-nvidia/0/temperature/0"]);
        Assert.False(roles.ContainsKey("/gpu-intel/0/temperature/0"));
        Assert.Equal(HistoryRoles.Fan, roles["/lpc/nct/0/fan/1"]);
        Assert.Equal("", roles["/nic/0/throughput/0"]); // watched by the user
        Assert.False(roles.ContainsKey("/missing"));
        Assert.Equal("/lpc/nct/0/control/1", HistoryRoles.ControlOf("/lpc/nct/0/fan/1"));
    }

    // ---------- fans ----------

    [Fact]
    public void A_fan_that_stops_while_driven_is_reported_once()
    {
        var watch = new FanWatch();
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        for (int s = 0; s <= 12; s++) Assert.False(watch.Check("fan", 1100, 50, t.AddSeconds(s)));
        bool fired = false;
        for (int s = 13; s <= 40; s++) fired |= watch.Check("fan", 0, 50, t.AddSeconds(s));
        Assert.True(fired);
        Assert.False(watch.Check("fan", 0, 50, t.AddSeconds(60))); // not again within 30 min
    }

    [Theory]
    [InlineData(null)] // duty unknown (a graphics card fan: zero-RPM mode is normal)
    [InlineData(20f)]  // driven too slowly to be sure
    public void Fans_that_may_stop_on_purpose_are_not_reported(float? duty)
    {
        var watch = new FanWatch();
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        for (int s = 0; s <= 12; s++) watch.Check("fan", 1100, duty, t.AddSeconds(s));
        for (int s = 13; s <= 60; s++) Assert.False(watch.Check("fan", 0, duty, t.AddSeconds(s)));
    }

    [Fact]
    public void An_empty_fan_header_is_never_reported()
    {
        var watch = new FanWatch();
        var t = new DateTime(2026, 10, 15, 12, 0, 0);
        for (int s = 0; s <= 120; s++) Assert.False(watch.Check("header", 0, 60, t.AddSeconds(s)));
    }

    [Fact]
    public void A_fan_slower_at_the_same_duty_than_months_ago_is_found()
    {
        var today = new DateTime(2026, 10, 15);
        long Day(int ago) => HistoryDb.DayOf(today.AddDays(-ago));
        var rows = new List<FanDutyRow>
        {
            new(1, Day(60), 60, 1200 * 400, 400),  // before: 1,200 RPM at 60 %
            new(1, Day(2), 60, 1000 * 400, 400),   // now: 1,000 RPM
            new(2, Day(60), 60, 1200 * 400, 400),
            new(2, Day(2), 60, 1150 * 400, 400),   // 4 % slower: normal
            new(3, Day(60), 60, 1200 * 10, 10),    // too little data
            new(3, Day(2), 60, 800 * 400, 400),
        };
        var found = FanDrift.Find(rows, today);
        var slow = Assert.Single(found);
        Assert.Equal(1, slow.Fan);
        Assert.Equal(60, slow.Duty);
        Assert.Equal(16.7, slow.Percent, 1);
        Assert.Equal(60, FanDrift.Bin(56));
        Assert.Equal(60, FanDrift.Bin(55));
    }

    // ---------- crashes ----------

    [Fact]
    public void Crashes_are_explained_in_plain_words()
    {
        var driver = CrashExplainer.Explain(new CrashRecord(0, CrashKinds.AppCrash, "Cyberpunk2077.exe", "nvwgf2umx.dll", "c0000005", null));
        Assert.Equal("Cyberpunk2077 crashed", driver.Title);
        Assert.Equal("NVIDIA driver", driver.Culprit);
        Assert.Contains("Cyberpunk2077", driver.Reason);

        var access = CrashExplainer.Explain(new CrashRecord(0, CrashKinds.AppCrash, "tool.exe", "tool.exe", "0xC0000005", null));
        Assert.Equal("Access violation", access.Culprit);

        var blue = CrashExplainer.Explain(new CrashRecord(0, CrashKinds.BlueScreen, "", null, "0x124", null));
        Assert.Equal("Blue screen: WHEA_UNCORRECTABLE_ERROR", blue.Title);
        Assert.Equal(0x124u, CrashExplainer.ParseBugcheck("0x00000124 (0x0000000000000000, 0xffff)"));
        Assert.Equal("c0000005", CrashExplainer.NormalizeCode("3221225477"));
        Assert.Equal("Unexpected shutdown", CrashExplainer.Explain(new CrashRecord(0, CrashKinds.Shutdown, "", null, null, null)).Title);
    }

    [Fact]
    public void A_culprit_behind_several_recent_crashes_is_pointed_out()
    {
        long now = HistoryDb.Unix(DateTime.Now);
        var crashes = Enumerable.Range(0, 3).Select(i => new CrashRecord(now - i * 3600, CrashKinds.DriverReset, "", "nvlddmkm", null, null))
            .Append(new CrashRecord(now - 100, CrashKinds.AppHang, "notepad.exe", null, null, null)).ToList();
        Assert.Contains("NVIDIA driver is behind 3 of the 4", CrashesPage.Pattern(crashes, DateTime.Now));
        Assert.Null(CrashesPage.Pattern(crashes.Take(2).ToList(), DateTime.Now));
        // Apps crashing in a Windows part they called isn't Windows' fault: no "Windows (KernelBase) is behind…"
        var kernelBase = Enumerable.Range(0, 4).Select(i => new CrashRecord(now - i, CrashKinds.AppCrash, $"app{i}.exe", "KERNELBASE.dll", "e0434352", null)).ToList();
        Assert.Null(CrashesPage.Pattern(kernelBase, DateTime.Now));
    }

    [Fact]
    public void The_note_of_what_happened_before_a_crash_reads_well()
    {
        string text = CrashView.Describe("cpu-temp=72;gpu-temp=83;gpu-load=98;fg=Cyberpunk 2077");
        Assert.Equal("In the 5 minutes before (highest): CPU 72 °C · GPU 83 °C · GPU load 98 % · Cyberpunk 2077 in front", text);
        Assert.Equal("", CrashView.Describe(null));
    }

    // ---------- changes ----------

    [Fact]
    public void Changes_are_what_differs_between_two_snapshots()
    {
        var before = new Dictionary<string, Dictionary<string, string>>
        {
            [ChangeKinds.Driver] = new() { ["NVIDIA GeForce RTX 4070"] = "576.52" },
            [ChangeKinds.App] = new() { ["Steam"] = "2.10", ["Old Tool"] = "1.0" },
            [ChangeKinds.Startup] = new() { ["Discord"] = "on" },
        };
        var after = new Dictionary<string, Dictionary<string, string>>
        {
            [ChangeKinds.Driver] = new() { ["NVIDIA GeForce RTX 4070"] = "581.08" },
            [ChangeKinds.App] = new() { ["Steam"] = "2.11", ["New Game"] = "1.0" },
            // Startup couldn't be read this time: not taken as "everything removed"
        };
        var changes = ChangeWatcher.Diff(before, after, 100);
        Assert.Contains(changes, c => c.Kind == ChangeKinds.Driver && c.Before == "576.52" && c.After == "581.08");
        Assert.Contains(changes, c => c.Subject == "Steam" && c.Before == "2.10" && c.After == "2.11");
        Assert.Contains(changes, c => c.Subject == "New Game" && c.Before == null);
        Assert.Contains(changes, c => c.Subject == "Old Tool" && c.After == null);
        Assert.DoesNotContain(changes, c => c.Kind == ChangeKinds.Startup);
        Assert.Equal("NVIDIA GeForce RTX 4070 driver 576.52 → 581.08", ChangesPage.Words(changes.First(c => c.Kind == ChangeKinds.Driver)).Text);
    }

    private static GameSessionSummary Session(string game, DateTime started, double fps, float gpu, double minutes = 30) =>
        new(game, started, minutes * 60, fps, null, null, null, null, 0, 0, 0, 0, null, gpu, false, null);

    [Fact]
    public void Driver_effects_compare_the_same_game_on_both_sides()
    {
        var change = new DateTime(2026, 10, 1);
        var sessions = new[]
        {
            Session("Cyberpunk 2077", change.AddDays(-3), 90, 78),
            Session("Cyberpunk 2077", change.AddDays(2), 100, 74),
            Session("Minesweeper", change.AddDays(3), 500, 40),     // only after: not compared
            Session("Doom", change.AddDays(-2), 200, 70, minutes: 5), // too little play
            Session("Doom", change.AddDays(2), 150, 70),
        };
        var effect = Assert.Single(ChangeEffects.Of(change, null, sessions));
        Assert.Equal("Cyberpunk 2077", effect.Game);
        Assert.Equal("Cyberpunk 2077: 90 → 100 FPS (+11 %), hottest GPU 78 °C → 74 °C", ChangeEffects.Describe(effect));
        // The next driver change ends the window
        Assert.Empty(ChangeEffects.Of(change, change.AddDays(1), sessions));
    }

    // ---------- reports ----------

    [Fact]
    public void Report_periods_cover_whole_weeks_months_and_years()
    {
        var day = new DateTime(2026, 10, 15, 14, 30, 0);
        var month = ReportPeriod.For(ReportRange.Month, day);
        Assert.Equal(new DateTime(2026, 10, 1), month.From);
        Assert.Equal(new DateTime(2026, 11, 1), month.To);
        Assert.Equal(new DateTime(2026, 9, 1), month.Previous.From);
        var week = ReportPeriod.For(ReportRange.Week, day);
        Assert.Equal(7, week.Days);
        Assert.True(week.From <= day && day < week.To);
        Assert.Equal(new DateTime(2026, 1, 1), ReportPeriod.For(ReportRange.Year, day).From);
    }

    [Fact]
    public void A_report_says_when_a_chip_held_the_users_alert_limit()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        const string key = "/gpu-nvidia/0/temperature/0";
        long gpu = db.SensorId(key, "GPU Core", "RTX", (int)SensorType.Temperature, HistoryRoles.GpuTemp);
        long app = db.AppId(@"C:\Games\cp.exe", "Cyberpunk 2077");
        var start = DateTime.Today.AddHours(20);
        long day = HistoryDb.DayOf(start);
        for (int m = 0; m < 20; m++)
        {
            double t = m < 5 ? 82 : 70;
            db.WriteMinute(new MinuteBatch(HistoryDb.Unix(start.AddMinutes(m)), day,
                new() { new MinuteValue(gpu, t * 60, 60, t - 1, t + 1) }, new() { new AppSlice(app, 60, 0, 0, null, t * 60, 60, t + 1, 0, 0) }, app, new()));
        }

        var alerts = new[] { new AlertRule { SensorId = key, Threshold = 80 } };
        var report = ReportBuilder.Build(db, ReportPeriod.For(ReportRange.Day, start), Array.Empty<GameSessionSummary>(), alerts);

        Assert.Equal(20, report.OnMinutes);
        Assert.Equal((5, 80.0), report.OverLimit[HistoryRoles.GpuTemp]);
        Assert.Equal("Cyberpunk 2077", report.HottestWith[HistoryRoles.GpuTemp]);
        var line = report.Insights.First(i => i.Tone == InsightTone.Hot);
        Assert.StartsWith("The GPU held 80 °C or more for 5 min (hottest 83 °C at", line.Text);
        Assert.EndsWith(", Cyberpunk 2077 in front).", line.Text);
        Assert.Contains(report.Insights, i => i.Text.StartsWith("Kelvra recorded 20 min of PC time; Cyberpunk 2077"));
        Assert.Equal(ReportBuilder.DefaultGpuLimit, ReportBuilder.LimitFor(HistoryRoles.GpuTemp, key, Array.Empty<AlertRule>()));
    }

    [Fact]
    public void A_week_report_reads_the_hottest_minute_and_sums_itself_up_in_a_sentence()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        long gpu = db.SensorId("/gpu-nvidia/0/temperature/0", "GPU Core", "RTX", (int)SensorType.Temperature, HistoryRoles.GpuTemp);
        long app = db.AppId(@"C:\Games\cp.exe", "Cyberpunk 2077");
        var start = DateTime.Today.AddHours(20);
        for (int m = 0; m < 20; m++)
        {
            double t = m == 7 ? 82 : 60;
            db.WriteMinute(new MinuteBatch(HistoryDb.Unix(start.AddMinutes(m)), HistoryDb.DayOf(start),
                new() { new MinuteValue(gpu, t * 60, 60, t - 1, t + 1) }, new() { new AppSlice(app, 60, 0, 0, null, t * 60, 60, t + 1, 0, 0) }, app, new()));
        }

        var report = ReportBuilder.Build(db, ReportPeriod.For(ReportRange.Week, start), Array.Empty<GameSessionSummary>(), Array.Empty<AlertRule>());

        Assert.Equal(start.AddMinutes(7), report.Stats[HistoryRoles.GpuTemp].MaxAt);
        Assert.Equal("Cyberpunk 2077", report.HottestWith[HistoryRoles.GpuTemp]);
        Assert.Single(report.Series[HistoryRoles.GpuTemp]); // twenty minutes in one hour: one hourly point
        Assert.Equal("A warm week.", ReportSummary.Headline(report)); // peak 83 °C: warm, not hot
        Assert.Contains("your PC was on for 20 min.", ReportSummary.Subline(report));
        Assert.EndsWith("· Cyberpunk 2077", ReportSummary.Peak(report, HistoryRoles.GpuTemp));
        Assert.Equal("", ReportSummary.PcTimeChange(report)); // nothing recorded the week before
    }

    [Fact]
    public void A_reports_games_are_one_row_per_game()
    {
        GameSessionSummary Session(string game, double minutes, double fps, int gpuBound, int cpuBound, bool bench = false) =>
            new(game, DateTime.Today, minutes * 60, fps, fps * 0.7, null, null, null, 0, gpuBound, cpuBound, 0, null, null, bench, null);
        var rows = ReportSummary.Games(new[]
        {
            Session("Elden Ring", 30, 60, 0, 10), Session("Cyberpunk 2077", 60, 90, 50, 5), Session("cyberpunk 2077", 60, 70, 50, 5),
            Session("Cyberpunk 2077", 5, 200, 0, 0, bench: true),
        });

        Assert.Equal(new[] { "Cyberpunk 2077", "Elden Ring" }, rows.Select(r => r.Name));
        Assert.Equal("2 h 00 m", rows[0].Played);
        Assert.Equal("80", rows[0].Avg); // weighted by time; the benchmark left out
        Assert.Equal("GPU", rows[0].HeldBy);
        Assert.Equal("CPU", rows[1].HeldBy);
    }

    [Fact]
    public void The_games_page_groups_sessions_per_game_most_recent_first()
    {
        GameSessionSummary Session(string game, int daysAgo, double fps, bool bench = false, bool crashed = false) =>
            new(game, DateTime.Today.AddDays(-daysAgo).AddHours(20), 3600, fps, fps * 0.5, null, null, null, 0, 100, 0, 0, null, null, bench, null,
                Crashed: crashed);
        var games = GameGroup.From(new[]
        {
            Session("Wardogs", 1, 118), Session("Hollow Knight Silksong", 3, 126), Session("wardogs", 2, 107, crashed: true),
            Session("Wardogs", 5, 77), Session("Benchmarked", 4, 300, bench: true),
        });

        Assert.Equal(new[] { "Wardogs", "Hollow Knight Silksong", "Benchmarked" }, games.Select(g => g.Name));
        var wardogs = games[0];
        Assert.Equal(3, wardogs.Sessions.Count);
        Assert.Equal(DateTime.Today.AddDays(-1).AddHours(20), wardogs.Sessions[0].Started); // newest first
        Assert.Equal(1, wardogs.Crashes);
        Assert.StartsWith("3 sessions · 3 h 00 m played · last Yesterday", wardogs.Summary);
        Assert.Equal("101", wardogs.Avg);
        Assert.Equal("GPU", wardogs.HeldBy);
        Assert.Null(games[2].Stats); // only a benchmark: no played stats
        Assert.StartsWith("1 benchmark · last", games[2].Summary);
    }

    [Fact]
    public void A_game_running_hotter_than_before_is_noticed_like_for_like()
    {
        using var dir = new TempDir();
        using var db = NewDb(dir);
        long app = db.AppId(@"C:\Games\cp.exe", "Cyberpunk 2077");
        void Play(DateTime at, int minutes, double gpu)
        {
            for (int m = 0; m < minutes; m++)
                db.WriteMinute(new MinuteBatch(HistoryDb.Unix(at.AddMinutes(m)), HistoryDb.DayOf(at), new(),
                    new() { new AppSlice(app, 60, 0, 0, null, gpu * 60, 60, gpu, 0, 0) }, app, new()));
        }
        var today = DateTime.Today.AddHours(19);
        Play(today.AddDays(-10), 90, 70);
        Play(today, 45, 78);

        var report = ReportBuilder.Build(db, ReportPeriod.For(ReportRange.Day, today), Array.Empty<GameSessionSummary>(), Array.Empty<AlertRule>());
        Assert.Contains(report.Insights, i => i.Text.StartsWith("In Cyberpunk 2077 the GPU ran 8° hotter than in the 30 days before"));
    }

    [Fact]
    public void Insight_durations_read_naturally()
    {
        Assert.Equal("45 min", Insights.Duration(45 * 60));
        Assert.Equal("2 h 05 min", Insights.Duration(125 * 60));
        Assert.Equal("7 h 40 m", Insights.Short(460 * 60));
        var us = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("Oct 10 – 16", ReportSummary.WeekDates(new DateTime(2026, 10, 10), new DateTime(2026, 10, 16), us));
        Assert.Equal("Sep 28 – Oct 4", ReportSummary.WeekDates(new DateTime(2026, 9, 28), new DateTime(2026, 10, 4), us));
        Assert.Equal("10 – 16 Oct", ReportSummary.WeekDates(new DateTime(2026, 10, 10), new DateTime(2026, 10, 16), System.Globalization.CultureInfo.GetCultureInfo("en-GB")));
    }
}
