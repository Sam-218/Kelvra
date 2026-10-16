using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace Kelvra;

/// <summary>One "worth knowing" line, with its colour resolved from the theme.</summary>
public sealed record InsightRow(string Icon, string Text, Brush Brush);

/// <summary>One number card: "CPU", "54°" avg "88°" peak, "Peak Sat 21:14 · Cyberpunk 2077".</summary>
public sealed record StatTile(string Label, string Value, Brush ValueBrush, string Unit, string Sub, string Value2 = "", Brush? Value2Brush = null, string Unit2 = "",
                              bool HasLink = false);

/// <summary>One app in the report's app list, its temperatures in their thermal colours.</summary>
public sealed record AppRow(string Name, string Path, string Time, string Cpu, Brush CpuBrush, string Gpu, Brush GpuBrush, string Down, string Up);

/// <summary>
/// Reports: any day, week, month or year from the long-term history (see <see cref="ReportBuilder"/>). Read on a worker
/// thread; "today" (and this week …) refreshes as each minute is written while the page is open.
/// </summary>
public partial class ReportsPage : UserControl
{
    private readonly App _app = App.Current;
    private ReportPeriod _period;
    private Report? _report;
    private bool _loading = true;
    private int _version;

    public ReportsPage()
    {
        InitializeComponent();
        var range = Enum.TryParse<ReportRange>(_app.Settings.ReportRange, out var r) ? r : ReportRange.Day;
        _period = ReportPeriod.For(range, DateTime.Today);
        foreach (RadioButton b in RangeButtons.Children) b.IsChecked = (string)b.Tag == range.ToString();
        _loading = false;

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = LoadAsync();
        };
        if (_app.Recorder is { } recorder)
            recorder.MinuteWritten += () =>
            {
                if (IsVisible && _period.IsCurrent) _ = LoadAsync(quiet: true);
            };
    }

    private async Task LoadAsync(bool quiet = false)
    {
        int version = ++_version;
        var period = _period;
        NextButton.IsEnabled = !period.IsCurrent;
        NowButton.Visibility = period.IsCurrent ? Visibility.Collapsed : Visibility.Visible;

        if (_app.Recorder is not { } recorder)
        {
            ShowEmpty("Long-term history isn't available: history.db couldn't be opened (see Settings → About → Open log folder).");
            return;
        }
        if (!_app.Settings.LongHistory)
        {
            ShowEmpty("Recording is off. Turn on Settings → History → Record history to get reports.");
            return;
        }
        if (!quiet) StatusText.Text = "Loading…";
        var alerts = _app.Settings.Alerts.ToList();
        Report report;
        try
        {
            report = await recorder.ReadAsync(db => ReportBuilder.Build(db, period, LoadSessions(), alerts));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.Error("Building a report", ex);
            StatusText.Text = "Couldn't read the history.";
            return;
        }
        if (version != _version) return; // a newer request is on its way
        Show(report);
    }

    private static List<GameSessionSummary> LoadSessions()
    {
        try { return GameSessionLog.Load(GameSessionLog.DefaultFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading game sessions", ex);
            return new();
        }
    }

    private void ShowEmpty(string text)
    {
        _report = null;
        CopyButton.IsEnabled = false;
        Headline.Text = _period.Title;
        Subline.Text = "";
        Scroller.Visibility = Visibility.Collapsed;
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }

    private void Show(Report r)
    {
        StatusText.Text = "";
        if (!r.HasData)
        {
            ShowEmpty(r.Period.IsCurrent
                ? "Nothing recorded yet. Kelvra records while it runs, also from the tray; the first minutes show up shortly."
                : "Nothing was recorded in this period (Kelvra wasn't running, or history was cleared).");
            return;
        }
        _report = r;
        CopyButton.IsEnabled = true;
        Headline.Text = ReportSummary.Headline(r);
        Subline.Text = ReportSummary.Subline(r);
        Scroller.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;

        Tiles.ItemsSource = TilesFor(r);
        Chart.SetData(r);
        ChartNote.Text = (r.Period.Range switch
        {
            ReportRange.Day => "by the minute",
            ReportRange.Week when r.Series.Values.Any(s => s.Count > r.Period.Days) => "hourly",
            _ => "daily",
        }) + " · shaded = PC on";
        HotNote.Text = $"over {Temp(ReportChart.HotDot)}";

        var apps = r.Apps.Where(a => a.FgSec >= 60).Take(8).Select(a => new AppRow(a.Name, a.Path, Insights.Short(a.FgSec),
            Temp(a.CpuTempAvg), HeatBrush(a.CpuTempAvg), Temp(a.GpuTempAvg), HeatBrush(a.GpuTempAvg),
            a.NetDown > 0 ? ByteFormat.Format(a.NetDown) : "–", a.NetUp > 0 ? $"Uploaded {ByteFormat.Format(a.NetUp)}" : "Nothing uploaded")).ToList();
        AppList.ItemsSource = apps;
        AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var games = ReportSummary.Games(r.Games);
        GameList.ItemsSource = games;
        GamesEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        InsightList.ItemsSource = r.Insights.Select(i => new InsightRow(i.Icon, i.Text, Res(i.Tone switch
        {
            InsightTone.Good => "GoodBrush",
            InsightTone.Warn => "WarnBrush",
            InsightTone.Hot => "HotBrush",
            _ => "MutedBrush",
        }))).ToList();
        InsightsCard.Visibility = r.Insights.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<StatTile> TilesFor(Report r)
    {
        var text = Res("TextBrush");
        int hours = r.OnMinutes / 60, minutes = r.OnMinutes % 60;
        var tiles = new List<StatTile>
        {
            new("PC ON", hours.ToString(), text, " h ", ReportSummary.PcTimeChange(r) is { Length: > 0 } change ? change : "recorded by Kelvra",
                minutes.ToString("00"), text, " m"),
        };
        foreach (var (label, role) in new[] { ("CPU", HistoryRoles.CpuTemp), ("GPU", HistoryRoles.GpuTemp) })
        {
            if (r.Stats.GetValueOrDefault(role) is not { } s) tiles.Add(new(label, "–", Res("MutedBrush"), "", "No temperatures recorded"));
            else tiles.Add(new(label, Temp(s.Avg), HeatBrush(s.Avg), " avg  ", ReportSummary.Peak(r, role), Temp(s.Max), HeatBrush(s.Max), " peak"));
        }
        string crash = r.Crashes.Count == 0 ? "None. Nothing crashed or froze."
            : $"{CrashExplainer.Explain(r.Crashes[0]).Title} · {CrashTime(r, HistoryDb.Local(r.Crashes[0].Ts))}";
        tiles.Add(new("CRASHES", r.Crashes.Count.ToString(), r.Crashes.Count > 0 ? Res("CritBrush") : text, "", crash, HasLink: r.Crashes.Count > 0));
        return tiles;
    }

    private void ShowCrashes_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavCrashes.IsChecked = true;
    }

    private static string CrashTime(Report r, DateTime at)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return r.Period.Range == ReportRange.Day ? at.ToString("t", c) : $"{at.ToString(r.Period.Range == ReportRange.Week ? "ddd" : "d MMM", c)} {at.ToString("t", c)}";
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private Brush HeatBrush(double? c) => c is double v ? Thermal.BrushFor(v) : Res("MutedBrush");

    private static string Temp(double? c) => c is double v ? $"{SensorFormat.ToDisplay(SensorType.Temperature, (float)v):0}°" : "–";

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        try
        {
            Clipboard.SetText(ReportSummary.Text(_report));
            StatusText.Text = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Error("Copying the report", ex); // another app holds the clipboard
            StatusText.Text = "Couldn't copy, try again";
        }
    }

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ReportRange>(tag, out var range)) return;
        _app.Settings.ReportRange = tag;
        _app.MarkDirty();
        // Keep looking at the same time: the week, month or year around the day shown
        _period = ReportPeriod.For(range, _period.IsCurrent ? DateTime.Today : _period.From);
        _ = LoadAsync();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _period = _period.Previous;
        _ = LoadAsync();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_period.IsCurrent) return;
        _period = _period.Next;
        _ = LoadAsync();
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        _period = ReportPeriod.For(_period.Range, DateTime.Today);
        _ = LoadAsync();
    }
}
