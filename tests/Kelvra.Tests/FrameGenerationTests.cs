namespace Kelvra.Tests;

/// <summary>FPS, lows, GPU busy and latency stay right with frame generation (DLSS / FSR in the game, AFMF / XeSS-FG in the driver).</summary>
public class FrameGenerationTests
{
    /// <param name="presentMs">Present to present.</param>
    /// <param name="shownMs">Screen update to screen update (null: never shown).</param>
    private static FrameSample F(double presentMs, double? shownMs, double? gpu = null, string type = "", double? cpu = 2,
                                 double? pcl = null, string mode = "Hardware: Independent Flip") =>
        new(42, 1, 0, mode, 0, presentMs, cpu, gpu, shownMs is null ? null : 5, shownMs, type, pcl);

    [Fact]
    public void Driver_frame_generation_counts_generated_frames_and_shows_the_rendered_rate()
    {
        // AMD Fluid Motion Frames at 60 → 120: a generated row, then the game's own, every 16.7 ms
        var stats = new FrameStats();
        for (int i = 0; i < 600; i++)
        {
            stats.Add(F(16.67, 8.335, gpu: 0, type: "AMD AFMF", cpu: 0));
            stats.Add(F(16.67, 8.335, gpu: 12, type: "Application"));
        }
        var s = stats.Snapshot();
        Assert.Equal(120, s.Fps!.Value, 0);
        Assert.Equal(60, s.RenderedFps!.Value, 0);
        Assert.Equal("AMD AFMF", s.FrameGeneration);
        Assert.Equal(120, s.Low1!.Value, 0);
        Assert.Equal(120, s.AvgFps!.Value, 0);
        Assert.Equal(72, s.GpuBusyPercent!.Value, 0); // 12 ms of GPU work per 16.7 ms, not per 8.3 ms
        Assert.Equal(10, s.SessionLength.TotalSeconds, 0); // generated rows don't make time run twice as fast
        Assert.Equal("AMD AFMF · 60 rendered", GameOverlay.Text(GameOverlay.FrameGenId, s, "game"));
    }

    [Fact]
    public void Frames_the_gpu_paces_itself_are_timed_by_the_screen_not_by_the_bunched_presents()
    {
        // DLSS 4 frame generation with flip metering: the game hands two frames over at once (1 ms apart, then 15.7 ms),
        // the GPU shows them 8.3 ms apart. It's a smooth 120 FPS; by presents the 1% low would read 64.
        var stats = new FrameStats();
        for (int i = 0; i < 2400; i++) stats.Add(F(i % 2 == 0 ? 1.0 : 15.67, 8.335 + (i % 3 - 1) * 0.05));
        var s = stats.Snapshot();
        Assert.True(s.PacedByDisplay);
        Assert.Equal(120, s.Fps!.Value, 0);
        Assert.Equal(8.3, s.FrameTimeMs!.Value, 1);
        Assert.InRange(s.Low1!.Value, 118, 122);
        Assert.Equal(0, s.Stutters);
        Assert.Null(s.FrameGeneration); // in-game frame generation isn't reported as such: its frames count like the game's own
    }

    [Fact]
    public void Ordinary_games_keep_present_to_present_frame_times()
    {
        // Tearing, every frame shown right away: the screen follows the presents, uneven frames included
        var tearing = new FrameStats();
        for (int i = 0; i < 1200; i++)
        {
            double ms = i % 3 == 0 ? 14 : 9;
            tearing.Add(F(ms, ms + (i % 2 == 0 ? 0.3 : -0.3)));
        }
        var t = tearing.Snapshot();
        Assert.False(t.PacedByDisplay);
        Assert.Equal(1000 / 14.0, t.Low1!.Value, 0);

        // A window at 300 FPS on a 144 Hz screen: most frames never reach it. FPS is what the game renders (like the game's own counter)
        var windowed = new FrameStats();
        for (int i = 0; i < 3000; i++) windowed.Add(F(3.33, i % 2 == 0 ? 6.94 : null, mode: "Composed: Flip"));
        var w = windowed.Snapshot();
        Assert.False(w.PacedByDisplay);
        Assert.Equal(300, w.Fps!.Value, 0);

        // VSync at 60 Hz on a 144 Hz-capable window: the screen's intervals are the jumpier ones – stay with the presents
        var vsync = new FrameStats();
        for (int i = 0; i < 1200; i++) vsync.Add(F(16.67 + (i % 2 == 0 ? 0.4 : -0.4), i % 2 == 0 ? 13.9 : 20.8, mode: "Composed: Flip"));
        Assert.False(vsync.Snapshot().PacedByDisplay);
    }

    [Fact]
    public void Pacing_decision_needs_a_clear_case_and_a_clearer_one_to_switch_back()
    {
        double[] bunched = Enumerable.Range(0, 120).Select(i => i % 2 == 0 ? 1.0 : 15.0).ToArray();
        double[] even = Enumerable.Repeat(8.0, 120).ToArray();
        double[] dropped = even.Select((v, i) => i % 4 == 0 ? double.NaN : v).ToArray();
        Assert.True(FrameStats.PacedByDisplay(bunched, even, now: false));
        Assert.False(FrameStats.PacedByDisplay(even, even, now: false));     // nothing to fix
        Assert.False(FrameStats.PacedByDisplay(bunched, dropped, now: false)); // a quarter never shown: presents it is
        Assert.False(FrameStats.PacedByDisplay(bunched, dropped, now: true));
        Assert.Equal(0, FrameStats.Variation(new[] { 5.0 }));
    }

    [Fact]
    public void Reflex_latency_is_preferred_and_not_mixed_with_estimates()
    {
        var stats = new FrameStats();
        for (int i = 0; i < 200; i++) stats.Add(F(10, 10, cpu: 4, pcl: i % 2 == 0 ? 31 : null)); // estimate: 4 + 5 = 9 ms
        Assert.Equal(31, stats.Snapshot().LatencyMs!.Value, 3);

        var noReflex = new FrameStats();
        for (int i = 0; i < 200; i++) noReflex.Add(F(10, 10, cpu: 4));
        Assert.Equal(9, noReflex.Snapshot().LatencyMs!.Value, 3);

        // A generated frame has no CPU time of its own: no estimate from it
        Assert.Null(F(16.7, 8.3, type: "Intel XeSS-FG", cpu: 0).LatencyMs);
        Assert.Null(F(16.7, null, pcl: 25).LatencyMs); // never shown: no latency
    }

    [Fact]
    public void Frame_type_display_change_and_reflex_columns_are_read()
    {
        // As PresentMon 2.6.0 writes them with --track_frame_type --track_pc_latency
        const string header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType," +
                              "TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency," +
                              "MsUntilDisplayed,MsPCLatency,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy," +
                              "MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";
        var csv = PresentMonCsv.FromHeader(header)!;
        var generated = csv.Parse("game.exe,42,0x10,DXGI,0,0,1,Hardware: Independent Flip,AMD AFMF,1000.5,NA,16.6,8.3,0.1,1.0,9.2,NA," +
                                  "990.1,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,NA,NA,NA,NA,NA")!;
        Assert.True(generated.Generated);
        Assert.Equal((16.6, 8.3, 0.0), (generated.FrameTimeMs, generated.DisplayedMs!.Value, generated.GpuBusyMs!.Value));

        var own = csv.Parse("game.exe,42,0x10,DXGI,0,0,1,Hardware: Independent Flip,Application,1000.5,NA,16.6,8.3,0.1,1.0,17.5,28.4," +
                            "990.1,16.6,6.0,10.6,0.5,14.0,13.1,0.9,NA,NA,NA,NA,NA")!;
        Assert.False(own.Generated);
        Assert.Equal(28.4, own.LatencyMs!.Value, 3);

        var dropped = csv.Parse("game.exe,42,0x10,DXGI,0,0,1,Composed: Flip,Application,1000.5,NA,3.3,NA,0.1,1.0,NA,NA," +
                                "990.1,3.3,2.0,1.3,0.5,3.0,2.9,0.1,NA,NA,NA,NA,NA")!;
        Assert.Null(dropped.DisplayedMs);
    }

    [Fact]
    public void Presentmon_is_asked_for_frame_types_and_reflex_latency_unless_it_choked_on_them()
    {
        var full = PresentMonRunner.Arguments("Kelvra.exe", tracking: true, frameTypes: true);
        Assert.Contains("--track_frame_type", full);
        Assert.Contains("--track_pc_latency", full);
        var plain = PresentMonRunner.Arguments("Kelvra.exe", tracking: false, frameTypes: true);
        Assert.DoesNotContain("--track_frame_type", plain);
        Assert.Equal(full.Length - 2, plain.Length);
    }

    [Fact]
    public void Frame_types_are_only_tracked_with_amd_or_intel_graphics()
    {
        // Tracking them makes PresentMon hold back every frame; only AMD's and Intel's driver frame generation is reported
        var nvidia = PresentMonRunner.Arguments("Kelvra.exe", tracking: true, frameTypes: false);
        Assert.DoesNotContain("--track_frame_type", nvidia);
        Assert.Contains("--track_pc_latency", nvidia);

        Assert.True(SystemInfo.IsAmdOrIntel(@"PCI\VEN_1002&DEV_744C&SUBSYS_0E3B1002&REV_C8\6&1A2B3C4D&0&00000019"));
        Assert.True(SystemInfo.IsAmdOrIntel(@"PCI\VEN_8086&DEV_A780&SUBSYS_7D251462&REV_04\3&11583659&0&10"));
        Assert.False(SystemInfo.IsAmdOrIntel(@"PCI\VEN_10DE&DEV_2882&SUBSYS_40EE1458&REV_A1\4&2D78AB8F&0&0008"));
        Assert.False(SystemInfo.IsAmdOrIntel(@"ROOT\BASICDISPLAY\0000"));
    }

    [Fact]
    public void Presentmon_queue_holds_the_frames_it_holds_back()
    {
        // PresentMon holds frames back up to 2 s; a smaller queue throws game frames away (Wardogs read 20 FPS instead of ~100)
        var args = PresentMonRunner.Arguments("Kelvra.exe", tracking: true, frameTypes: true);
        int size = int.Parse(args[Array.IndexOf(args, "--set_circular_buffer_size") + 1]);
        Assert.True(size >= 2048);
    }

    [Fact]
    public void Benchmark_csv_keeps_one_timeline_with_generated_frames()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "bench.csv");
        GameSessionLog.WriteFrames(path, new[]
        {
            BenchFrame.From(F(16.7, 8.3, gpu: 12, type: "Application")),
            BenchFrame.From(F(16.7, 8.3, gpu: 0, type: "AMD AFMF", cpu: 0)),
            BenchFrame.From(F(16.7, 8.4, gpu: 12, type: "Application")),
        });
        var times = File.ReadAllLines(path).Skip(1).Select(l => l.Split(',')[0]).ToArray();
        Assert.Equal(new[] { "0", "0", "16.7" }, times);
        Assert.EndsWith(",8.3,1", File.ReadAllLines(path)[2]);
    }
}
