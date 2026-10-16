using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>A day heading or one change on the Changes page.</summary>
public sealed record ChangeRow(bool IsHeader, string Icon, string Text, string Sub, string Time)
{
    public bool HasSub => Sub.Length > 0;
}

/// <summary>
/// The change timeline (see <see cref="ChangeWatcher"/>), newest first and grouped by day. Under a driver, BIOS or Windows
/// change: how the games played on both sides of it ran (see <see cref="ChangeEffects"/>).
/// </summary>
public partial class ChangesPage : UserControl
{
    private readonly App _app = App.Current;
    private List<ChangeRecord> _all = new();
    private List<GameSessionSummary> _sessions = new();

    public ChangesPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _ = LoadAsync();
        };
        if (_app.Changes is { } watcher)
            watcher.Added += found => Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible) _ = LoadAsync();
            });
    }

    private async Task LoadAsync()
    {
        if (_app.Recorder is not { } recorder)
        {
            _all = new();
            Show("Long-term history isn't available, so changes can't be listed (see the log in Settings → About).");
            return;
        }
        (_all, _sessions) = await recorder.ReadAsync(db =>
        {
            List<GameSessionSummary> sessions;
            try { sessions = GameSessionLog.Load(GameSessionLog.DefaultFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sessions = new(); }
            return (db.Changes(0, long.MaxValue), sessions);
        });
        Show(null);
    }

    private void Show(string? message)
    {
        IEnumerable<ChangeRecord> shown = _all;
        if (ShowSystem.IsChecked == true) shown = _all.Where(c => c.Kind is ChangeKinds.Driver or ChangeKinds.Bios or ChangeKinds.Windows or ChangeKinds.Update);
        else if (ShowApps.IsChecked == true) shown = _all.Where(c => c.Kind == ChangeKinds.App);
        else if (ShowStartup.IsChecked == true) shown = _all.Where(c => c.Kind == ChangeKinds.Startup);

        var rows = new List<ChangeRow>();
        DateTime? day = null;
        foreach (var c in shown.Take(500))
        {
            var time = HistoryDb.Local(c.Ts);
            if (time.Date != day)
            {
                day = time.Date;
                rows.Add(new ChangeRow(true, "", DayTitle(time.Date).ToUpper(CultureInfo.CurrentCulture), "", ""));
            }
            var (icon, text) = Words(c);
            rows.Add(new ChangeRow(false, icon, text, Effects(c), time.ToString("t", CultureInfo.CurrentCulture)));
        }
        List.ItemsSource = rows;
        EmptyText.Text = message ?? (_all.Count == 0
            ? "No changes yet. Kelvra noted how your PC is set up today; from now on it lists what changes.\nWindows updates from the last 30 days appear within a minute."
            : "None of this kind.");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DayTitle(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>The change in a sentence, with an icon.</summary>
    internal static (string Icon, string Text) Words(ChangeRecord c) => c.Kind switch
    {
        ChangeKinds.Driver => ("", c.Before == null ? $"{c.Subject} added (driver {c.After})"
                                       : c.After == null ? $"{c.Subject} removed"
                                       : $"{c.Subject} driver {c.Before} → {c.After}"),
        ChangeKinds.Bios => ("", $"BIOS {c.Before} → {c.After}"),
        ChangeKinds.Windows => ("", $"Windows {c.Before} → {c.After}"),
        ChangeKinds.Update => ("", $"Windows update {c.Subject} installed"),
        ChangeKinds.App => ("", c.Before == null ? $"{c.Subject} installed{Version(c.After)}"
                                    : c.After == null ? $"{c.Subject} removed"
                                    : $"{c.Subject} updated {c.Before} → {c.After}"),
        ChangeKinds.Startup => ("", c.Before == null ? $"{c.Subject} added to startup{(c.After == "off" ? " (switched off)" : "")}"
                                        : c.After == null ? $"{c.Subject} removed from startup"
                                        : c.After == "on" ? $"{c.Subject} now starts with Windows"
                                        : $"{c.Subject} no longer starts with Windows"),
        _ => ("", $"{c.Subject}: {c.Before} → {c.After}"),
    };

    private static string Version(string? v) => string.IsNullOrEmpty(v) ? "" : $" ({v})";

    /// <summary>For driver, BIOS and Windows changes: how the games ran before and after (until the next such change).</summary>
    private string Effects(ChangeRecord c)
    {
        if (c.Kind is not (ChangeKinds.Driver or ChangeKinds.Bios or ChangeKinds.Windows) || c.Before == null || c.After == null) return "";
        var at = HistoryDb.Local(c.Ts);
        var next = _all.Where(o => o.Kind == c.Kind && o.Subject == c.Subject && o.Ts > c.Ts).Select(o => (DateTime?)HistoryDb.Local(o.Ts)).Min();
        var effects = ChangeEffects.Of(at, next, _sessions);
        if (effects.Count == 0)
            return at > DateTime.Now.AddDays(-30) ? "How games run since: play a game you also played before it (10 minutes or more), and it shows here." : "";
        return "Since then: " + string.Join("  ·  ", effects.Select(ChangeEffects.Describe));
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Show(null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Changes is { } watcher) await watcher.ScanAsync();
        await LoadAsync();
    }
}
