using System.Windows;

namespace Kelvra;

public enum PawnIoChoice { Later, Never, Installed, Failed }

/// <summary>Asks the user to install (or update) the PawnIO driver and runs the installer.</summary>
public partial class PawnIoPromptWindow : Window
{
    /// <param name="manual">Opened from a button (not the startup prompt): hide "Don't ask again".</param>
    public PawnIoPromptWindow(bool manual = false)
    {
        InitializeComponent();
        if (manual) NeverButton.Visibility = Visibility.Collapsed;
        if (PawnIoInstaller.InstalledVersion is Version v)
        {
            Heading.Text = "Update the sensor driver?";
            Body.Text = $"Your PawnIO driver (version {v}) is outdated. Kelvra needs version " +
                        $"{PawnIoInstaller.MinimumVersion.ToString(2)} or newer to read CPU temperatures, voltages and fan speeds.";
            InstallButton.Content = "Update";
        }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    public PawnIoChoice Choice { get; private set; } = PawnIoChoice.Later;

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Never_Click(object sender, RoutedEventArgs e)
    {
        Choice = PawnIoChoice.Never;
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = LaterButton.IsEnabled = NeverButton.IsEnabled = false;
        InstallButton.Content = "Installing…";
        ErrorText.Visibility = Visibility.Collapsed;

        bool ok;
        try
        {
            ok = await PawnIoInstaller.InstallAsync();
        }
        catch (Exception ex)
        {
            ok = false;
            ErrorText.Text = "Install failed: " + ex.Message;
        }

        if (ok)
        {
            Choice = PawnIoChoice.Installed;
            Close();
            return;
        }

        Choice = PawnIoChoice.Failed;
        if (ErrorText.Text.Length == 0)
            ErrorText.Text = "PawnIO wasn't installed (the installer was cancelled or declined). You can try again.";
        ErrorText.Visibility = Visibility.Visible;
        InstallButton.Content = "Try again";
        InstallButton.IsEnabled = LaterButton.IsEnabled = NeverButton.IsEnabled = true;
    }

    /// <summary>Shows the prompt when needed. Returns true if PawnIO was installed.</summary>
    public static bool ShowIfNeeded(AppSettings settings, Window? owner)
    {
        if (!PawnIoInstaller.NeedsInstall || settings.PawnIoPromptDismissed) return false;

        var dialog = new PawnIoPromptWindow();
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        if (owner is { IsVisible: true }) owner.Activate();

        if (dialog.Choice == PawnIoChoice.Never) settings.PawnIoPromptDismissed = true;
        return dialog.Choice == PawnIoChoice.Installed;
    }
}
