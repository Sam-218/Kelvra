using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

public class AlertAndHistoryTests : IDisposable
{
    public void Dispose() => SensorFormat.UseFahrenheit = false;

    private static (SensorStore Store, SensorVm Hot) StoreWithHotCpu()
    {
        var store = new SensorStore();
        var hot = Data.Sensor("/cpu/temp/0", SensorType.Temperature, 90);
        store.All.Add(hot);
        store.ById[hot.Id] = hot;
        return (store, hot);
    }

    [Fact]
    public void Alerts_fire_once_per_cooldown_and_respect_enabled_and_condition()
    {
        var (store, hot) = StoreWithHotCpu();
        var settings = new AppSettings();
        var alerts = new AlertService();
        int fired = 0;
        alerts.Fired += _ => fired++;
        settings.Alerts.Add(new AlertRule { SensorId = hot.Id, Threshold = 85, DurationSeconds = 0, CooldownMinutes = 5 });
        settings.Alerts.Add(new AlertRule { SensorId = hot.Id, Threshold = 85, DurationSeconds = 0, Enabled = false });
        settings.Alerts.Add(new AlertRule { SensorId = hot.Id, Threshold = 95, Condition = AlertCondition.Below, DurationSeconds = 0 });
        settings.Alerts.Add(new AlertRule { SensorId = hot.Id, Threshold = 99 }); // not reached

        alerts.Evaluate(settings, store);
        alerts.Evaluate(settings, store);

        Assert.Equal(2, fired);
        Assert.Equal(2, alerts.Recent.Count);
        Assert.False(settings.Alerts[1].IsActive);
        Assert.False(settings.Alerts[3].IsActive);
        Assert.Equal(hot.ValueText, settings.Alerts[0].CurrentText);
    }

    [Fact]
    public void Alert_waits_for_the_duration()
    {
        var (store, hot) = StoreWithHotCpu();
        var settings = new AppSettings();
        settings.Alerts.Add(new AlertRule { SensorId = hot.Id, Threshold = 85, DurationSeconds = 60 });
        var alerts = new AlertService();
        int fired = 0;
        alerts.Fired += _ => fired++;
        alerts.Evaluate(settings, store);
        Assert.Equal(0, fired);
        Assert.True(settings.Alerts[0].IsActive);
    }

    [Fact]
    public void Alert_rule_limits_and_units()
    {
        Assert.Equal(3600, new AlertRule { DurationSeconds = 99999 }.DurationSeconds);
        Assert.Equal(0, new AlertRule { CooldownMinutes = -3 }.CooldownMinutes);
        var r = new AlertRule { Threshold = 85, SensorType = SensorType.Temperature };
        SensorFormat.UseFahrenheit = true;
        Assert.Equal(185f, r.ThresholdDisplay);
        r.ThresholdDisplay = 212;
        Assert.Equal(100f, r.Threshold, 3);
    }

    [Fact]
    public void History_keeps_one_sample_per_second_oldest_first()
    {
        var (store, hot) = StoreWithHotCpu();
        var history = new HistoryService();
        int sampled = 0;
        history.Sampled += () => sampled++;
        for (int i = 0; i < 3; i++)
        {
            history.Record(store);
            history.Record(store); // too soon: ignored
            Thread.Sleep(1000);
        }
        var series = new List<(DateTime Time, float Value)>();
        history.GetSeries(hot.Id, TimeSpan.FromMinutes(1), series);
        Assert.Equal(3, sampled);
        Assert.Equal(3, series.Count);
        Assert.True(series[0].Time < series[1].Time && series[1].Time < series[2].Time);
        Assert.All(series, s => Assert.Equal(90f, s.Value));
    }
}
