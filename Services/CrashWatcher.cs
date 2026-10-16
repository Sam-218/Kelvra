using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace Kelvra;

/// <summary>
/// Reads crashes from the Windows event logs into the history: app crashes (Application Error 1000) and freezes
/// (Application Hang 1002), graphics driver resets (Display 4101), blue screens and unexpected shutdowns (Kernel-Power 41,
/// with the stop code from BugCheck 1001). Only records newer than the last one read are asked for (by record id), every
/// 2 minutes and on demand. The first time, the last 30 days.
/// When the PC went down is taken from Kelvra's own history (the last minute it recorded), not parsed from Windows'
/// localized message text. Each crash keeps a note of what the hardware did in the minutes before it.
/// </summary>
public sealed class CrashWatcher : IDisposable
{
    private const string AppKey = "crash.app.rid", SystemKey = "crash.sys.rid";
    private const long FirstDaysMs = 30L * 24 * 3600 * 1000;

    private readonly HistoryDb _db;
    private readonly SemaphoreSlim _scan = new(1, 1);
    private Timer? _timer;

    public CrashWatcher(HistoryDb db) => _db = db;

    /// <summary>New crashes were added (raised on a worker thread).</summary>
    public event Action<IReadOnlyList<CrashRecord>>? Added;

    public void Start() => _timer = new Timer(_ => _ = ScanAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(2));

    /// <summary>Reads what's new now (also used right after a game closes, to see whether it crashed).</summary>
    public async Task ScanAsync()
    {
        if (!await _scan.WaitAsync(0).ConfigureAwait(false))
        {
            await _scan.WaitAsync().ConfigureAwait(false); // one is running: wait for it, then it has read everything
            _scan.Release();
            return;
        }
        try
        {
            await Task.Run(Scan).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Reading crashes from the event log", ex);
        }
        finally
        {
            _scan.Release();
        }
    }

    private void Scan()
    {
        var found = new List<CrashRecord>();
        ReadApplication(found);
        ReadSystem(found);
        if (found.Count == 0) return;
        var withContext = found.Select(c => c with { Context = ContextBefore(c) }).ToList();
        if (_db.InsertCrashes(withContext) > 0) Added?.Invoke(withContext);
    }

    private void ReadApplication(List<CrashRecord> found)
    {
        const string events = "(EventID=1000 and Provider[@Name='Application Error']) or (EventID=1002 and Provider[@Name='Application Hang'])";
        foreach (var r in Query("Application", AppKey, events))
        {
            using (r)
            {
                if (r.TimeCreated is not DateTime time) continue;
                string app = Prop(r, 0) ?? "";
                if (r.Id == 1000)
                {
                    string? code = r.Properties.Count > 6 ? CodeText(r.Properties[6].Value) : null;
                    found.Add(new CrashRecord(HistoryDb.Unix(time), CrashKinds.AppCrash, app, Prop(r, 3), code, Prop(r, 10)));
                }
                else found.Add(new CrashRecord(HistoryDb.Unix(time), CrashKinds.AppHang, app, null, null, null));
            }
        }
    }

    private void ReadSystem(List<CrashRecord> found)
    {
        const string events = "(EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power']) or (EventID=4101 and Provider[@Name='Display'])" +
                              " or (EventID=1001 and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'])";
        var boots = new List<(DateTime Logged, uint Bugcheck)>();
        var stopCodes = new List<(DateTime Logged, string Text)>();
        foreach (var r in Query("System", SystemKey, events))
        {
            using (r)
            {
                if (r.TimeCreated is not DateTime time) continue;
                switch (r.Id)
                {
                    case 4101:
                        found.Add(new CrashRecord(HistoryDb.Unix(time), CrashKinds.DriverReset, "", Prop(r, 0), null, null));
                        break;
                    case 41:
                        uint.TryParse(Named(r, "BugcheckCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint bug);
                        boots.Add((time, bug));
                        break;
                    case 1001 when Prop(r, 0) is { } text:
                        stopCodes.Add((time, text));
                        break;
                }
            }
        }

        // Kernel-Power 41 is written at the next start; BugCheck 1001 a little after it, with the stop code
        foreach (var (logged, bug) in boots)
        {
            string? stop = stopCodes.Where(s => Math.Abs((s.Logged - logged).TotalMinutes) < 15)
                                    .OrderBy(s => Math.Abs((s.Logged - logged).TotalSeconds)).Select(s => s.Text).FirstOrDefault();
            uint? code = bug != 0 ? bug : CrashExplainer.ParseBugcheck(stop);
            long boot = HistoryDb.Unix(logged);
            // When it went down: the last minute Kelvra recorded before this start (if it ran within the 2 days before)
            long down = _db.LastMinuteBefore(boot) is long last && boot - last < 2 * 86400 ? last + 59 : boot;
            found.Add(code is uint c && c != 0
                ? new CrashRecord(down, CrashKinds.BlueScreen, "", null, $"0x{c:X}", stop)
                : new CrashRecord(down, CrashKinds.Shutdown, "", null, null, down == boot ? "time unknown" : null));
        }
    }

    /// <summary>
    /// The hardware in the 5 minutes before a crash, from the history: the highest CPU and GPU temperatures, GPU load and
    /// power, and the app in front. Stored as "role=value;…" (°C, %, W) and worded when shown (see <see cref="CrashView"/>).
    /// </summary>
    private string? ContextBefore(CrashRecord c)
    {
        var parts = new List<string>();
        foreach (var s in _db.Sensors().Where(s => s.Role is HistoryRoles.CpuTemp or HistoryRoles.GpuTemp or HistoryRoles.GpuLoad or HistoryRoles.GpuPower))
        {
            var minutes = _db.Minutes(s.Id, c.Ts - 300, c.Ts + 60);
            if (minutes.Count > 0) parts.Add($"{s.Role}={minutes.Max(m => m.Max).ToString("0", CultureInfo.InvariantCulture)}");
        }
        var fg = _db.ForegroundMinutes(c.Ts - 300, c.Ts + 60);
        if (fg.Count > 0 && _db.AppNames().TryGetValue(fg.MaxBy(x => x.Key).Value, out var name)) parts.Add($"fg={name.Replace(";", ",")}");
        return parts.Count > 0 ? string.Join(";", parts) : null;
    }

    /// <summary>Records of <paramref name="events"/> newer than the last one read from <paramref name="log"/> (remembered under <paramref name="key"/>).</summary>
    private IEnumerable<EventRecord> Query(string log, string key, string events)
    {
        long? last = long.TryParse(_db.GetMeta(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rid) ? rid : null;
        string when = last is long id ? $"EventRecordID > {id}" : $"TimeCreated[timediff(@SystemTime) <= {FirstDaysMs}]";
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[({when}) and ({events})]]");
        long newest = last ?? 0;
        using var reader = new EventLogReader(query);
        while (true)
        {
            EventRecord? r;
            try { r = reader.ReadEvent(); }
            catch (EventLogException ex)
            {
                Log.Error($"Reading the {log} log", ex);
                break;
            }
            if (r == null) break;
            if (r.RecordId is long recordId) newest = Math.Max(newest, recordId);
            yield return r;
        }
        if (newest > 0) _db.SetMeta(key, newest.ToString(CultureInfo.InvariantCulture));
        else if (last == null) _db.SetMeta(key, "0");
    }

    private static string? Prop(EventRecord r, int index)
    {
        try
        {
            return index < r.Properties.Count ? r.Properties[index].Value?.ToString()?.Trim() : null;
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static string? CodeText(object? value) => value switch
    {
        uint u => u.ToString("x8", CultureInfo.InvariantCulture),
        int i => unchecked((uint)i).ToString("x8", CultureInfo.InvariantCulture),
        _ => CrashExplainer.NormalizeCode(value?.ToString()),
    };

    /// <summary>A named value of the event's data (Kernel-Power 41's BugcheckCode), read from its XML.</summary>
    private static string? Named(EventRecord r, string name)
    {
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            return XElement.Parse(r.ToXml()).Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or EventLogException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
