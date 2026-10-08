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
    /// <summary>One of <see cref="Accents.All"/> by name.</summary>
    public string Accent { get; set; } = Accents.Default;
    public int RefreshMs { get; set; } = 1000;
    public bool OverlaysVisible { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; }
    /// <summary>The minimise button hides Kelvra in the tray instead of leaving it on the taskbar.</summary>
    public bool MinimizeToTray { get; set; }
    public bool DefaultsApplied { get; set; }
    public bool PawnIoPromptDismissed { get; set; }

    public bool Fahrenheit { get; set; }
    public bool StartWithWindows { get; set; }

    // Gaming (Settings → Gaming)
    /// <summary>Run PresentMon to measure FPS and frame times in games.</summary>
    public bool GameFpsEnabled { get; set; } = true;
    /// <summary>Exe names that always count as games (e.g. windowed games), and ones that never do.</summary>
    public List<string> GameAlways { get; set; } = new();
    public List<string> GameNever { get; set; } = new();
    /// <summary>Overlays set to "only while a game is running" are shown (their own hotkey toggles this).</summary>
    public bool GameOverlaysVisible { get; set; } = true;

    // Updates (Settings → About)
    /// <summary>Ask GitHub once a day whether a newer Kelvra release exists.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The release the user answered "Not now" to, and when: it's offered again 24 h later (a newer one at once).</summary>
    public string? UpdateDeclinedVersion { get; set; }
    public DateTime? UpdateDeclinedAtUtc { get; set; }

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
    /// <summary>Save the last 24 h of one-minute history to disk, so graphs survive a restart.</summary>
    public bool KeepHistory { get; set; }

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

    /// <summary>Fan outputs Kelvra drives (Fans page). Fans without a profile stay under BIOS control.</summary>
    public List<FanProfile> Fans { get; set; } = new();

    // Mini window (null position = next to the main window's screen corner)
    public bool MiniOpen { get; set; }
    public bool MiniTopmost { get; set; } = true;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public double MiniWidth { get; set; } = 340;
    public double MiniHeight { get; set; } = 620;

    // Global shortcuts ("" = off). The old Ctrl+Shift+O/L took those keys away from Chrome, Excel and VS Code.
    public string HotkeyOverlays { get; set; } = Hotkey.DefaultOverlays;
    public string HotkeyLock { get; set; } = Hotkey.DefaultLock;
    /// <summary>Shows/hides the gaming overlays only.</summary>
    public string HotkeyGameOverlays { get; set; } = Hotkey.DefaultGameOverlays;
    /// <summary>Starts/stops a benchmark recording of the running game.</summary>
    public string HotkeyBenchmark { get; set; } = Hotkey.DefaultBenchmark;

    /// <summary>Why the last save failed (null = it worked). The app tells the user once per new error.</summary>
    [JsonIgnore] public string? LastSaveError { get; private set; }

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
        if (HistoryMinutes is not (1 or 5 or 15 or 60 or 360 or 1440)) HistoryMinutes = 5;
        if (HotkeyOverlays is null || (HotkeyOverlays.Length > 0 && !Hotkey.TryParse(HotkeyOverlays, out _))) HotkeyOverlays = Hotkey.DefaultOverlays;
        if (HotkeyLock is null || (HotkeyLock.Length > 0 && !Hotkey.TryParse(HotkeyLock, out _))) HotkeyLock = Hotkey.DefaultLock;
        if (HotkeyGameOverlays is null || (HotkeyGameOverlays.Length > 0 && !Hotkey.TryParse(HotkeyGameOverlays, out _))) HotkeyGameOverlays = Hotkey.DefaultGameOverlays;
        if (HotkeyBenchmark is null || (HotkeyBenchmark.Length > 0 && !Hotkey.TryParse(HotkeyBenchmark, out _))) HotkeyBenchmark = Hotkey.DefaultBenchmark;
        if (LogIntervalSeconds is not (1 or 5 or 10 or 60)) LogIntervalSeconds = 1;
        if (DuplicateMinSizeMb is not (1 or 10 or 100)) DuplicateMinSizeMb = 1;
        Theme ??= "System";
        if (!Accents.IsKnown(Accent)) Accent = Accents.Default;
        HistoryMode ??= "Category";
        HistoryCategory ??= "hw:CPU";
        Alerts ??= new();
        Overlays ??= new();
        HistorySelected ??= new();
        CleanupUnchecked ??= new();
        CollapsedGroups ??= new();
        GroupOrder ??= new();
        SystemCardOrder ??= new();
        GameAlways = (GameAlways ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GameNever = (GameNever ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RemoveNulls(Alerts);
        RemoveNulls(Overlays);
        Fans = (Fans ?? new()).Where(f => f != null && !string.IsNullOrEmpty(f.ControlId)).GroupBy(f => f.ControlId).Select(g => g.First()).ToList();
        foreach (var f in Fans) FanCurve.Repair(f);
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

    /// <summary>Writes settings.json. Returns false (and sets <see cref="LastSaveError"/>) if it couldn't.</summary>
    public bool Save() => Save(FilePath);

    internal bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write a temp file, then swap it in: a crash mid-write can't leave a half-written settings.json
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            LastSaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Not fatal, but not silent either: the caller tells the user, and the log keeps the details
            if (LastSaveError != ex.Message) Log.Error($"Saving settings to {path}", ex);
            LastSaveError = ex.Message;
            return false;
        }
    }

    /// <summary>The same JSON options settings.json uses (overlay export/import shares them).</summary>
    internal static JsonSerializerOptions Json => JsonOptions;
}
