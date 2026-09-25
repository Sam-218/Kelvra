using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>Swaps the Dark/Light brush dictionary at runtime and follows the Windows setting in "System" mode.</summary>
public static class ThemeManager
{
    private static string _mode = "System";

    public static bool IsDark { get; private set; } = true;

    public static void Initialize(string mode)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == "System")
                Application.Current.Dispatcher.BeginInvoke(() => Apply(_mode));
        };
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        _mode = mode;
        bool dark = mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;

        var merged = Application.Current.Resources.MergedDictionaries;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        merged[0] = dict; // index 0 is always the colour dictionary (see App.xaml)

        foreach (Window w in Application.Current.Windows)
            ApplyTitleBar(w);
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }


    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}

public static class ColorUtil
{
    public static Color Parse(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                return c;
        }
        catch (FormatException)
        {
        }
        return fallback;
    }

    public static SolidColorBrush Brush(string? hex, Color fallback, byte? alpha = null)
    {
        var c = Parse(hex, fallback);
        if (alpha is byte a) c.A = a;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static bool IsValidHex(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
}
