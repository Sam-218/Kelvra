using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>
/// Detects and installs the PawnIO driver (https://pawnio.eu), which LibreHardwareMonitor needs
/// for CPU temperatures, voltages and fan speeds. The official signed installer is embedded at build time.
/// </summary>
public static class PawnIoInstaller
{
    public static readonly Version MinimumVersion = new(2, 0, 0, 0);
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>Installed version, read fresh from the registry (the library caches its own copy at startup).</summary>
    public static Version? InstalledVersion
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hklm.OpenSubKey(UninstallKey);
                    if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out var v)) return v;
                }
                catch
                {
                    // try the other view
                }
            }
            return null;
        }
    }

    public static bool IsInstalled => InstalledVersion is not null;

    public static bool NeedsInstall => InstalledVersion is not Version v || v < MinimumVersion;

    public static string StatusText => InstalledVersion switch
    {
        null => "Not installed – CPU temperatures, voltages and fan speeds are unavailable.",
        var v when v < MinimumVersion => $"Version {v} is outdated – please update.",
        var v => $"Version {v} installed.",
    };

    /// <summary>Runs the bundled installer and waits for it. Returns true if PawnIO is installed afterwards.</summary>
    public static async Task<bool> InstallAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Kelvra");
        string path = Path.Combine(dir, "PawnIO_setup.exe");

        try
        {
            Directory.CreateDirectory(dir);
            using (var resource = typeof(PawnIoInstaller).Assembly.GetManifestResourceStream("PawnIO_setup.exe"))
            {
                if (resource == null) throw new InvalidOperationException("The PawnIO installer is missing from this build.");
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
                await resource.CopyToAsync(file);
            }

            // UseShellExecute lets Windows show a UAC prompt if Kelvra itself isn't elevated.
            using var process = Process.Start(new ProcessStartInfo(path, "-install") { UseShellExecute = true });
            if (process != null) await process.WaitForExitAsync();
        }
        catch (Win32Exception)
        {
            // User declined the UAC prompt
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch { /* still in use or already gone */ }
        }

        return !NeedsInstall;
    }
}
