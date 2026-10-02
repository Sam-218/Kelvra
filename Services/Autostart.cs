using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Kelvra;

/// <summary>
/// "Start with Windows". Kelvra needs admin rights, and Windows won't elevate apps from the
/// Run key at sign-in, so we register a Task Scheduler task that runs at logon with highest privileges.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Kelvra";
    private const string OldTaskName = "SysMonitor"; // name before the rename
    public const string MinimizedArg = "--minimized";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Kelvra.exe's current path (where the logon task would point).</summary>
    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Kelvra.exe");

    /// <summary>
    /// The task starts Kelvra as administrator without asking. That's only safe when normal programs can't replace
    /// Kelvra.exe (or drop a DLL next to it), e.g. in C:\Program Files\Kelvra, not in Downloads or on the Desktop.
    /// </summary>
    public static bool IsExeLocationSafe() => SecureFiles.IsAdminOnlyWritable(ExePath);

    public static string UnsafeLocationMessage =>
        $"Kelvra.exe is in a folder that normal programs can change:\n{Path.GetDirectoryName(ExePath)}\n\n" +
        "Starting it with Windows would run that file as administrator at every sign-in, so any program could replace it " +
        "and take over your PC. Move Kelvra.exe to a folder only administrators can change, e.g. C:\\Program Files\\Kelvra, " +
        "start it from there and turn this on again.";

    /// <summary>Creates or updates the logon task (also fixes the path if the exe moved). Refuses unsafe exe locations.</summary>
    public static bool Enable(out string error)
    {
        string exe = ExePath;
        if (!IsExeLocationSafe())
        {
            error = UnsafeLocationMessage;
            return false;
        }
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Kelvra in the tray when you sign in.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{MinimizedArg}</Arguments></Exec>
              </Actions>
            </Task>
            """;

        // Private folder + locked file: nothing can edit the XML (e.g. swap the command) before the admin task is created
        string dir = SecureFiles.CreatePrivateFolder();
        string file = Path.Combine(dir, "Kelvra-autostart.xml");
        try
        {
            byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)];
            using var locked = SecureFiles.WriteAndLock(file, bytes);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out error);
            if (code == 0) RemoveOldTask();
            return code == 0;
        }
        finally
        {
            SecureFiles.DeleteQuietly(dir);
        }
    }

    public static bool Disable(out string error)
    {
        RemoveOldTask();
        if (!IsEnabled())
        {
            error = "";
            return true;
        }
        return RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out error) == 0;
    }

    private static void RemoveOldTask()
    {
        if (RunSchtasks($"/Query /TN \"{OldTaskName}\"", out _) == 0)
            RunSchtasks($"/Delete /TN \"{OldTaskName}\" /F", out _);
    }

    private static int RunSchtasks(string args, out string error)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        error = p.StandardError.ReadToEnd().Trim();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}
