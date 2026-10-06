using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>
/// The "Copy diagnostics" text for bug reports: versions, rights, driver, detected hardware, Kelvra's own state and the
/// end of the log. The user name is replaced in paths, and nothing is sent anywhere: it only goes to the clipboard.
/// </summary>
public static class Diagnostics
{
    public static string Build(AppSettings settings, SensorStore sensors, IEnumerable<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Kelvra {typeof(Diagnostics).Assembly.GetName().Version?.ToString(3)} diagnostics · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Windows: {WindowsVersion()} · {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Administrator: {(SecureFiles.IsElevated ? "yes" : "no")}");
        sb.AppendLine($"PawnIO: {PawnIoInstaller.InstalledVersion?.ToString() ?? "not installed"}");
        sb.AppendLine($"Refresh: {settings.RefreshMs} ms · theme {settings.Theme} · {(settings.Fahrenheit ? "°F" : "°C")}");
        sb.AppendLine($"Overlays: {settings.Overlays.Count} ({settings.Overlays.Count(o => o.Enabled)} on) · alerts: {settings.Alerts.Count} · keep history: {settings.KeepHistory}");
        sb.AppendLine($"Hotkeys: {Show(settings.HotkeyOverlays)} / {Show(settings.HotkeyLock)}");
        foreach (var note in notes) sb.AppendLine(note);
        if (sensors.Error != null) sb.AppendLine($"Sensor error: {sensors.Error}");

        sb.AppendLine();
        sb.AppendLine($"Hardware ({sensors.All.Count} sensors):");
        foreach (var g in sensors.All.GroupBy(s => s.GroupName))
        {
            var first = g.First();
            int temps = g.Count(s => s.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature);
            sb.AppendLine($"  {g.Key} [{first.HardwareType}] · {g.Count()} sensors, {temps} temperatures");
        }

        sb.AppendLine();
        sb.AppendLine("Log (last 40 lines):");
        foreach (var line in Log.Tail(40)) sb.AppendLine("  " + line);
        return Anonymize(sb.ToString());
    }

    private static string Show(string hotkey) => hotkey.Length == 0 ? "off" : hotkey;

    /// <summary>C:\Users\Sam\… → %USERPROFILE%\… so a pasted report doesn't carry the account name.</summary>
    internal static string Anonymize(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        return user.Length > 2 ? text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase) : text;
    }

    private static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            int build = Environment.OSVersion.Version.Build;
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11"); // the registry still says 10 on 11
            return $"{product} {display} (build {build}.{key?.GetValue("UBR")})".Trim();
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
