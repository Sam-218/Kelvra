using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace Kelvra;

/// <summary>The change kinds on the Changes page.</summary>
public static class ChangeKinds
{
    public const string Driver = "driver", Bios = "bios", Windows = "windows", Update = "update", App = "app", Startup = "startup";
}

/// <summary>
/// Notices what changed on the PC: graphics drivers, the BIOS, the Windows build, apps installed, removed or updated, and
/// startup items. A snapshot is taken now and then (at start, then every 6 hours) and set against the last one; the first
/// is only the baseline. Windows Update's own log adds each update by name, at the time it was installed.
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    private const string SnapshotKey = "changes.snapshot", UpdateKey = "changes.wu.rid";

    private readonly HistoryDb _db;
    private readonly SemaphoreSlim _scan = new(1, 1);
    private Timer? _timer;

    public ChangeWatcher(HistoryDb db) => _db = db;

    /// <summary>New changes were found (raised on a worker thread).</summary>
    public event Action<IReadOnlyList<ChangeRecord>>? Added;

    public void Start() => _timer = new Timer(_ => _ = ScanAsync(), null, TimeSpan.FromSeconds(40), TimeSpan.FromHours(6));

    public async Task ScanAsync()
    {
        if (!await _scan.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await Task.Run(Scan).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Looking for changes", ex);
        }
        finally
        {
            _scan.Release();
        }
    }

    private void Scan()
    {
        var found = new List<ChangeRecord>();
        long now = HistoryDb.Unix(DateTime.Now);
        var current = Snapshot();
        if (_db.GetMeta(SnapshotKey) is string json)
        {
            try
            {
                var before = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
                if (before != null) found.AddRange(Diff(before, current, now));
            }
            catch (JsonException ex)
            {
                Log.Error("Reading the last change snapshot", ex);
            }
        }
        _db.SetMeta(SnapshotKey, JsonSerializer.Serialize(current));
        ReadWindowsUpdates(found);
        if (found.Count == 0) return;
        _db.InsertChanges(found);
        Added?.Invoke(found);
    }

    /// <summary>What's installed and how the PC is set up, by kind → subject → value. A kind that couldn't be read is left out.</summary>
    internal static Dictionary<string, Dictionary<string, string>> Snapshot()
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        void Add(string kind, Func<Dictionary<string, string>> read)
        {
            try
            {
                var items = read();
                if (items.Count > 0) all[kind] = items;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Reading {kind} for the change list", ex);
            }
        }

        Add(ChangeKinds.Driver, () => SystemInfo.GpuDrivers().GroupBy(g => g.Adapter).ToDictionary(g => g.Key, g => g.First().Version));
        Add(ChangeKinds.Bios, () => Values(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", ("BIOS", "BIOSVersion")));
        Add(ChangeKinds.Windows, () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null) return new();
            string version = $"{key.GetValue("DisplayVersion")} (build {key.GetValue("CurrentBuild")}.{key.GetValue("UBR")})".Trim();
            return new() { ["Windows"] = version };
        });
        Add(ChangeKinds.App, InstalledApps);
        Add(ChangeKinds.Startup, () => StartupManager.Load().GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                                        .ToDictionary(g => g.Key, g => g.First().Enabled ? "on" : "off"));
        return all;
    }

    /// <summary>Apps in Apps &amp; features, by name → version (Windows' own components and updates left out).</summary>
    private static Dictionary<string, string> InstalledApps()
    {
        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var list = root.OpenSubKey(uninstall);
            if (list == null) continue;
            foreach (string sub in list.GetSubKeyNames())
            {
                using var app = list.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string name || name.Trim().Length == 0) continue;
                if (app.GetValue("SystemComponent") is int system && system == 1) continue;
                if (app.GetValue("ParentKeyName") != null) continue; // an update of another entry
                apps[name.Trim()] = (app.GetValue("DisplayVersion") as string)?.Trim() ?? "";
            }
        }
        return apps;
    }

    private static Dictionary<string, string> Values(RegistryKey hive, string path, params (string Subject, string Value)[] values)
    {
        using var key = hive.OpenSubKey(path);
        var map = new Dictionary<string, string>();
        foreach (var (subject, value) in values)
            if (key?.GetValue(value)?.ToString()?.Trim() is { Length: > 0 } v) map[subject] = v;
        return map;
    }

    /// <summary>
    /// What differs between two snapshots: for list kinds (apps, startup) items added and removed, and for every kind
    /// values that changed. A kind missing on either side is skipped, so a failed read isn't taken as everything removed.
    /// </summary>
    internal static List<ChangeRecord> Diff(Dictionary<string, Dictionary<string, string>> before,
                                            Dictionary<string, Dictionary<string, string>> after, long ts)
    {
        var changes = new List<ChangeRecord>();
        foreach (var (kind, now) in after)
        {
            if (!before.TryGetValue(kind, out var was)) continue;
            bool list = kind is ChangeKinds.App or ChangeKinds.Startup;
            foreach (var (subject, value) in now.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!was.TryGetValue(subject, out var old))
                {
                    if (list) changes.Add(new ChangeRecord(ts, kind, subject, null, value));
                }
                else if (!string.Equals(old, value, StringComparison.Ordinal)) changes.Add(new ChangeRecord(ts, kind, subject, old, value));
            }
            if (list)
                foreach (var (subject, old) in was.Where(x => !now.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    changes.Add(new ChangeRecord(ts, kind, subject, old, null));
        }
        return changes;
    }

    /// <summary>
    /// Windows updates installed, from the Setup log (Servicing event 2: "Package KB… was successfully changed to the
    /// Installed state"; Windows 11 no longer logs installs in WindowsUpdateClient). Newer than the last record read; the
    /// first time, 30 days. Data: [0] package, [2] state, [3] result.
    /// </summary>
    private void ReadWindowsUpdates(List<ChangeRecord> found)
    {
        long? last = long.TryParse(_db.GetMeta(UpdateKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rid) ? rid : null;
        string when = last is long id ? $"EventRecordID > {id}" : "TimeCreated[timediff(@SystemTime) <= 2592000000]";
        long newest = last ?? 0;
        try
        {
            var query = new EventLogQuery("Setup", PathType.LogName, $"*[System[({when}) and Provider[@Name='Microsoft-Windows-Servicing'] and (EventID=2)]]");
            using var reader = new EventLogReader(query);
            for (var r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
            {
                using (r)
                {
                    if (r.RecordId is long recordId) newest = Math.Max(newest, recordId);
                    if (r.TimeCreated is not DateTime time || r.Properties.Count < 4) continue;
                    string package = r.Properties[0].Value?.ToString()?.Trim() ?? "";
                    if (package.StartsWith("KB", StringComparison.OrdinalIgnoreCase) &&
                        r.Properties[2].Value?.ToString() == "Installed" && r.Properties[3].Value?.ToString() is "0x0" or "0")
                        found.Add(new ChangeRecord(HistoryDb.Unix(time), ChangeKinds.Update, package, null, null));
                }
            }
        }
        catch (EventLogException ex)
        {
            Log.Error("Reading Windows' Setup log", ex);
        }
        _db.SetMeta(UpdateKey, newest.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
