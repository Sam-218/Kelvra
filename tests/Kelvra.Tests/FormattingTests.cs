using LibreHardwareMonitor.Hardware;

namespace Kelvra.Tests;

public class FormattingTests : IDisposable
{
    public void Dispose() => SensorFormat.UseFahrenheit = false;

    [Theory]
    [InlineData(SensorType.Temperature, 47.4f, "47 °C")]
    [InlineData(SensorType.Load, 99.6f, "100 %")]
    [InlineData(SensorType.Voltage, 1.23456f, "1.235 V")]
    [InlineData(SensorType.Power, 65.04f, "65.0 W")]
    [InlineData(SensorType.Clock, 4650.4f, "4650 MHz")]
    [InlineData(SensorType.Fan, 997f, "997 RPM")]
    [InlineData(SensorType.Throughput, 1.5f * 1024 * 1024, "1.5 MB/s")]
    [InlineData(SensorType.Throughput, 2048f, "2 KB/s")]
    [InlineData(SensorType.Throughput, 100f, "100 B/s")]
    [InlineData(SensorType.TimeSpan, 3600f, "01:00:00")]
    public void Formats_values_with_units(SensorType type, float value, string expected) =>
        Assert.Equal(expected, SensorFormat.Format(type, value));

    [Fact]
    public void Missing_values_show_a_dash()
    {
        Assert.Equal("-", SensorFormat.Format(SensorType.Load, null));
        Assert.Equal("-", SensorFormat.Format(SensorType.Load, float.NaN));
    }

    [Theory] // FS-01
    [InlineData(25 * 3600f, "25:00:00")]
    [InlineData(90061f, "25:01:01")]
    [InlineData(86400f, "24:00:00")]
    [InlineData(59f, "00:00:59")]
    [InlineData(-3600f, "01:00:00")] // sign dropped, as before
    public void Durations_of_a_day_or_more_do_not_wrap(float seconds, string expected) =>
        Assert.Equal(expected, SensorFormat.Format(SensorType.TimeSpan, seconds));

    [Fact]
    public void Fahrenheit_is_display_only()
    {
        SensorFormat.UseFahrenheit = true;
        Assert.Equal("212 °F", SensorFormat.Format(SensorType.Temperature, 100f));
        Assert.Equal(85f, SensorFormat.FromDisplay(SensorType.Temperature, SensorFormat.ToDisplay(SensorType.Temperature, 85f)), 3);
        Assert.Equal("50 %", SensorFormat.Format(SensorType.Load, 50f)); // other types untouched
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    [InlineData(long.MaxValue, "8192 PB")]
    public void Formats_byte_counts(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Format(bytes));
}
