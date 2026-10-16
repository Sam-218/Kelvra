using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>The thermal scale: Kelvra 2's only colours. Every temperature is drawn in the colour of its band.</summary>
public enum Heat { Cold, Fine, Warm, Hot, Critical }

public static class Thermal
{
    /// <summary>Upper ends of the bands (°C): below 40 cold, below 70 fine, below 82 warm, below 92 hot, else critical.</summary>
    public const double ColdBelow = 40, FineBelow = 70, WarmBelow = 82, HotBelow = 92;

    /// <summary>The chips' usual limits when no alert rule says otherwise (°C): where the headroom bar ends.</summary>
    public const double CpuLimit = 95, GpuLimit = 87;

    /// <summary>The band of a temperature. At or over <paramref name="limit"/> (the user's alert limit) it's critical too.</summary>
    public static Heat Of(double celsius, double? limit = null)
    {
        if (limit is double l && celsius >= l) return Heat.Critical;
        return celsius < ColdBelow ? Heat.Cold : celsius < FineBelow ? Heat.Fine : celsius < WarmBelow ? Heat.Warm
             : celsius < HotBelow ? Heat.Hot : Heat.Critical;
    }

    /// <summary>The theme brush for a band (from the current theme, so light and dark get their own shades).</summary>
    public static string BrushKey(Heat heat) => heat switch
    {
        Heat.Cold => "ColdBrush",
        Heat.Fine => "GoodBrush",
        Heat.Warm => "WarnBrush",
        Heat.Hot => "HotBrush",
        _ => "CritBrush",
    };

    public static Brush BrushFor(double celsius, double? limit = null) =>
        Application.Current?.TryFindResource(BrushKey(Of(celsius, limit))) as Brush ?? Brushes.Gray;

    /// <summary>"Normal", "Warm", "Hot", "Critical": the state pill next to a CPU or GPU.</summary>
    public static string StateWord(Heat heat) => heat switch
    {
        Heat.Warm => "Warm",
        Heat.Hot => "Hot",
        Heat.Critical => "Critical",
        _ => "Normal",
    };

    /// <summary>The user's own "above" alert limit for a sensor, else the chip's usual limit.</summary>
    public static double LimitFor(string sensorId, bool gpu, IEnumerable<AlertRule> alerts) =>
        alerts.Where(a => a.Enabled && a.Condition == AlertCondition.Above && a.SensorId == sensorId).Select(a => (double?)a.Threshold).Min()
        ?? (gpu ? GpuLimit : CpuLimit);
}

/// <summary>
/// Colour for a <see cref="Heat"/> (bind to <see cref="SensorVm.Heat"/>) or a temperature in °C: its thermal colour;
/// null (not a temperature) gets the normal text colour, or the muted one with ConverterParameter "muted".
/// ConverterParameter "state": a state pill's text (cold counts as fine: "Normal" is green); "soft": the pill's background.
/// </summary>
public sealed class HeatBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mode = parameter as string ?? "";
        Heat? heat = value switch
        {
            Heat h => h,
            double d when !double.IsNaN(d) => Thermal.Of(d),
            float f when !float.IsNaN(f) => Thermal.Of(f),
            _ => null,
        };
        string key = heat switch
        {
            null => mode == "muted" ? "MutedBrush" : "TextBrush",
            Heat h when mode == "soft" => h switch
            {
                Heat.Warm => "WarnSoftBrush",
                Heat.Hot => "HotSoftBrush",
                Heat.Critical => "CritSoftBrush",
                _ => "GoodSoftBrush",
            },
            Heat h when mode == "state" => Thermal.BrushKey(h == Heat.Cold ? Heat.Fine : h),
            Heat h => Thermal.BrushKey(h),
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
