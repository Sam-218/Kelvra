using System.Diagnostics;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>A program that starts when you sign in (registry Run key, Startup folder or a scheduled task).</summary>
public sealed class StartupItem : ObservableObject
{
    private readonly Action<bool> _apply;
    private bool _enabled;

    /// <param name="apply">Turns the entry on or off for real (throws if it can't).</param>
    public StartupItem(string name, string command, string location, string? imagePath, bool enabled, Action<bool> apply)
    {
        Name = name;
        Command = command;
        Location = location;
        ImagePath = imagePath;
        _enabled = enabled;
        _apply = apply;
        Icon = ProcessMonitor.IconFor(imagePath);
        try
        {
            Publisher = imagePath != null && File.Exists(imagePath)
                ? FileVersionInfo.GetVersionInfo(imagePath).CompanyName?.Trim() ?? ""
                : "";
        }
        catch
        {
            Publisher = "";
        }
    }

    public string Name { get; }
    public string Command { get; }
    public string Location { get; }
    public string? ImagePath { get; }
    public string Publisher { get; }
    public string PublisherText => string.IsNullOrEmpty(Publisher) ? "Unknown publisher" : Publisher;
    public ImageSource? Icon { get; }

    /// <summary>Raised when switching an entry failed (the page shows the reason).</summary>
    public static event Action<StartupItem, string>? ChangeFailed;

    /// <summary>Setting this writes the same flag Task Manager uses (or enables/disables the scheduled task).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            try
            {
                _apply(value);
            }
            catch (Exception ex)
            {
                OnPropertyChanged(); // put the switch back
                Log.Warn($"Startup item \"{Name}\" couldn't be switched {(value ? "on" : "off")}: {ex.Message}");
                ChangeFailed?.Invoke(this, ex.Message);
                return;
            }
            Set(ref _enabled, value);
        }
    }
}

/// <summary>Reads and toggles startup entries exactly like Task Manager's "Startup apps".</summary>
public static class StartupManager
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    internal const string TaskLocation = "Scheduled task";

    /// <summary>
    /// All Run-key and Startup-folder entries (you and all users) plus scheduled tasks that run at sign-in, sorted by name.
    /// Reads the registry, disk and Task Scheduler: worker thread.
    /// </summary>
    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, Run, "Registry · you", Approved + "Run");
        ReadRunKey(items, Registry.LocalMachine, Run, "Registry · all users", Approved + "Run");
        ReadRunKey(items, Registry.LocalMachine, Run32, "Registry · all users (32-bit)", Approved + "Run32");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder · you");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder · all users");
        ReadScheduledTasks(items);
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- scheduled tasks ----------

    private const int TriggerLogon = 9;   // TASK_TRIGGER_LOGON
    private const int ActionExec = 0;     // TASK_ACTION_EXEC
    private const int EnumHidden = 1;     // TASK_ENUM_HIDDEN

    /// <summary>
    /// Tasks with a "run at log on" trigger (how updaters and admin tools start, invisible to the Run key).
    /// Windows' own tasks under \Microsoft are left out (switching those off can break Windows), and so is Kelvra's
    /// own task, which Settings → Start with Windows manages. Uses the Task Scheduler COM API, which ships with Windows.
    /// </summary>
    private static void ReadScheduledTasks(List<StartupItem> items)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            ReadTaskFolder(items, service.GetFolder("\\"), depth: 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading scheduled tasks failed: " + ex.Message);
        }
    }

    private static void ReadTaskFolder(List<StartupItem> items, dynamic folder, int depth)
    {
        if (depth > 8) return;
        string folderPath = folder.Path;
        if (folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

        foreach (dynamic task in folder.GetTasks(EnumHidden))
        {
            try
            {
                if (string.Equals((string)task.Path, "\\" + Autostart.TaskName, StringComparison.OrdinalIgnoreCase)) continue;
                dynamic definition = task.Definition;
                if (!HasLogonTrigger(definition)) continue;

                string? exe = null, command = "";
                foreach (dynamic action in definition.Actions)
                {
                    if ((int)action.Type != ActionExec) continue;
                    exe = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? "").Trim('"'));
                    command = $"\"{exe}\" {(string?)action.Arguments}".Trim();
                    break;
                }

                string path = task.Path;
                items.Add(new StartupItem((string)task.Name, command, $"{TaskLocation} · {path}", exe,
                    (bool)task.Enabled, enabled => SetTaskEnabled(path, enabled)));
            }
            catch (Exception ex)
            {
                Log.Warn("Skipping a scheduled task: " + ex.Message); // no access to this one
            }
        }

        foreach (dynamic sub in folder.GetFolders(0))
            ReadTaskFolder(items, sub, depth + 1);
    }

    /// <summary>Fresh connection on the calling (UI) thread: the objects from <see cref="Load"/> belong to a worker thread.</summary>
    private static void SetTaskEnabled(string path, bool enabled)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        int slash = path.LastIndexOf('\\');
        dynamic folder = service.GetFolder(slash <= 0 ? "\\" : path[..slash]);
        dynamic task = folder.GetTask(path[(slash + 1)..]);
        task.Enabled = enabled;
    }

    private static bool HasLogonTrigger(dynamic definition)
    {
        foreach (dynamic trigger in definition.Triggers)
            if ((int)trigger.Type == TriggerLogon) return true;
        return false;
    }

    private static void ReadRunKey(List<StartupItem> items, RegistryKey hive, string path, string location, string approvedKey)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string command) continue;
                items.Add(new StartupItem(name, command, location, ExeFromCommand(command),
                    IsApproved(hive, approvedKey, name), on => SetApproved(hive, approvedKey, name, on)));
            }
        }
        catch
        {
            // no access to this key
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, RegistryKey hive, string location)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file;
            items.Add(new StartupItem(Path.GetFileNameWithoutExtension(file), target, location, target,
                IsApproved(hive, Approved + "StartupFolder", fileName), on => SetApproved(hive, Approved + "StartupFolder", fileName, on)));
        }
    }

    /// <summary>No flag or an even first byte = enabled; odd first byte (0x03, 0x07…) = disabled.</summary>
    private static bool IsApproved(RegistryKey hive, string key, string value)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(value) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Writes Task Manager's 12-byte flag: byte 0 = 0x02 on / 0x03 off, bytes 4–11 = when it was turned off (FILETIME).</summary>
    internal static void SetApproved(RegistryKey hive, string key, string value, bool enabled)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        using var k = hive.CreateSubKey(key, writable: true);
        k.SetValue(value, data, RegistryValueKind.Binary);
    }

    /// <summary>Pulls the program path out of a command line like "C:\x\app.exe" --background.</summary>
    public static string? ExeFromCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }

    /// <summary>Resolves a .lnk via the WScript.Shell COM object (no extra dependency). Null if it can't be read.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }
}
