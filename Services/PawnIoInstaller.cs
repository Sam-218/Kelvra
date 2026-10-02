using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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
        byte[] setup = ReadBundledInstaller();
        // Admin-only folder + locked, re-checked file: no other program can swap the installer before it runs as admin
        string dir = SecureFiles.CreatePrivateFolder();
        string path = Path.Combine(dir, "PawnIO_setup.exe");

        try
        {
            Process? process;
            using (var locked = SecureFiles.WriteAndLock(path, setup))
            {
                // UseShellExecute lets Windows show a UAC prompt if Kelvra itself isn't elevated.
                process = Process.Start(new ProcessStartInfo(path, "-install") { UseShellExecute = true });
            }
            using (process)
            {
                if (process != null) await process.WaitForExitAsync();
            }
        }
        catch (Win32Exception)
        {
            // User declined the UAC prompt
            return false;
        }
        finally
        {
            SecureFiles.DeleteQuietly(dir);
        }

        return !NeedsInstall;
    }

    /// <summary>The embedded installer, checked against the SHA-256 the build verified (see Kelvra.csproj).</summary>
    internal static byte[] ReadBundledInstaller()
    {
        var assembly = typeof(PawnIoInstaller).Assembly;
        using var resource = assembly.GetManifestResourceStream("PawnIO_setup.exe")
                             ?? throw new InvalidOperationException("The PawnIO installer is missing from this build.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        string? expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "PawnIOSha256")?.Value;
        if (string.IsNullOrEmpty(expected) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bundled PawnIO installer failed its integrity check.");
        return bytes;
    }
}
