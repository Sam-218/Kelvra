using Microsoft.Data.Sqlite;

namespace Kelvra;

/// <summary>One sensor as the history knows it. <see cref="Role"/> marks the ones reports look for ("cpu-temp", "fan" …).</summary>
public sealed record HistorySensor(long Id, string Key, string Name, string Hardware, int Kind, string Role);

/// <summary>A sensor's minute: the average, lowest and highest reading, and the samples behind it (for the day totals).</summary>
public readonly record struct MinuteValue(long Sensor, double Sum, int N, double Min, double Max)
{
    public double Avg => N > 0 ? Sum / N : double.NaN;
}

/// <summary>What one app did in a minute: time in front, the temperatures while it was, and its internet use.</summary>
public sealed record AppSlice(long App, double FgSec, double CpuTempSum, int CpuTempN, double? CpuTempMax,
                              double GpuTempSum, int GpuTempN, double? GpuTempMax, long NetDown, long NetUp);

/// <summary>A fan's speed at a duty (10 % steps) during a minute, for spotting a fan that slows down over the months.</summary>
public readonly record struct FanDutySample(long Fan, int Duty, double RpmSum, int N);

/// <summary>Everything recorded in one minute, written in one transaction.</summary>
public sealed record MinuteBatch(long Ts, long Day, List<MinuteValue> Sensors, List<AppSlice> Apps, long? ForegroundApp, List<FanDutySample> Fans);

/// <summary>A sensor's day: the sum and count of its readings (for the average), and the lowest and highest.</summary>
public sealed record DayStat(long Day, double Sum, int N, double Min, double Max)
{
    public double Avg => N > 0 ? Sum / N : double.NaN;
}

/// <summary>An app over a range of days: seconds in front, temperatures while it was (°C), and bytes down / up.</summary>
public sealed record AppTotal(long App, string Path, string Name, double FgSec, double? CpuTempAvg, double? CpuTempMax,
                              double? GpuTempAvg, double? GpuTempMax, long NetDown, long NetUp);

/// <summary>A fan's speed readings at one duty step on one day (see <see cref="FanDrift"/>).</summary>
public sealed record FanDutyRow(long Fan, long Day, int Duty, double RpmSum, int N);

/// <summary>A crash, hang, driver reset, blue screen or unexpected shutdown (see <see cref="CrashKinds"/>).</summary>
public sealed record CrashRecord(long Ts, string Kind, string App, string? Module, string? Code, string? Detail, string? Context = null)
{
    public long Id { get; init; }
}

/// <summary>Something that changed on the PC: a driver, a Windows update, an app installed or removed, a startup item.</summary>
public sealed record ChangeRecord(long Ts, string Kind, string Subject, string? Before, string? After);

/// <summary>
/// Long-term history in %APPDATA%\Kelvra\history.db (SQLite, WAL). Minute rows for the recorded sensors (kept for a
/// set number of days) and day rows (kept for good), per-app day totals, fan speed per duty, crashes and changes.
/// One connection, used by one thread at a time (every public method takes the lock).
/// ponytail: a global lock; reads wait for a minute's write (a few ms). Separate reader connections if that ever shows.
/// </summary>
public sealed class HistoryDb : IDisposable
{
    private const int SchemaVersion = 1;

    public static string DefaultFile => Path.Combine(AppSettings.Dir, "history.db");

    private readonly SqliteConnection _conn;
    private readonly Dictionary<string, long> _sensorIds = new();
    private readonly Dictionary<string, long> _appIds = new(StringComparer.OrdinalIgnoreCase);

    private HistoryDb(SqliteConnection conn) => _conn = conn;

    public static HistoryDb Open(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        conn.Open();
        var db = new HistoryDb(conn);
        db.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        db.Migrate();
        return db;
    }

    // ---------- schema ----------

    /// <summary>Numbered steps, each run once in its own transaction; PRAGMA user_version says how far a file got.</summary>
    private static readonly string[] Migrations =
    {
        // 1
        """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE sensors(id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, hardware TEXT NOT NULL,
            kind INTEGER NOT NULL, role TEXT NOT NULL DEFAULT '');
        CREATE TABLE sensor_minute(sensor INTEGER NOT NULL, ts INTEGER NOT NULL, avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL,
            PRIMARY KEY(sensor, ts)) WITHOUT ROWID;
        CREATE TABLE sensor_day(sensor INTEGER NOT NULL, day INTEGER NOT NULL, sum REAL NOT NULL, n INTEGER NOT NULL,
            min REAL NOT NULL, max REAL NOT NULL, PRIMARY KEY(sensor, day)) WITHOUT ROWID;
        CREATE TABLE days(day INTEGER PRIMARY KEY, minutes INTEGER NOT NULL);
        CREATE TABLE apps(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL);
        CREATE TABLE app_day(app INTEGER NOT NULL, day INTEGER NOT NULL, fg_sec REAL NOT NULL DEFAULT 0,
            cpu_temp_sum REAL NOT NULL DEFAULT 0, cpu_temp_n INTEGER NOT NULL DEFAULT 0, cpu_temp_max REAL,
            gpu_temp_sum REAL NOT NULL DEFAULT 0, gpu_temp_n INTEGER NOT NULL DEFAULT 0, gpu_temp_max REAL,
            net_down INTEGER NOT NULL DEFAULT 0, net_up INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(app, day)) WITHOUT ROWID;
        CREATE TABLE fg_minute(ts INTEGER PRIMARY KEY, app INTEGER NOT NULL);
        CREATE TABLE fan_duty_day(fan INTEGER NOT NULL, day INTEGER NOT NULL, duty INTEGER NOT NULL, rpm_sum REAL NOT NULL,
            n INTEGER NOT NULL, PRIMARY KEY(fan, day, duty)) WITHOUT ROWID;
        CREATE TABLE crashes(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, app TEXT NOT NULL DEFAULT '',
            module TEXT, code TEXT, detail TEXT, context TEXT, UNIQUE(kind, ts, app));
        CREATE INDEX ix_crashes_ts ON crashes(ts);
        CREATE TABLE changes(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT NOT NULL,
            before TEXT, after TEXT);
        CREATE INDEX ix_changes_ts ON changes(ts);
        """,
    };

    private void Migrate()
    {
        int version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        for (int v = version; v < Migrations.Length && v < SchemaVersion; v++)
        {
            using var tx = _conn.BeginTransaction();
            Exec(Migrations[v]);
            Exec($"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
    }

    // ---------- time ----------

    public static long Unix(DateTime time) => new DateTimeOffset(time.ToUniversalTime()).ToUnixTimeSeconds();
    public static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

    /// <summary>The local day a moment falls in, as the unix time of that day's midnight (the key of every day row).</summary>
    public static long DayOf(DateTime time) => Unix(time.ToLocalTime().Date);

    // ---------- writing ----------

    /// <summary>
    /// The row id of a sensor, made the first time it's recorded (its name and role are kept up to date then).
    /// One caller thread (the recorder's): the cache is read without the lock, so a known id never waits for a write.
    /// </summary>
    public long SensorId(string key, string name, string hardware, int kind, string role)
    {
        if (_sensorIds.TryGetValue(key, out long known)) return known;
        lock (_conn)
        {
            using var cmd = Cmd("""
                INSERT INTO sensors(key, name, hardware, kind, role) VALUES($k, $n, $h, $t, $r)
                ON CONFLICT(key) DO UPDATE SET name = excluded.name, hardware = excluded.hardware, kind = excluded.kind, role = excluded.role
                RETURNING id
                """, ("$k", key), ("$n", name), ("$h", hardware), ("$t", kind), ("$r", role));
            return _sensorIds[key] = (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>The row id of an app (by full exe path), made the first time it's seen. One caller thread, as <see cref="SensorId"/>.</summary>
    public long AppId(string path, string name)
    {
        if (_appIds.TryGetValue(path, out long known)) return known;
        lock (_conn)
        {
            using var cmd = Cmd("""
                INSERT INTO apps(path, name) VALUES($p, $n)
                ON CONFLICT(path) DO UPDATE SET name = excluded.name
                RETURNING id
                """, ("$p", path), ("$n", name));
            return _appIds[path] = (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Writes a finished minute. Day rows are added to, not recomputed from the day's minutes, so each write costs the
    /// same however long the day has been.
    /// </summary>
    public void WriteMinute(MinuteBatch b)
    {
        lock (_conn)
        {
            using var tx = _conn.BeginTransaction();
            using (var minute = Cmd("INSERT OR REPLACE INTO sensor_minute(sensor, ts, avg, min, max) VALUES($s, $ts, $a, $lo, $hi)"))
            using (var day = Cmd("""
                INSERT INTO sensor_day(sensor, day, sum, n, min, max) VALUES($s, $d, $sum, $n, $lo, $hi)
                ON CONFLICT(sensor, day) DO UPDATE SET sum = sum + excluded.sum, n = n + excluded.n,
                    min = min(min, excluded.min), max = max(max, excluded.max)
                """))
            {
                var (ms, mts, ma, mlo, mhi) = (P(minute, "$s"), P(minute, "$ts"), P(minute, "$a"), P(minute, "$lo"), P(minute, "$hi"));
                var (ds, dd, dsum, dn, dlo, dhi) = (P(day, "$s"), P(day, "$d"), P(day, "$sum"), P(day, "$n"), P(day, "$lo"), P(day, "$hi"));
                mts.Value = b.Ts;
                dd.Value = b.Day;
                foreach (var v in b.Sensors)
                {
                    if (v.N <= 0) continue;
                    ms.Value = ds.Value = v.Sensor;
                    ma.Value = Math.Round(v.Avg, 2);
                    mlo.Value = dlo.Value = v.Min;
                    mhi.Value = dhi.Value = v.Max;
                    dsum.Value = v.Sum;
                    dn.Value = v.N;
                    minute.ExecuteNonQuery();
                    day.ExecuteNonQuery();
                }
            }

            Run("INSERT INTO days(day, minutes) VALUES($d, 1) ON CONFLICT(day) DO UPDATE SET minutes = minutes + 1", ("$d", b.Day));
            if (b.ForegroundApp is long fg) Run("INSERT OR REPLACE INTO fg_minute(ts, app) VALUES($ts, $a)", ("$ts", b.Ts), ("$a", fg));

            foreach (var a in b.Apps)
                Run("""
                    INSERT INTO app_day(app, day, fg_sec, cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, net_down, net_up)
                    VALUES($a, $d, $fg, $cs, $cn, $cm, $gs, $gn, $gm, $down, $up)
                    ON CONFLICT(app, day) DO UPDATE SET fg_sec = fg_sec + excluded.fg_sec,
                        cpu_temp_sum = cpu_temp_sum + excluded.cpu_temp_sum, cpu_temp_n = cpu_temp_n + excluded.cpu_temp_n,
                        cpu_temp_max = max(coalesce(cpu_temp_max, excluded.cpu_temp_max), coalesce(excluded.cpu_temp_max, cpu_temp_max)),
                        gpu_temp_sum = gpu_temp_sum + excluded.gpu_temp_sum, gpu_temp_n = gpu_temp_n + excluded.gpu_temp_n,
                        gpu_temp_max = max(coalesce(gpu_temp_max, excluded.gpu_temp_max), coalesce(excluded.gpu_temp_max, gpu_temp_max)),
                        net_down = net_down + excluded.net_down, net_up = net_up + excluded.net_up
                    """, ("$a", a.App), ("$d", b.Day), ("$fg", a.FgSec), ("$cs", a.CpuTempSum), ("$cn", a.CpuTempN), ("$cm", a.CpuTempMax),
                    ("$gs", a.GpuTempSum), ("$gn", a.GpuTempN), ("$gm", a.GpuTempMax), ("$down", a.NetDown), ("$up", a.NetUp));

            foreach (var f in b.Fans)
                Run("""
                    INSERT INTO fan_duty_day(fan, day, duty, rpm_sum, n) VALUES($f, $d, $duty, $sum, $n)
                    ON CONFLICT(fan, day, duty) DO UPDATE SET rpm_sum = rpm_sum + excluded.rpm_sum, n = n + excluded.n
                    """, ("$f", f.Fan), ("$d", b.Day), ("$duty", f.Duty), ("$sum", f.RpmSum), ("$n", f.N));
            tx.Commit();
        }
    }

    /// <summary>
    /// Deletes minute detail older than <paramref name="before"/> (unix). Day rows, app days, fan days, crashes and changes
    /// stay: they're what reports of past months and years are made of, and they're small.
    /// </summary>
    public void PruneMinutes(long before)
    {
        lock (_conn)
        {
            Run("DELETE FROM sensor_minute WHERE ts < $t", ("$t", before));
            Run("DELETE FROM fg_minute WHERE ts < $t", ("$t", before));
        }
    }

    /// <summary>Adds crashes not seen before (the same kind, time and app is one crash). Returns how many were new.</summary>
    public int InsertCrashes(IEnumerable<CrashRecord> crashes)
    {
        lock (_conn)
        {
            using var tx = _conn.BeginTransaction();
            int added = 0;
            foreach (var c in crashes)
                added += Run("""
                    INSERT OR IGNORE INTO crashes(ts, kind, app, module, code, detail, context) VALUES($ts, $k, $a, $m, $c, $d, $x)
                    """, ("$ts", c.Ts), ("$k", c.Kind), ("$a", c.App), ("$m", c.Module), ("$c", c.Code), ("$d", c.Detail), ("$x", c.Context));
            tx.Commit();
            return added;
        }
    }

    public void InsertChanges(IEnumerable<ChangeRecord> changes)
    {
        lock (_conn)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var c in changes)
                Run("INSERT INTO changes(ts, kind, subject, before, after) VALUES($ts, $k, $s, $b, $a)",
                    ("$ts", c.Ts), ("$k", c.Kind), ("$s", c.Subject), ("$b", c.Before), ("$a", c.After));
            tx.Commit();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_conn) return Scalar("SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;
    }

    public void SetMeta(string key, string value)
    {
        lock (_conn) Run("INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)", ("$k", key), ("$v", value));
    }

    /// <summary>Deletes all history (Settings → History → Clear). What's installed (the change baseline) stays.</summary>
    public void Clear()
    {
        lock (_conn)
        {
            Exec("""
                DELETE FROM sensor_minute; DELETE FROM sensor_day; DELETE FROM days; DELETE FROM app_day; DELETE FROM fg_minute;
                DELETE FROM fan_duty_day; DELETE FROM crashes; DELETE FROM changes;
                """);
            Exec("VACUUM");
        }
    }

    // ---------- reading ----------

    public List<HistorySensor> Sensors()
    {
        lock (_conn)
            return Read("SELECT id, key, name, hardware, kind, role FROM sensors", r =>
                new HistorySensor(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5)));
    }

    /// <summary>A sensor's minutes from <paramref name="from"/> up to (not including) <paramref name="to"/>, oldest first.</summary>
    public List<(long Ts, double Avg, double Min, double Max)> Minutes(long sensor, long from, long to)
    {
        lock (_conn)
            return Read("SELECT ts, avg, min, max FROM sensor_minute WHERE sensor = $s AND ts >= $f AND ts < $t ORDER BY ts",
                r => (r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)), ("$s", sensor), ("$f", from), ("$t", to));
    }

    /// <summary>A sensor's days whose midnight is in [<paramref name="fromDay"/>, <paramref name="toDay"/>), oldest first.</summary>
    public List<DayStat> Days(long sensor, long fromDay, long toDay)
    {
        lock (_conn)
            return Read("SELECT day, sum, n, min, max FROM sensor_day WHERE sensor = $s AND day >= $f AND day < $t ORDER BY day",
                r => new DayStat(r.GetInt64(0), r.GetDouble(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4)),
                ("$s", sensor), ("$f", fromDay), ("$t", toDay));
    }

    /// <summary>Minutes recorded per day (how long the PC was on with Kelvra running).</summary>
    public List<(long Day, int Minutes)> OnDays(long fromDay, long toDay)
    {
        lock (_conn)
            return Read("SELECT day, minutes FROM days WHERE day >= $f AND day < $t ORDER BY day",
                r => (r.GetInt64(0), r.GetInt32(1)), ("$f", fromDay), ("$t", toDay));
    }

    /// <summary>Each app's totals over the days in [from, to), the longest in front first.</summary>
    public List<AppTotal> AppTotals(long fromDay, long toDay)
    {
        lock (_conn)
            return Read("""
                SELECT a.id, a.path, a.name, sum(d.fg_sec),
                    sum(d.cpu_temp_sum) / nullif(sum(d.cpu_temp_n), 0), max(d.cpu_temp_max),
                    sum(d.gpu_temp_sum) / nullif(sum(d.gpu_temp_n), 0), max(d.gpu_temp_max),
                    sum(d.net_down), sum(d.net_up)
                FROM app_day d JOIN apps a ON a.id = d.app
                WHERE d.day >= $f AND d.day < $t
                GROUP BY a.id ORDER BY sum(d.fg_sec) DESC, sum(d.net_down) DESC
                """, r => new AppTotal(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), D(r, 4), D(r, 5), D(r, 6), D(r, 7),
                    r.GetInt64(8), r.GetInt64(9)), ("$f", fromDay), ("$t", toDay));
    }

    /// <summary>The app in front for each recorded minute in [from, to).</summary>
    public Dictionary<long, long> ForegroundMinutes(long from, long to)
    {
        lock (_conn)
            return Read("SELECT ts, app FROM fg_minute WHERE ts >= $f AND ts < $t", r => (r.GetInt64(0), r.GetInt64(1)), ("$f", from), ("$t", to))
                .ToDictionary(x => x.Item1, x => x.Item2);
    }

    public Dictionary<long, string> AppNames()
    {
        lock (_conn)
            return Read("SELECT id, name FROM apps", r => (r.GetInt64(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
    }

    public List<FanDutyRow> FanDuty(long fromDay, long toDay)
    {
        lock (_conn)
            return Read("SELECT fan, day, duty, rpm_sum, n FROM fan_duty_day WHERE day >= $f AND day < $t",
                r => new FanDutyRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetDouble(3), r.GetInt32(4)), ("$f", fromDay), ("$t", toDay));
    }

    /// <summary>Crashes in [from, to), newest first.</summary>
    public List<CrashRecord> Crashes(long from, long to)
    {
        lock (_conn)
            return Read("SELECT id, ts, kind, app, module, code, detail, context FROM crashes WHERE ts >= $f AND ts < $t ORDER BY ts DESC",
                r => new CrashRecord(r.GetInt64(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), S(r, 6), S(r, 7)) { Id = r.GetInt64(0) },
                ("$f", from), ("$t", to));
    }

    /// <summary>Changes in [from, to), newest first.</summary>
    public List<ChangeRecord> Changes(long from, long to)
    {
        lock (_conn)
            return Read("SELECT ts, kind, subject, before, after FROM changes WHERE ts >= $f AND ts < $t ORDER BY ts DESC, id DESC",
                r => new ChangeRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4)), ("$f", from), ("$t", to));
    }

    /// <summary>The last minute anything was recorded before <paramref name="ts"/>: when Kelvra last saw the PC running.</summary>
    public long? LastMinuteBefore(long ts)
    {
        lock (_conn)
            return Scalar("""
                SELECT max(x) FROM (SELECT max(ts) AS x FROM fg_minute WHERE ts < $t UNION ALL SELECT max(ts) FROM sensor_minute WHERE ts < $t)
                """, ("$t", ts)) as long?;
    }

    // ---------- helpers ----------

    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SqliteParameter P(SqliteCommand cmd, string name) => cmd.Parameters.Add(new SqliteParameter(name, null));

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Run(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> row, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(row(r));
        return list;
    }

    public void Dispose()
    {
        lock (_conn) _conn.Dispose();
    }
}
