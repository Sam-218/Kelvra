using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

public class GamingTests
{
    // Real PresentMon 2.6.0 output (Tests/Gold/test_case_0.csv in the PresentMon repository)
    private const string GoldHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,TimeInQPC," +
        "MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,MsPCLatency," +
        "CPUStartQPC,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsVideoBusy,MsAnimationError," +
        "AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency,MsInstrumentedLatency";
    private const string GoldLine =
        "dwm.exe,1268,0x224B280A1C0,DXGI,1,0,0,Hardware: Legacy Flip,Application,2076674276,NA,16.47540000000000,16.63730000000000," +
        "0.08930000000000,0.06730000000000,16.4296,NA,2076511276,16.3893,16.3000,0.0893,0.8529,15.5144,1.0752,14.4392,0.0000,NA,332.7030,NA,NA,NA,NA";

    // What Kelvra asks for (--qpc_time_ms): a shorter header, columns in another order
    private const string KelvraHeader = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,AllowsTearing,PresentMode,FrameType," +
                                        "CPUStartQPCTime,MsBetweenPresents,MsCPUBusy,MsGPUBusy,MsUntilDisplayed,MsPCLatency";

    private static FrameSample Frame(double time, double ms, double? cpu = null, double? gpu = null, ulong swap = 1, double? untilDisplayed = 5) =>
        new(42, swap, 0, "Hardware: Independent Flip", time, ms, cpu, gpu, untilDisplayed);

    private static GameRules.AppInfo App(string exe) => new(GameRules.Normalize(exe), "", "", "");

    /// <summary>Feeds <paramref name="count"/> frames of <paramref name="ms"/> each, starting after <paramref name="start"/>. Returns the end time.</summary>
    private static double Feed(FrameStats stats, int count, double ms, double start = 0, double? cpuFraction = null, double? gpuFraction = null)
    {
        double t = start;
        for (int i = 0; i < count; i++)
        {
            t += ms;
            stats.Add(Frame(t, ms, cpuFraction * ms, gpuFraction * ms));
        }
        return t;
    }

    // ---------- PresentMon CSV ----------

    [Fact]
    public void Real_presentmon_output_is_read()
    {
        var csv = PresentMonCsv.FromHeader("﻿" + GoldHeader);
        Assert.NotNull(csv);
        var f = csv.Parse(GoldLine);
        Assert.NotNull(f);
        Assert.Equal((1268, 0x224B280A1C0UL, 1, "Hardware: Legacy Flip"), (f.ProcessId, f.SwapChain, f.SyncInterval, f.PresentMode));
        Assert.Equal((16.4754, 16.3, 1.0752, 16.4296), (f.FrameTimeMs, f.CpuBusyMs!.Value, f.GpuBusyMs!.Value, f.UntilDisplayedMs!.Value));
        Assert.Equal(16.3 + 16.4296, f.LatencyMs!.Value, 6);
        Assert.True(f.Displayed);
    }

    [Fact]
    public void Columns_are_found_by_name_not_position()
    {
        var csv = PresentMonCsv.FromHeader(KelvraHeader)!;
        var f = csv.Parse("game.exe,42,0x10,DXGI,0,1,Composed: Flip,Intel XeSS-FG,123456.5,8.3,7.1,8.0,NA,22.5")!;
        Assert.Equal((123456.5, 8.3, 7.1, 8.0), (f.TimeMs, f.FrameTimeMs, f.CpuBusyMs!.Value, f.GpuBusyMs!.Value));
        Assert.Null(f.LatencyMs); // not shown, so no latency
        Assert.False(f.Displayed);
    }

    [Theory]
    [InlineData("Started recording.")]
    [InlineData("")]
    [InlineData("game.exe,42,0x10")] // cut off
    [InlineData("game.exe,notanumber,0x10,DXGI,0,1,Composed: Flip,Application,1,8.3,7.1,8.0,1,NA")]
    [InlineData("game.exe,42,0x10,DXGI,0,1,Composed: Flip,Application,1,NA,7.1,8.0,1,NA")]
    [InlineData("game.exe,42,0x10,DXGI,0,1,Composed: Flip,Application,1,-3,7.1,8.0,1,NA")]
    public void Lines_that_arent_frames_are_skipped(string line) => Assert.Null(PresentMonCsv.FromHeader(KelvraHeader)!.Parse(line));

    [Theory]
    [InlineData("Capture failed: requires elevated privilege")]
    [InlineData("a,b,c")]
    public void Non_presentmon_headers_are_refused(string line) => Assert.Null(PresentMonCsv.FromHeader(line));

    // ---------- frame statistics ----------

    [Fact]
    public void Steady_16_7_ms_frames_are_60_fps()
    {
        var stats = new FrameStats();
        double end = Feed(stats, 600, 1000 / 60.0);
        var s = stats.Snapshot();
        Assert.Equal(60, s.Fps!.Value, 1);
        Assert.Equal(16.67, s.FrameTimeMs!.Value, 2);
        Assert.Equal(60, s.Low1!.Value, 1);
        Assert.Equal(60, s.AvgFps!.Value, 1);
        Assert.Equal(0, s.Stutters);
        Assert.Equal(10, s.SessionLength.TotalSeconds, 1);
    }

    [Fact]
    public void Percentile_lows_come_from_the_slowest_frames()
    {
        var stats = new FrameStats();
        double t = 0;
        // 980 frames at 10 ms (100 FPS) and 20 at 40 ms: the slowest 1 % are 25 FPS, the average stays high
        for (int i = 0; i < 1000; i++)
        {
            double ms = i % 50 == 25 ? 40 : 10;
            t += ms;
            stats.Add(Frame(t, ms));
        }
        var s = stats.Snapshot();
        Assert.Equal(25, s.Low1!.Value, 1);
        Assert.Equal(25, s.Low01!.Value, 1);
        Assert.InRange(s.AvgFps!.Value, 93, 96);
    }

    [Fact]
    public void Spikes_count_as_stutters_but_steady_or_smoothly_changing_rates_dont()
    {
        long clock = 100_000;
        var stats = new FrameStats(() => clock);
        double t = Feed(stats, 100, 10);
        t += 45;
        stats.Add(Frame(t, 45)); // 4.5× the median: a hitch
        t = Feed(stats, 100, 10, t);
        clock += 12_000; // 12 s later (e.g. alt-tabbed, so no frames were counted)
        var s = stats.Snapshot();
        Assert.Equal(1, s.Stutters);
        Assert.Equal(12, s.SecondsSinceStutter!.Value, 1);

        // 300 FPS with a 7 ms frame: twice the median, but only 3.7 ms longer – not noticeable
        var fast = new FrameStats();
        double u = Feed(fast, 100, 3.33);
        u += 7;
        fast.Add(Frame(u, 7));
        Assert.Equal(0, fast.Snapshot().Stutters);

        // A frame rate drifting from 60 to 40 FPS isn't stuttering
        var drift = new FrameStats();
        double v = 0;
        for (int i = 0; i < 300; i++)
        {
            double ms = 16.7 + i * 0.03;
            v += ms;
            drift.Add(Frame(v, ms));
        }
        Assert.Equal(0, drift.Snapshot().Stutters);
    }

    [Theory]
    [InlineData(0.98, 0.20, false, Bottleneck.GpuBound)]
    [InlineData(0.86, 0.01, true, Bottleneck.GpuBound)]  // VSync on but the GPU is maxed: still the GPU
    [InlineData(0.60, 0.20, false, Bottleneck.CpuBound)] // GPU waits, uneven frames: the CPU
    [InlineData(0.60, 0.02, false, Bottleneck.Capped)]   // GPU waits, perfectly even frames: a limiter
    [InlineData(0.60, 0.20, true, Bottleneck.Capped)]    // GPU waits with VSync on
    [InlineData(0.82, 0.02, false, Bottleneck.CpuBound)]
    public void Bottleneck_follows_gpu_busy_and_frame_pacing(double gpu, double variation, bool vsync, Bottleneck expected) =>
        Assert.Equal(expected, FrameStats.Classify(gpu, variation, vsync));

    [Fact]
    public void A_gpu_bound_game_reads_gpu_bound_even_though_presentmons_cpu_busy_is_maxed_too()
    {
        // Dying Light 2 showed CPU busy 98 % and GPU busy 98 % at once; the CPU was only waiting for the GPU
        var stats = new FrameStats();
        Feed(stats, 200, 18.5, cpuFraction: 0.98, gpuFraction: 0.98);
        var s = stats.Snapshot();
        Assert.Equal(Bottleneck.GpuBound, s.Bound);
        Assert.Equal(98, s.GpuBusyPercent!.Value, 1);
    }

    [Fact]
    public void Uneven_frames_with_an_idle_gpu_read_cpu_bound_and_even_ones_capped()
    {
        var cpuBound = new FrameStats();
        double t = 0;
        for (int i = 0; i < 300; i++)
        {
            double ms = i % 3 == 0 ? 14 : 9; // uneven, as when the CPU can't keep up
            t += ms;
            cpuBound.Add(Frame(t, ms, gpu: ms * 0.5));
        }
        Assert.Equal(Bottleneck.CpuBound, cpuBound.Snapshot().Bound);

        var capped = new FrameStats();
        Feed(capped, 300, 16.67, gpuFraction: 0.4); // a 60 FPS limiter
        Assert.Equal(Bottleneck.Capped, capped.Snapshot().Bound);
    }

    [Fact]
    public void Without_gpu_data_the_bottleneck_is_unknown() => Assert.Equal(Bottleneck.Unknown, FrameStats.Classify(null, 0.1, false));

    // ---------- after alt-tab ----------

    [Fact]
    public void Real_2_6_header_gives_the_time_column()
    {
        // As logged by Kelvra from PresentMon 2.6.0
        const string header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs," +
                              "MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed," +
                              "CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError," +
                              "AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";
        var csv = PresentMonCsv.FromHeader(header)!;
        var f = csv.Parse("b1-Win64-Shipping.exe,9000,0x1F2B7282400,DXGI,0,0,1,Hardware: Independent Flip,117972.5012,NA,13.3,13.3,0.17,1.19,8.5," +
                          "117979.8228,13.3,12.1,0.1,0.5,11.0,10.9,0.1,NA,NA,NA,NA,NA")!;
        Assert.Equal((117972.5012, 13.3, 10.9), (f.TimeMs, f.FrameTimeMs, f.GpuBusyMs!.Value));

        // A zero-length present (other apps write these) isn't a frame, but the line itself is fine: no log noise
        string zero = "claude.exe,7172,0x1F2B7282400,DXGI,0,0,0,Composed: Flip,117972.5012,NA,0.00000000000000,NA,0.16990000000000," +
                      "1.19020000000000,NA,117979.8228,0.1699,0.0000,0.1699,0.0000,0.8743,0.8648,0.0095,NA,NA,NA,NA,NA";
        Assert.Null(csv.Parse(zero));
        Assert.True(csv.IsComplete(zero));
        Assert.False(csv.IsComplete("claude.exe,7172"));
    }

    [Fact]
    public void Late_filter_starts_over_when_every_frame_is_late_for_10_seconds()
    {
        // Not a backlog (with a 512-frame buffer those clear in seconds) but the clocks drifted: don't lose FPS for good
        var late = new LateFrameFilter();
        Assert.False(late.IsLate(1000, 11_000));
        double t = 2000;
        for (double arrival = 15_000; arrival < 15_000 + LateFrameFilter.GiveUpMs; arrival += 500, t += 500)
            Assert.True(late.IsLate(t, arrival));
        Assert.False(late.IsLate(t, 15_000 + LateFrameFilter.GiveUpMs));       // gives up: this is "on time" now
        Assert.False(late.IsLate(t + 500, 15_500 + LateFrameFilter.GiveUpMs)); // and stays so
    }

    [Fact]
    public void Skipped_frames_dont_mix_into_the_fps_right_after()
    {
        // 30 FPS while hidden behind another window would otherwise linger in "the last second"
        var stats = new FrameStats();
        double t = Feed(stats, 120, 33.3); // 30 FPS (e.g. throttled)
        stats.MarkGap();                   // frames were skipped
        Feed(stats, 30, 10, t);            // 0.3 s back in the game at 100 FPS
        Assert.Equal(100, stats.Snapshot().Fps!.Value, 1);
    }

    [Theory]
    [InlineData(0, 0, 2560, 1440, false, true)]       // borderless / exclusive fullscreen
    [InlineData(-8, -8, 2568, 1448, true, false)]     // maximized window on a monitor without taskbar
    [InlineData(0, 0, 2560, 1440, true, false)]       // a captioned window exactly screen-sized
    [InlineData(0, 0, 1920, 1080, false, false)]      // smaller than the screen
    public void Only_real_fullscreen_counts_not_a_maximized_window(int l, int t, int r, int b, bool caption, bool fullscreen) =>
        Assert.Equal(fullscreen, GameRules.IsFullscreen((l, t, r, b), (0, 0, 2560, 1440), caption));

    [Fact]
    public void Leftover_extraction_folders_are_swept_but_nothing_else()
    {
        using var dir = new TempDir();
        string old = Path.Combine(dir.Path, "Kelvra-0123456789abcdef0123456789abcdef");
        string fresh = Path.Combine(dir.Path, "Kelvra-fedcba9876543210fedcba9876543210");
        string other = Path.Combine(dir.Path, "Kelvra-settings");
        foreach (var d in new[] { old, fresh, other }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(old, "PresentMon.exe"), "x");
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        Directory.SetCreationTimeUtc(other, DateTime.UtcNow.AddHours(-3));

        PresentMonRunner.SweepOldFolders(dir.Path, DateTime.UtcNow.AddHours(-1));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh)); // too new: may be in use right now
        Assert.True(Directory.Exists(other)); // not one of ours
    }

    [Fact]
    public void Frames_presentmon_delivers_late_are_recognised()
    {
        var late = new LateFrameFilter();
        // PresentMon started at our 10 000 ms; frames arrive ~50 ms after they happened
        Assert.False(late.IsLate(1000, 11_050));
        Assert.False(late.IsLate(2000, 12_040));
        Assert.False(late.IsLate(3000, 13_900));   // 0.9 s behind: still fine
        Assert.True(late.IsLate(4000, 30_000));    // 16 s behind: a backlog
        Assert.False(late.IsLate(0, 99_999));      // no time column: never late

        late.Reset();                              // PresentMon restarted: its clock starts at 0 again
        Assert.False(late.IsLate(100, 40_000));
    }

    [Fact]
    public void Pauses_like_alt_tab_or_loading_dont_count_as_stutters_or_lows()
    {
        var stats = new FrameStats();
        double t = Feed(stats, 300, 13.3);       // 75 FPS
        stats.Add(Frame(t + 4000, 4000));        // 4 s without a frame (alt-tab, loading screen)
        Feed(stats, 300, 13.3, t + 4000);
        var s = stats.Snapshot();
        Assert.Equal(0, s.Stutters);
        Assert.Equal(75, s.Low01!.Value, 0);
        Assert.Equal(75, s.MinFps!.Value, 0);
        Assert.Equal(75, s.AvgFps!.Value, 0);
    }

    [Fact]
    public void Fps_counts_every_frame_on_screen()
    {
        var stats = new FrameStats();
        double t = 0;
        for (int i = 0; i < 400; i++)
        {
            t += 5; // 200 FPS on screen, whatever made the frames
            stats.Add(Frame(t, 5));
        }
        Assert.Equal(200, stats.Snapshot().Fps!.Value, 1);
    }

    [Fact]
    public void Latency_is_frame_start_until_shown_and_skips_dropped_frames()
    {
        var stats = new FrameStats();
        double t = 0;
        for (int i = 0; i < 100; i++)
        {
            t += 10;
            // every other frame is dropped (never shown): it has no latency and mustn't pull the average down
            stats.Add(Frame(t, 10, cpu: 8, untilDisplayed: i % 2 == 0 ? 12 : null));
        }
        Assert.Equal(20.0, stats.Snapshot().LatencyMs!.Value);
    }

    [Fact]
    public void Old_frames_leave_the_60_second_window_but_stay_in_the_session()
    {
        var stats = new FrameStats();
        double t = Feed(stats, 100, 50); // 5 s at 20 FPS
        t = Feed(stats, 7000, 10, t);    // then 70 s at 100 FPS
        var s = stats.Snapshot();
        Assert.Equal(100, s.Low1!.Value, 1);         // the slow start is out of the window
        Assert.Equal(20, s.MinFps!.Value, 1);        // but the session remembers it
        Assert.Equal(100, s.MaxFps!.Value, 1);
        Assert.InRange(s.AvgFps!.Value, 90, 95);
        Assert.InRange(stats.Session().Low1!.Value, 19, 21);
    }

    [Fact]
    public void When_frames_stop_the_current_values_disappear()
    {
        long clock = 50_000;
        var stats = new FrameStats(() => clock);
        Feed(stats, 100, 10);
        Assert.NotNull(stats.Snapshot().Fps);

        clock += (long)FrameStats.StaleMs + 1; // no frames arrive for a while
        var s = stats.Snapshot();
        Assert.Null(s.Fps);
        Assert.Null(s.FrameTimeMs);
        Assert.NotNull(s.AvgFps); // session values stay
    }

    [Fact]
    public void Presentmons_time_column_doesnt_matter()
    {
        // The old bug: PresentMon counts from its own start, Kelvra compared with time since boot – every frame looked stale
        var stats = new FrameStats();
        double t = 0;
        for (int i = 0; i < 300; i++)
        {
            t += 10;
            stats.Add(Frame(i % 2 == 0 ? 3.0 : 9_000_000_000.0, 10)); // nonsense times
        }
        var s = stats.Snapshot();
        Assert.Equal(100, s.Fps!.Value, 1);
        Assert.Equal(3, s.SessionLength.TotalSeconds, 1);
    }

    [Fact]
    public void List_entries_match_game_names_and_install_folders_not_just_exe_names()
    {
        var dyingLight = new GameRules.AppInfo("DyingLightGame_x64_rwdi",
            @"E:\SteamLibrary\steamapps\common\Dying Light 2\ph\work\bin\x64\DyingLightGame_x64_rwdi.exe", "Dying Light 2", "Dying Light 2");
        var warThunder = new GameRules.AppInfo("aces", @"D:\Games\War Thunder\win64\aces.exe", "War Thunder (DirectX 11, 64bit)", "");
        var s = new AppSettings { GameAlways = { "Dying Light 2", "Warthunder" } };

        Assert.True(GameRules.IsAlways(dyingLight, s));
        Assert.True(GameRules.IsAlways(warThunder, s));    // "Warthunder" = folder "War Thunder"
        Assert.False(GameRules.Matches("Light", dyingLight)); // whole names only, no partial hits
        Assert.False(GameRules.Matches("", dyingLight));
        Assert.True(GameRules.Matches("dyinglightgame_x64_rwdi.exe", dyingLight));
        Assert.True(GameRules.IsNever(warThunder, new AppSettings { GameNever = { "war thunder" } }));
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Dying Light 2\bin\game.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteClient-Win64-Shipping.exe", true)]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", true)]
    [InlineData(@"C:\Program Files\Blender Foundation\Blender\blender.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    public void Store_libraries_count_as_games_even_windowed(string path, bool library) =>
        Assert.Equal(library, GameRules.IsInGameLibrary(path));

    [Fact]
    public void A_steady_fullscreen_or_library_or_listed_program_looks_like_a_game()
    {
        var plain = new GameRules.AppInfo("tool", @"C:\Tools\tool.exe", "Tool", "");
        var s = new AppSettings();
        Assert.False(GameRules.LooksLikeGame(plain, s, fullscreen: false, "Composed: Flip"));
        Assert.True(GameRules.LooksLikeGame(plain, s, fullscreen: true, "Composed: Flip"));
        Assert.True(GameRules.LooksLikeGame(plain, s, fullscreen: false, "Hardware: Independent Flip"));
        Assert.True(GameRules.LooksLikeGame(plain, new AppSettings { GameAlways = { "Tool" } }, false, "Composed: Flip"));
        Assert.True(GameRules.LooksLikeGame(plain with { Path = @"D:\SteamLibrary\steamapps\common\Tool\tool.exe" }, s, false, ""));
    }

    [Fact]
    public void The_busiest_swap_chain_is_followed()
    {
        var stats = new FrameStats();
        double t = 0;
        // A launcher swap chain draws first, then the game draws 10× as often
        for (int i = 0; i < 300; i++)
        {
            t += 10;
            stats.Add(Frame(t, 100, swap: 1));
            for (int k = 0; k < 10; k++) stats.Add(Frame(t + k, 10, swap: 2));
        }
        Assert.Equal(100, stats.Snapshot().Fps!.Value, 1);
    }

    [Fact]
    public void Graph_gets_the_recent_frames_with_stutters_marked()
    {
        var stats = new FrameStats();
        double t = Feed(stats, 2000, 10);
        t += 50;
        stats.Add(Frame(t, 50));
        var points = stats.Recent(10_000);
        Assert.InRange(points.Length, 990, 1001);
        Assert.True(points[^1].Stutter);
        Assert.All(points, p => Assert.True(p.TimeMs >= t - 10_000));
    }

    // ---------- which programs are games ----------

    [Theory]
    [InlineData("chrome", false)]
    [InlineData("Discord.exe", false)]
    [InlineData("explorer", false)]
    [InlineData("Cyberpunk2077", true)]
    [InlineData("eldenring.exe", true)]
    [InlineData("", false)]
    public void Known_non_games_are_ignored(string exe, bool candidate) =>
        Assert.Equal(candidate, GameRules.IsCandidate(App(exe), new AppSettings()));

    [Fact]
    public void Always_and_never_lists_override_the_rules()
    {
        var s = new AppSettings { GameAlways = { "chrome.exe" }, GameNever = { "Blender" } };
        Assert.True(GameRules.IsCandidate(App("chrome"), s));
        Assert.True(GameRules.IsAlways(App("Chrome.exe"), s));
        Assert.False(GameRules.IsCandidate(App("blender.exe"), s));
    }

    [Theory]
    [InlineData("Hardware: Independent Flip", true)]
    [InlineData("Hardware Composed: Independent Flip", true)]
    [InlineData("Hardware: Legacy Flip", true)]
    [InlineData("Composed: Flip", false)]
    [InlineData("", false)]
    public void Hardware_present_modes_mark_fullscreen_style_games(string mode, bool hardware) =>
        Assert.Equal(hardware, GameRules.IsHardwarePresentMode(mode));

    // ---------- Game sensors ----------

    [Fact]
    public void Game_sensors_read_like_other_sensors()
    {
        Assert.Equal("144 FPS", SensorFormat.Format(GameSensors.Fps, 143.6f));
        Assert.Equal("6.9 ms", SensorFormat.Format(GameSensors.Milliseconds, 6.94f));
        Assert.Equal("3", SensorFormat.Format(GameSensors.Count, 3));
        Assert.Equal("144", OverlayFormat.Value(GameSensors.Fps, 143.6f, -1, units: false));
        Assert.Equal("Game", SensorCategories.HardwareCategory(GameSensors.Hardware));
        Assert.Equal("Frames", SensorCategories.TypeCategory(GameSensors.Fps));
    }

    [Fact]
    public void Game_sensors_are_empty_without_a_game()
    {
        var stats = new FrameStats();
        double end = Feed(stats, 300, 10);
        var playing = GameSensors.Readings(stats.Snapshot(), gameActive: true);
        var idle = GameSensors.Readings(FrameSnapshot.Empty, gameActive: false);

        Assert.Equal(100, playing.Single(r => r.Id == GameSensors.FpsId).Value!.Value, 1);
        Assert.All(idle, r => Assert.Null(r.Value));
        Assert.Equal(playing.Select(r => r.Id), idle.Select(r => r.Id));
        Assert.All(playing, r => Assert.True(GameSensors.IsGameSensor(r.Id)));
    }

    [Fact]
    public void Bundled_presentmon_matches_the_build_hash() => Assert.True(PresentMonRunner.ReadBundledExe().Length > 100_000);

    // ---------- gaming overlay ----------

    private static readonly IReadOnlyList<SensorVm> NoSensors = Array.Empty<SensorVm>();

    private static GameOverlay.Module M(string key) => GameOverlay.Modules.Single(m => m.Key == key);

    [Fact]
    public void Modules_go_in_and_out_in_a_fixed_order()
    {
        var p = new OverlayProfile();
        GameOverlay.Set(p, M("graph"), true, NoSensors);
        GameOverlay.Set(p, M("fps"), true, NoSensors);
        GameOverlay.Set(p, M("name"), true, NoSensors);
        GameOverlay.Set(p, M("lows"), true, NoSensors);
        Assert.Equal(new[] { GameSensors.FpsId, GameSensors.Low1Id, GameSensors.Low01Id, GameOverlay.FrameTimesId, GameOverlay.NameId },
                     p.Items.Select(i => i.SensorId));

        GameOverlay.Set(p, M("lows"), false, NoSensors);
        Assert.False(GameOverlay.IsOn(p, M("lows"), NoSensors));
        Assert.True(GameOverlay.IsOn(p, M("graph"), NoSensors));
        Assert.Equal(3, p.Items.Count);

        GameOverlay.Set(p, M("fps"), true, NoSensors); // already on: no duplicate
        Assert.Single(p.Items, i => i.SensorId == GameSensors.FpsId);
    }

    [Fact]
    public void Module_items_stay_together_and_own_items_are_kept()
    {
        var p = new OverlayProfile();
        p.Items.Add(new OverlayItem { SensorId = "extra:time" });
        GameOverlay.Set(p, M("fps"), true, NoSensors);
        GameOverlay.Set(p, M("graph"), true, NoSensors);
        Assert.Equal(new[] { GameSensors.FpsId, GameOverlay.FrameTimesId, "extra:time" }, p.Items.Select(i => i.SensorId));
        GameOverlay.Set(p, M("fps"), false, NoSensors);
        Assert.Equal(new[] { GameOverlay.FrameTimesId, "extra:time" }, p.Items.Select(i => i.SensorId));
    }

    [Fact]
    public void Gaming_template_shows_only_in_games_with_the_default_modules()
    {
        var p = OverlayTemplates.Create("Gaming", NoSensors, "Gaming");
        Assert.True(p.ShowOnlyInGames);
        foreach (var key in GameOverlay.DefaultModules.Where(k => k != "hardware"))
            Assert.True(GameOverlay.IsOn(p, M(key), NoSensors), key);
        Assert.Equal("Frametime graph", OverlayExtras.NameOf(GameOverlay.FrameTimesId));
        Assert.Equal(GameOverlay.Group, OverlayExtras.GroupOf(GameOverlay.BoundId));
        Assert.Contains(OverlayExtras.PickerItems(), s => s.Id == GameOverlay.BoundId);
    }

    [Theory]
    [InlineData(true, true, true, true, true, true)]    // locked gaming overlay, game in front: shown
    [InlineData(true, true, true, true, false, false)]  // no game: hidden
    [InlineData(true, true, false, true, true, false)]  // gaming overlays hidden by their shortcut
    [InlineData(false, true, true, true, true, false)]  // all overlays hidden
    [InlineData(true, false, true, true, false, true)]  // unlocked: shown so it can be placed
    [InlineData(true, true, true, false, false, true)]  // a normal overlay doesn't care about games
    public void Gaming_overlays_follow_the_game(bool visible, bool locked, bool gameOverlaysVisible, bool gameOnly, bool gameInFront, bool shown)
    {
        var p = new OverlayProfile { Locked = locked, ShowOnlyInGames = gameOnly };
        Assert.Equal(shown, OverlayManager.Wanted(p, visible, gameOverlaysVisible, gameInFront));
    }

    [Fact]
    public void Gaming_overlay_setting_survives_export_and_import()
    {
        var p = OverlayTemplates.Create("Gaming", NoSensors, "Mine");
        var back = OverlayExchange.Import(OverlayExchange.Export(p));
        Assert.True(back.ShowOnlyInGames);
        Assert.Equal(p.Items.Select(i => i.SensorId), back.Items.Select(i => i.SensorId));
    }

    [Fact]
    public void Overlay_texts_describe_the_game()
    {
        var s = GameOverlay.Demo;
        Assert.Equal("GPU-bound · 97 %", GameOverlay.Text(GameOverlay.BoundId, s, "game"));
        Assert.Equal("138 · 64 – 165", GameOverlay.Text(GameOverlay.RangeId, s, "game"));
        Assert.Equal("3 · last 42 s ago", GameOverlay.Text(GameOverlay.StuttersId, s, "game"));
        Assert.Equal("eldenring", GameOverlay.Text(GameOverlay.NameId, s, "eldenring"));
        Assert.Equal("-", GameOverlay.Text(GameOverlay.BoundId, s, null)); // no game
        Assert.Equal("none", GameOverlay.Text(GameOverlay.StuttersId, s with { Stutters = 0 }, "game"));
        Assert.Equal("Capped (limiter / VSync)", GameOverlay.Text(GameOverlay.BoundId, s with { Bound = Bottleneck.Capped }, "game"));
        Assert.Equal("CPU-bound · GPU 97 %", GameOverlay.Text(GameOverlay.BoundId, s with { Bound = Bottleneck.CpuBound }, "game"));
    }

    [Theory]
    [InlineData("DyingLightGame_x64_rwdi", @"E:\SteamLibrary\steamapps\common\Dying Light 2\ph\work\bin\x64\DyingLightGame_x64_rwdi.exe", "", "", "Dying Light 2")]
    [InlineData("aces", @"D:\Games\War Thunder\win64\aces.exe", "War Thunder (DirectX 11, 64bit) - In battle", "", "War Thunder")]
    [InlineData("game", @"D:\Stuff\game.exe", "", "Some Game", "Some Game")]
    [InlineData("game", @"D:\Stuff\game.exe", "", "", "game")]
    [InlineData("x", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\x.exe", "Fortnite  ", "", "Fortnite")]
    public void Games_get_readable_names(string exe, string path, string title, string product, string expected) =>
        Assert.Equal(expected, GameRules.DisplayName(new GameRules.AppInfo(exe, path, title, product)));

    // ---------- game sessions ----------

    private static GameSessionSummary Summary(string game, bool benchmark = false) =>
        new(game, new DateTime(2026, 10, 8, 20, 0, 0), 2820, 138, 98, 71, 64, 165, 3, 2000, 500, 100, 78.5f, 71f, benchmark, null);

    [Fact]
    public void Sessions_are_kept_newest_first_up_to_the_limit()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sessions.jsonl");
        for (int i = 0; i < GameSessionLog.Keep + 5; i++) GameSessionLog.Append(Summary($"game{i}"), path);

        var list = GameSessionLog.Load(path);
        Assert.Equal(GameSessionLog.Keep, list.Count);
        Assert.Equal($"game{GameSessionLog.Keep + 4}", list[0].Game);
        Assert.Equal(Summary("x") with { Game = list[0].Game }, list[0]);
    }

    [Fact]
    public void A_broken_session_line_doesnt_lose_the_others()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sessions.jsonl");
        GameSessionLog.Append(Summary("one"), path);
        File.AppendAllText(path, "{ \"Game\": \"cut off\n");
        GameSessionLog.Append(Summary("two"), path);
        Assert.Equal(new[] { "two", "one" }, GameSessionLog.Load(path).Select(s => s.Game));
    }

    [Fact]
    public void Session_summary_reads_well()
    {
        var s = Summary("Cyberpunk2077", benchmark: true);
        Assert.Equal("Cyberpunk2077 · Benchmark", s.Title);
        Assert.Equal("Avg 138 FPS · 1% low 98 · 0.1% low 71", s.FpsLine);
        Assert.Contains("GPU-bound 76 % of the time", s.DetailLine);
        Assert.Contains("3 stutters", s.DetailLine);
        Assert.EndsWith("47 min", s.When);
    }

    [Fact]
    public void Summary_comes_from_the_frame_statistics()
    {
        var stats = new FrameStats();
        Feed(stats, 6000, 10, gpuFraction: 0.95, cpuFraction: 0.5); // 60 s at 100 FPS, GPU-bound
        var s = GameSessionSummary.From("game", DateTime.Now, stats, 80, 70);
        Assert.Equal(100, s.AvgFps!.Value, 1);
        Assert.InRange(s.Low1!.Value, 99, 101);
        Assert.InRange(s.GpuBoundSeconds, 58, 60);
        Assert.Equal(60, s.Seconds, 0);
    }

    [Fact]
    public void Benchmark_frames_are_written_as_csv()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sub", "bench.csv");
        GameSessionLog.WriteFrames(path, new[]
        {
            BenchFrame.From(Frame(1000, 10, cpu: 6, gpu: 9, untilDisplayed: 4)),
            BenchFrame.From(Frame(1010, 12.5, untilDisplayed: null)),
        });
        var lines = File.ReadAllLines(path);
        Assert.Equal("TimeMs,FrameTimeMs,FPS,CpuBusyMs,GpuBusyMs,LatencyMs,Displayed", lines[0]);
        Assert.Equal("0,10,100,6,9,10,1", lines[1]);
        Assert.Equal("12.5,12.5,80,,,,0", lines[2]);
    }

    [Fact]
    public void Low_fps_is_the_warning_not_high_fps()
    {
        var p = new OverlayProfile();
        var item = new OverlayItem { SensorId = GameSensors.FpsId };
        Assert.Equal(0, OverlayFormat.Level(144, p.Limits(item, GameSensors.Fps)));
        Assert.Equal(1, OverlayFormat.Level(45, p.Limits(item, GameSensors.Fps)));
        Assert.Equal(2, OverlayFormat.Level(25, p.Limits(item, GameSensors.Fps)));

        var own = new OverlayItem { SensorId = GameSensors.FpsId, WarnAt = 120, HotAt = 90 };
        Assert.Equal(1, OverlayFormat.Level(100, p.Limits(own, GameSensors.Fps)));
        Assert.Equal(0, OverlayFormat.Level(999, p.Limits(new OverlayItem(), SensorType.Clock)));
    }
}
