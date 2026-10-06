using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

/// <summary>Fan curve maths and the controller's safety rules; overlay formatting, styles, anchoring, copying; accents.</summary>
public class FanAndOverlayTests : IDisposable
{
    public void Dispose() => SensorFormat.UseFahrenheit = false;

    // ---------- fan curve ----------

    private static ObservableCollection<FanPoint> Points(params (float T, float P)[] pts) =>
        new(pts.Select(p => new FanPoint { Temp = p.T, Percent = p.P }));

    [Theory]
    [InlineData(20, 30)]   // below the first point: flat
    [InlineData(35, 30)]
    [InlineData(45, 35)]   // halfway between 35→30 and 55→40
    [InlineData(70, 65)]
    [InlineData(95, 100)]  // after the last point: flat
    public void Curve_interpolates_between_points(float temp, float expected) =>
        Assert.Equal(expected, FanCurve.Evaluate(FanCurve.DefaultPoints(), temp), 3);

    [Fact]
    public void Curve_order_of_points_does_not_matter() =>
        Assert.Equal(50f, FanCurve.Evaluate(Points((80, 100), (40, 0)), 60), 3);

    [Fact]
    public void Broken_curves_are_repaired_on_load()
    {
        using var dir = new TempDir();
        string json = "{ \"Fans\": [ { \"ControlId\": \"/lpc/0/control/1\", \"Mode\": \"Curve\", \"Points\": [ { \"Temp\": 900, \"Percent\": -5 } ] }," +
                      " { \"ControlId\": \"/lpc/0/control/1\" }, null, { \"ControlId\": \"\" } ] }";
        var s = AppSettings.Load(dir.File("settings.json", System.Text.Encoding.UTF8.GetBytes(json)));
        var fan = Assert.Single(s.Fans);                 // duplicates, nulls and empty ids dropped
        Assert.Equal(4, fan.Points.Count);               // one point isn't a curve: defaults
        Assert.All(fan.Points, p => Assert.InRange(p.Percent, 0, 100));
    }

    // ---------- fan controller ----------

    private sealed class Rig
    {
        public readonly SensorStore Store = new();
        public readonly SensorVm Temp;
        public readonly SensorVm Control;
        public readonly List<(string Id, float? Percent)> Calls = new();
        public readonly FanController Controller = new();
        public readonly FanProfile Profile;

        public Rig(float temp, FanMode mode = FanMode.Curve)
        {
            Temp = Add(new SensorReading("/cpu/0/temperature/0", "/cpu/0", "CPU", HardwareType.Cpu, "Package", SensorType.Temperature, temp, temp, temp));
            Control = Add(new SensorReading("/lpc/0/control/1", "/mb", "Board", HardwareType.Motherboard, "Fan #1", SensorType.Control, 40, 40, 40) { Controllable = true });
            Profile = new FanProfile { ControlId = Control.Id, SourceId = Temp.Id, Mode = mode, MinPercent = 20, RampDown = 100, Hysteresis = 0 };
        }

        private SensorVm Add(SensorReading r)
        {
            var vm = new SensorVm(r, r.HardwareName);
            Store.All.Add(vm);
            Store.ById[vm.Id] = vm;
            return vm;
        }

        public void SetTemp(float t) => Temp.Update(new SensorReading(Temp.Id, "/cpu/0", "CPU", HardwareType.Cpu, "Package", SensorType.Temperature, t, t, t));

        public float? Tick(double seconds = 1)
        {
            Controller.Tick(new[] { Profile }, Store, seconds, (id, p) => Calls.Add((id, p)));
            return Calls.Count > 0 ? Calls[^1].Percent : null;
        }
    }

    [Fact]
    public void Automatic_fans_are_never_touched()
    {
        var rig = new Rig(80, FanMode.Auto);
        rig.Tick();
        rig.Tick();
        Assert.Empty(rig.Calls);
        Assert.Contains("Automatic", rig.Profile.Status);
    }

    [Fact]
    public void Curve_follows_the_temperature_and_respects_the_minimum()
    {
        var rig = new Rig(70);
        Assert.Equal(65f, rig.Tick()!.Value, 1);
        rig.SetTemp(20);
        Assert.Equal(30f, rig.Tick()!.Value, 1); // curve says 30
        rig.Profile.MinPercent = 50;
        Assert.Equal(50f, rig.Tick()!.Value, 1);  // the floor wins
    }

    [Fact]
    public void Critical_temperature_means_full_speed_even_on_a_low_curve()
    {
        var rig = new Rig(95);
        rig.Profile.Points = Points((20, 10), (100, 20));
        Assert.Equal(100f, rig.Tick());

        var fixedRig = new Rig(95, FanMode.Fixed);
        fixedRig.Profile.FixedPercent = 25;
        Assert.Equal(100f, fixedRig.Tick());
    }

    [Fact]
    public void Missing_temperature_sensor_hands_the_fan_back()
    {
        var rig = new Rig(70);
        rig.Tick();
        rig.Profile.SourceId = "/gone/0";
        rig.Tick();
        Assert.Null(rig.Calls[^1].Percent);
        Assert.Contains("automatic", rig.Profile.Status);
    }

    [Fact]
    public void Fans_slow_down_gradually_but_speed_up_at_once()
    {
        var rig = new Rig(85); // 100 %
        rig.Profile.RampDown = 5;
        Assert.Equal(100f, rig.Tick());
        rig.SetTemp(20);                         // curve wants 30 %
        Assert.Equal(95f, rig.Tick(1)!.Value, 1); // -5 %/s
        Assert.Equal(85f, rig.Tick(2)!.Value, 1);
        rig.SetTemp(85);
        Assert.Equal(100f, rig.Tick(1));          // up: immediately
    }

    [Fact]
    public void Hysteresis_stops_the_fan_pulsing()
    {
        var rig = new Rig(70);
        rig.Profile.Hysteresis = 3;
        float first = rig.Tick()!.Value;
        rig.SetTemp(68);                  // 2 °C drop: stays
        rig.Tick();
        Assert.Equal(first, rig.Calls[^1].Percent!.Value, 1);
        rig.SetTemp(66);                  // 4 °C drop: follows the curve down
        rig.Tick();
        Assert.True(rig.Calls[^1].Percent < first);
    }

    [Fact]
    public void Removing_a_profile_returns_its_fan_to_automatic()
    {
        var rig = new Rig(70);
        rig.Tick();
        rig.Controller.Tick(Array.Empty<FanProfile>(), rig.Store, 1, (id, p) => rig.Calls.Add((id, p)));
        Assert.Equal((rig.Control.Id, (float?)null), rig.Calls[^1]);
    }

    [Fact]
    public void Outputs_that_cant_be_controlled_are_left_alone()
    {
        var rig = new Rig(70);
        rig.Profile.ControlId = "/not/controllable";
        rig.Tick();
        Assert.Empty(rig.Calls);
        Assert.Contains("can't be controlled", rig.Profile.Status);
    }

    // ---------- overlays ----------

    [Theory]
    [InlineData(SensorType.Temperature, 47.26f, -1, true, "47 °C")]
    [InlineData(SensorType.Temperature, 47.26f, -1, false, "47")]
    [InlineData(SensorType.Temperature, 47.26f, 1, true, "47.3 °C")]
    [InlineData(SensorType.Load, 7.5f, 2, false, "7.50")]
    [InlineData(SensorType.Throughput, 1572864f, -1, true, "1.5 MB/s")]
    [InlineData(SensorType.Clock, null, 0, true, "-")]
    public void Overlay_values_follow_decimals_and_units(SensorType type, float? raw, int decimals, bool units, string expected) =>
        Assert.Equal(expected, OverlayFormat.Value(type, raw, decimals, units));

    [Fact]
    public void Overlay_items_can_have_their_own_warning_limits()
    {
        var p = new OverlayProfile { WarmTemp = 60, HotTemp = 80 };
        var normal = new OverlayItem();
        var own = new OverlayItem { WarnAt = 40, HotAt = 50 };
        Assert.Equal(1, OverlayFormat.Level(65, p.Limits(normal, SensorType.Temperature)));
        Assert.Equal(2, OverlayFormat.Level(65, p.Limits(own, SensorType.Temperature)));
        Assert.Equal(0, OverlayFormat.Level(999, p.Limits(normal, SensorType.Clock))); // clocks never warn by default
        Assert.Equal(2, OverlayFormat.Level(4600, p.Limits(new OverlayItem { HotAt = 4500 }, SensorType.Clock)));
    }

    [Fact]
    public void Styles_change_the_look_but_not_the_sensors_or_position()
    {
        foreach (var style in OverlayStyles.All)
        {
            var p = new OverlayProfile { X = 300, Y = 200, Anchor = OverlayAnchor.BottomRight };
            p.Items.Add(new OverlayItem { SensorId = "/cpu/0/load/0", Label = "CPU" });
            style.Apply(p);
            Assert.Equal((300d, 200d, OverlayAnchor.BottomRight), (p.X, p.Y, p.Anchor));
            Assert.Equal("CPU", Assert.Single(p.Items).Label);
            Assert.True(ColorUtil.IsValidHex(p.ValueColor) && ColorUtil.IsValidHex(p.BackgroundColor), style.Name);
        }
    }

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 20, 20)]
    [InlineData(OverlayAnchor.TopRight, 1920 - 200 - 20, 20)]
    [InlineData(OverlayAnchor.BottomCenter, (1920 - 200) / 2, 1080 - 100 - 20)]
    [InlineData(OverlayAnchor.MiddleLeft, 20, (1080 - 100) / 2)]
    public void Anchored_overlays_land_in_the_right_place(OverlayAnchor anchor, int x, int y) =>
        Assert.Equal((x, y), OverlayWindow.AnchorPosition(anchor, 0, 0, 1920, 1080, 200, 100, 20));

    [Fact]
    public void Anchoring_works_on_a_second_monitor_left_of_the_main_one() =>
        Assert.Equal((-2560 + 2560 - 300 - 10, 1440 - 50 - 10), OverlayWindow.AnchorPosition(OverlayAnchor.BottomRight, -2560, 0, 2560, 1440, 300, 50, 10));

    [Fact]
    public void Duplicating_an_overlay_copies_every_setting()
    {
        var p = new OverlayProfile { Name = "A", FontFamily = "Consolas", ValueScale = 1.5, Layout = OverlayLayout.Grid, Columns = 3, BorderThickness = 2, Anchor = OverlayAnchor.TopRight };
        p.Items.Add(new OverlayItem { SensorId = "s", ShowGraph = true, Decimals = 2, ValueColor = "#123456", HotAt = 90, SensorName = "Shown name" });
        var c = p.Clone("B");
        Assert.Equal(("B", "Consolas", 1.5, OverlayLayout.Grid, 3, 2, OverlayAnchor.TopRight), (c.Name, c.FontFamily, c.ValueScale, c.Layout, c.Columns, c.BorderThickness, c.Anchor));
        var item = Assert.Single(c.Items);
        Assert.Equal((true, 2, "#123456", (float?)90, "Shown name"), (item.ShowGraph, item.Decimals, item.ValueColor, item.HotAt, item.SensorName));
        Assert.NotEqual(p.Id, c.Id);
    }

    [Fact]
    public void Imported_overlays_check_the_new_options_too()
    {
        string json = "{\"Kind\":\"kelvra-overlay\",\"Version\":1,\"Overlay\":{\"Name\":\"X\",\"Layout\":\"Grid\",\"Columns\":99,\"ValueScale\":50," +
                      "\"BorderColor\":\"url(evil)\",\"WarmColor\":\"#00FF00\",\"TextEffect\":\"Outline\",\"Anchor\":\"BottomRight\",\"Monitor\":\"\\\\\\\\.\\\\DISPLAY2\"," +
                      "\"Items\":[{\"SensorId\":\"a\",\"ValueColor\":\"javascript:\",\"Decimals\":9,\"ShowGraph\":true}]}}";
        var p = OverlayExchange.Import(json);
        Assert.Equal((6, 3d, new OverlayProfile().BorderColor, "#00FF00", OverlayTextEffect.Outline, OverlayAnchor.BottomRight),
                     (p.Columns, p.ValueScale, p.BorderColor, p.WarmColor, p.TextEffect, p.Anchor));
        var item = Assert.Single(p.Items);
        Assert.Equal(((string?)null, 3, true), (item.ValueColor, item.Decimals, item.ShowGraph));
    }

    // ---------- accents ----------

    [Fact]
    public void Unknown_accents_fall_back_to_the_default()
    {
        using var dir = new TempDir();
        Assert.Equal(Accents.Default, AppSettings.Load(dir.File("s.json", "{ \"Accent\": \"Chartreuse\" }"u8.ToArray())).Accent);
        Assert.Equal("Teal", AppSettings.Load(dir.File("t.json", "{ \"Accent\": \"Teal\" }"u8.ToArray())).Accent);
        Assert.Equal("Amber", Accents.Find(null).Name);
    }
}
