using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>
/// Game sessions and benchmark runs (game-sessions.jsonl): one card per game, and a click shows that game's sessions,
/// newest first. A session that ended in a crash is marked.
/// </summary>
public partial class GamesPage : UserControl
{
    private readonly App _app = App.Current;
    private string? _game; // the game whose sessions are shown; null = all games

    public GamesPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Load();
        };
        _app.GameSessionsChanged += () =>
        {
            if (IsVisible) Load();
        };
    }

    private void Load()
    {
        List<GameSessionSummary> sessions;
        try
        {
            sessions = GameSessionLog.Load(GameSessionLog.DefaultFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Reading game sessions", ex);
            sessions = new();
        }
        var games = GameGroup.From(sessions);
        var shown = _game == null ? null : games.FirstOrDefault(g => string.Equals(g.Name, _game, StringComparison.OrdinalIgnoreCase));
        if (shown == null) _game = null; // its sessions are gone (the log keeps the last 200)

        GameList.ItemsSource = games;
        GameList.Visibility = shown == null ? Visibility.Visible : Visibility.Collapsed;
        SessionsList.ItemsSource = shown?.Sessions;
        SessionsList.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = shown?.Name ?? "Games";

        string key = _app.Settings.HotkeyBenchmark.Length > 0 ? $" ({_app.Settings.HotkeyBenchmark})" : "";
        Subtitle.Text = shown != null
            ? shown.Summary + (shown.HasCrashes ? $" · {shown.CrashText}" : "")
            : $"Every game you played, with its sessions and benchmark runs{key}. Click a game to see them.";
        EmptyText.Text = _app.Settings.GameFpsEnabled
            ? $"No game sessions yet.\nPlay a game for a while (it's saved when the game closes), or record a benchmark with the benchmark shortcut{key}."
            : "FPS measuring is off. Turn it on in Settings → Gaming to keep game sessions.";
        EmptyText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Game_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameGroup game) return;
        _game = game.Name;
        Load();
        Scroller.ScrollToTop();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _game = null;
        Load();
        Scroller.ScrollToTop();
    }

    private void OpenSessionCsv_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameSessionSummary { CsvFile: { } file } && File.Exists(file))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
