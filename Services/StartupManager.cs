using System.Diagnostics;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>A program that starts when you sign in (registry Run key or Startup folder).</summary>
public sealed class StartupItem : ObservableObject
{
    private bool _enabled;

    public StartupItem(string name, string command, string location, string? imagePath,
                       RegistryKey approvedHive, string approvedKey, string approvedValue, bool enabled)
    {
        Name = name;
        Command = command;
        Location = location;
        ImagePath = imagePath;
        ApprovedHive = approvedHive;
        ApprovedKey = approvedKey;
        ApprovedValue = approvedValue;
        _enabled = enabled;
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

    internal RegistryKey ApprovedHive { get; }
    internal string ApprovedKey { get; }
    internal string ApprovedValue { get; }

    /// <summary>Setting this writes the same "StartupApproved" flag Task Manager uses.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            try
            {
                StartupManager.SetApproved(this, value);
            }
            catch (Exception ex)
            {
                OnPropertyChanged(); // put the switch back
                System.Windows.MessageBox.Show($"Couldn't change “{Name}”:\n{ex.Message}", "Kelvra",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, Run, "Registry · you", Approved + "Run");
        ReadRunKey(items, Registry.LocalMachine, Run, "Registry · all users", Approved + "Run");
        ReadRunKey(items, Registry.LocalMachine, Run32, "Registry · all users (32-bit)", Approved + "Run32");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder · you");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder · all users");
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
                items.Add(new StartupItem(name, command, location, ExeFromCommand(command), hive, approvedKey, name,
                    IsApproved(hive, approvedKey, name)));
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
            items.Add(new StartupItem(Path.GetFileNameWithoutExtension(file), target, location, target, hive,
                Approved + "StartupFolder", fileName, IsApproved(hive, Approved + "StartupFolder", fileName)));
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

    internal static void SetApproved(StartupItem item, bool enabled)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        using var k = item.ApprovedHive.CreateSubKey(item.ApprovedKey, writable: true);
        k.SetValue(item.ApprovedValue, data, RegistryValueKind.Binary);
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
