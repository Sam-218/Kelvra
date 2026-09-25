using System.Windows;
using WinForms = System.Windows.Forms;

namespace Kelvra;

public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon = new();
    private readonly WinForms.ToolStripMenuItem _toggleOverlays;
    private readonly WinForms.ToolStripMenuItem _toggleLock;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon.Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        _icon.Text = "Kelvra";

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Kelvra", null, (_, _) => App.Current.MainView.ShowFromTray());
        _toggleOverlays = new WinForms.ToolStripMenuItem("Show overlays", null, (_, _) =>
            App.Current.Overlays.Visible = !App.Current.Overlays.Visible) { ShortcutKeyDisplayString = "Ctrl+Shift+O" };
        _toggleLock = new WinForms.ToolStripMenuItem("Unlock overlays (move)", null, (_, _) =>
            App.Current.Overlays.ToggleLockAll()) { ShortcutKeyDisplayString = "Ctrl+Shift+L" };
        menu.Items.Add(_toggleOverlays);
        menu.Items.Add(_toggleLock);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.Current.Quit());
        menu.Opening += (_, _) =>
        {
            _toggleOverlays.Checked = App.Current.Overlays.Visible;
            _toggleLock.Text = App.Current.Overlays.AnyUnlocked ? "Lock overlays" : "Unlock overlays (move)";
        };

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) App.Current.MainView.ShowFromTray();
        };
        _icon.Visible = true;
    }

    /// <summary>Windows notification from the tray icon (used by alerts).</summary>
    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.Warning);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
