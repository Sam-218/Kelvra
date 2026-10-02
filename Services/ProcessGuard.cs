using System.Runtime.InteropServices;

namespace Kelvra;

/// <summary>
/// Processes Windows can't live without. Ending one as administrator crashes the PC (blue screen) or signs everyone out,
/// so the Apps page refuses instead of only warning about unsaved work.
/// </summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Secure System", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
    };

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// True for the System/Idle pseudo-processes, processes Windows flags as critical, and the core Windows processes by name.
    /// A name only counts when the exe lives in the Windows folder (or can't be read), so a fake "csrss.exe" can still be ended.
    /// </summary>
    public static bool IsCritical(int pid, string name, string? path)
    {
        if (pid <= 4) return true; // 0 = Idle, 4 = System
        if (CriticalNames.Contains(name) && (path is null || SystemPaths.IsSameOrInside(path, WindowsDir))) return true;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            return IsProcessCritical(h, out bool critical) && critical;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(IntPtr h, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}
