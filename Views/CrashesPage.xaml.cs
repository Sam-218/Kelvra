using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>A crash as the Crashes page shows it.</summary>
public sealed record CrashView(string Icon, Brush Brush, string Title, string When, string Culprit, string Reason, string Advice, string Context)
{
    public bool HasContext => Context.Length > 0;

    /// <summary>"Before it: CPU 72 °C · GPU 83 °C · GPU load 98 % · GPU 310 W · Cyberpunk 2077 in front" from the stored note.</summary>
    public static string Describe(string? context)
    {
        if (string.IsNullOrEmpty(context)) return "";
        var parts = new List<string>();
        foreach (var pair in context.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = pair[..eq], value = pair[(eq + 1)..];
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v);
            parts.Add(key switch
            {
                HistoryRoles.CpuTemp => $"CPU {SensorFormat.Format(SensorType.Temperature, v)}",
                HistoryRoles.GpuTemp => $"GPU {SensorFormat.Format(SensorType.Temperature, v)}",
                HistoryRoles.GpuLoad => $"GPU load {v:0} %",
                HistoryRoles.GpuPower => $"GPU {v:0} W",
                "fg" => $"{value} in front",
                _ => "",
            });
        }
        parts.RemoveAll(p => p.Length == 0);
        return parts.Count == 0 ? "" : "In the 5 minutes before (highest): " + string.Join(" · ", parts);
    }
}

/// <summary>
/// Crashes from the history (see <see cref="CrashWatcher"/>): newest first, each in plain words with what to try and what
/// the hardware was doing just before. When one culprit is behind several recent problems, a bar says so.
/// </summary>
public partial class CrashesPage : UserControl
{
    private readonly App _app = App.Current;
    private List<CrashRecord> _all = new();

    public CrashesPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = LoadAsync();
        };
        if (_app.Crashes is { } watcher)
            watcher.Added += found => Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible) _ = LoadAsync();
            });
    }

    private async Task LoadAsync()
    {
        if (_app.Recorder is not { } recorder)
        {
            Show(new(), "Long-term history isn't available, so crashes can't be listed (see the log in Settings → About).");
            return;
        }
        _all = await recorder.ReadAsync(db => db.Crashes(0, long.MaxValue));
        Show(_all, null);
    }

    private void Show(List<CrashRecord> crashes, string? message)
    {
        IEnumerable<CrashRecord> shown = crashes;
        if (ShowApps.IsChecked == true) shown = crashes.Where(c => c.Kind is CrashKinds.AppCrash or CrashKinds.AppHang);
        else if (ShowSystem.IsChecked == true) shown = crashes.Where(c => c.Kind is not (CrashKinds.AppCrash or CrashKinds.AppHang));

        var views = shown.Take(300).Select(ToView).ToList();
        List.ItemsSource = views;
        EmptyText.Text = message ?? (crashes.Count == 0
            ? "No crashes recorded – Kelvra looked through the last 30 days of Windows' logs, and keeps watching."
            : "None of this kind.");
        EmptyText.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = views.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var pattern = Pattern(crashes, DateTime.Now);
        PatternText.Text = pattern ?? "";
        PatternBar.Visibility = pattern != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// "NVIDIA driver is behind 4 of the 6 problems in the last 30 days." when one culprit stands out (3 or more). Only
    /// culprits you can act on: not freezes, and not the Windows parts every crashing app passes through (ntdll, KernelBase).
    /// </summary>
    internal static string? Pattern(IReadOnlyList<CrashRecord> crashes, DateTime now)
    {
        long since = HistoryDb.Unix(now.AddDays(-30));
        var recent = crashes.Where(c => c.Ts >= since).Select(c => (c.Kind, CrashExplainer.Explain(c).Culprit)).ToList();
        var top = recent.Where(r => r.Kind != CrashKinds.AppHang && r.Culprit is not "Unknown" && !r.Culprit.StartsWith("Windows ("))
                        .GroupBy(r => r.Culprit).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (top == null || top.Count() < 3) return null;
        return $"{top.Key} is behind {top.Count()} of the {recent.Count} problems in the last 30 days. Start with that one's advice below.";
    }

    private CrashView ToView(CrashRecord c)
    {
        var ex = CrashExplainer.Explain(c);
        var time = HistoryDb.Local(c.Ts);
        string when = c.Detail == "time unknown"
            ? $"Noticed when the PC started again, {time.ToString("f", CultureInfo.CurrentCulture)} (Kelvra wasn't running, so when it went down isn't known)"
            : time.ToString("f", CultureInfo.CurrentCulture);
        if (c.Kind == CrashKinds.AppCrash && c.Detail is { Length: > 0 } path) when += $" · {path}";
        if (c.Kind == CrashKinds.BlueScreen && c.Code != null) when += $" · stop code {c.Code}";
        string icon = c.Kind switch
        {
            CrashKinds.AppHang => "",      // stopwatch
            CrashKinds.DriverReset => "",  // display
            CrashKinds.BlueScreen => "",   // error
            CrashKinds.Shutdown => "",     // power
            _ => "",                       // warning
        };
        var brush = (Brush)FindResource(c.Kind is CrashKinds.BlueScreen or CrashKinds.Shutdown ? "CritBrush" : "WarnBrush");
        return new CrashView(icon, brush, ex.Title, when, ex.Culprit, ex.Reason, ex.Advice, CrashView.Describe(c.Context));
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Show(_all, null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Crashes is { } watcher) await watcher.ScanAsync();
        await LoadAsync();
    }
}
