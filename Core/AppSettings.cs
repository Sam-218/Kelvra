using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvra;

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
    /// <summary>null = default order; otherwise "Name", "TypeLabel", "Value", "Min" or "Max".</summary>
    public string? SensorSortKey { get; set; }
    public bool SensorSortDescending { get; set; }
    public ObservableCollection<OverlayProfile> Overlays { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            ImportFromOldName();
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: fall back to defaults
        }
        return new AppSettings();
    }

    /// <summary>Kelvra used to be called SysMonitor: bring its settings over once.</summary>
    private static void ImportFromOldName()
    {
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor", "settings.json");
        if (File.Exists(FilePath) || !File.Exists(old)) return;
        Directory.CreateDirectory(Dir);
        File.Copy(old, FilePath);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Not fatal
        }
    }
}
