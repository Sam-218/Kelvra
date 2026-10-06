using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>°C stored, °C or °F shown (follows Settings → Temperature unit).</summary>
public sealed class TemperatureConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is float c ? Math.Round(SensorFormat.ToDisplay(SensorType.Temperature, c)) : DependencyProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? SensorFormat.FromDisplay(SensorType.Temperature, (float)d) : DependencyProperty.UnsetValue;
}

/// <summary>Shows a number with a suffix, or a word for 0 ("Auto", "Off"): parameter = "suffix|zero word".</summary>
public sealed class NumberLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "").Split('|');
        double v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (v == 0 && parts.Length > 1) return parts[1];
        return v.ToString(v % 1 == 0 ? "0" : "0.0#", CultureInfo.CurrentCulture) + parts[0];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true when the value equals the parameter (for showing parts of the editor, e.g. Columns only in Grid layout).</summary>
public sealed class MatchVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
