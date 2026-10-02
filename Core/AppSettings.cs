using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvra;

/// <summary>Everything the user can change, saved as JSON in %APPDATA%\Kelvra\settings.json. New properties need a default.</summary>
public sealed class AppSettings
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kelvra");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>"System", "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";
    public int RefreshMs { get; set; } = 1000;
    public bool OverlaysVisible { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; }
    public bool DefaultsApplied { get; set; }
    public bool PawnIoPromptDismissed { get; set; }

    public bool Fahrenheit { get; set; }
    public bool StartWithWindows { get; set; }

    // Alerts
    public ObservableCollection<AlertRule> Alerts { get; set; } = new();
    public bool AlertSound { get; set; } = true;

    // History page
    /// <summary>"All", "Category" or "Selected".</summary>
    public string HistoryMode { get; set; } = "Category";
    /// <summary>"hw:&lt;hardware category&gt;" or "type:&lt;sensor-type category&gt;".</summary>
    public string HistoryCategory { get; set; } = "hw:CPU";
    public List<string> HistorySelected { get; set; } = new();
    public int HistoryMinutes { get; set; } = 5;
    public int LogIntervalSeconds { get; set; } = 1;

    // Disk cleanup / duplicates
    /// <summary>Cleanup items the user unticked. The Recycle Bin starts unticked.</summary>
    public List<string> CleanupUnchecked { get; set; } = new() { "recycle" };
    public int DuplicateMinSizeMb { get; set; } = 1;

    // Sensors page view state
    public List<string> CollapsedGroups { get; set; } = new();
    /// <summary>User-chosen device order (hardware IDs). Devices not listed follow in detection order.</summary>
    public List<string> GroupOrder { get; set; } = new();
    /// <summary>User-chosen System page card order (card titles). Cards not listed follow in default order.</summary>
    public List<string> SystemCardOrder { get; set; } = new();
    /// <summary>null = default order; otherwise "Name", "TypeLabel", "Value", "Min" or "Max".</summary>
    public string? SensorSortKey { get; set; }
    public bool SensorSortDescending { get; set; }
    public ObservableCollection<OverlayProfile> Overlays { get; set; } = new();

    /// <summary>
    /// Set when settings.json couldn't be read. The broken file was renamed to this path instead of being overwritten,
    /// so overlays and alerts can still be rescued from it by hand.
    /// </summary>
    [JsonIgnore] public string? RecoveredFrom { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            ImportFromOldName();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old settings can't be copied: start fresh
        }
        return Load(FilePath);
    }

    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            loaded.Repair();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings(); // unreadable right now (locked?): not corrupt, so leave the file alone
        }
        catch (Exception)
        {
            // Corrupt settings: keep them aside (the next save would otherwise overwrite them) and start with defaults
            var fresh = new AppSettings();
            try
            {
                string backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, backup);
                fresh.RecoveredFrom = backup;
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Couldn't rename it either: defaults, as before
            }
            return fresh;
        }
    }

    /// <summary>
    /// Fixes values a hand-edited or old file could contain: a refresh outside the slider's range (a negative one used to
    /// stop sensor polling, 0 polled non-stop), missing lists (JSON null), and history/log/size choices the UI doesn't offer.
    /// </summary>
    internal void Repair()
    {
        RefreshMs = Math.Clamp(RefreshMs, 250, 5000);
        if (HistoryMinutes is not (1 or 5 or 15 or 60)) HistoryMinutes = 5;
        if (LogIntervalSeconds is not (1 or 5 or 10 or 60)) LogIntervalSeconds = 1;
        if (DuplicateMinSizeMb is not (1 or 10 or 100)) DuplicateMinSizeMb = 1;
        Theme ??= "System";
        HistoryMode ??= "Category";
        HistoryCategory ??= "hw:CPU";
        Alerts ??= new();
        Overlays ??= new();
        HistorySelected ??= new();
        CleanupUnchecked ??= new();
        CollapsedGroups ??= new();
        GroupOrder ??= new();
        SystemCardOrder ??= new();
        RemoveNulls(Alerts);
        RemoveNulls(Overlays);
    }

    private static void RemoveNulls<T>(ObservableCollection<T> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] is null) list.RemoveAt(i);
    }

    /// <summary>Kelvra used to be called SysMonitor: bring its settings over once.</summary>
    private static void ImportFromOldName()
    {
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor", "settings.json");
        if (File.Exists(FilePath) || !File.Exists(old)) return;
        Directory.CreateDirectory(Dir);
        File.Copy(old, FilePath);
    }

    public void Save() => Save(FilePath);

    internal void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write a temp file, then swap it in: a crash mid-write can't leave a half-written settings.json
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Not fatal
        }
    }
}
