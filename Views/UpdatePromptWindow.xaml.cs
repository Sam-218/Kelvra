using System.ComponentModel;
using System.Windows;

namespace Kelvra;

public enum UpdateChoice { Later, Installed }

/// <summary>Offers a newer Kelvra release and installs it ("Not now" asks again 24 h later, see <see cref="Updater.ShouldPrompt"/>).</summary>
public partial class UpdatePromptWindow : Window
{
    private readonly UpdateInfo _update;
    private bool _busy;

    public UpdatePromptWindow(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        Heading.Text = $"Kelvra {update.Version.ToString(3)} is available";
        Body.Text = $"You have version {Updater.CurrentVersion.ToString(3)}. Kelvra downloads the update, restarts, and keeps all your settings.";
        NotesText.Text = update.Notes.Length > 0 ? update.Notes : "No release notes.";
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        InstallButton.IsEnabled = LaterButton.IsEnabled = false;
        InstallButton.Content = "Downloading…";
        ErrorText.Visibility = Visibility.Collapsed;
        var progress = new Progress<double>(p => InstallButton.Content = $"Downloading… {p * 100:0}%");

        try
        {
            await Updater.DownloadAndInstallAsync(_update, progress);
            Choice = UpdateChoice.Installed;
            _busy = false;
            Close();
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything at all: the dialog must never stay stuck with both buttons disabled and closing blocked
            Log.Error("Installing the update", ex);
            ErrorText.Text = "The update wasn't installed. " + Updater.Describe(ex);
        }
        finally
        {
            _busy = false;
        }

        ErrorText.Visibility = Visibility.Visible;
        InstallButton.Content = "Try again";
        InstallButton.IsEnabled = LaterButton.IsEnabled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _busy; // the download is replacing the exe: let it finish
        base.OnClosing(e);
    }

    /// <summary>Shows the prompt (owned by <paramref name="owner"/> when it's visible) and returns the user's choice.</summary>
    public static UpdateChoice Ask(UpdateInfo update, Window? owner)
    {
        var dialog = new UpdatePromptWindow(update);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
