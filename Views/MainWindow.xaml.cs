using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Kelvra.NativeMethods;

namespace Kelvra;

/// <summary>
/// Icon-rail shell that switches between the pages, plus the Overlays editor, the Settings page, the global hotkeys
/// (configurable, Settings → Shortcuts) and the small toast messages. Each other page lives in its own UserControl.
/// </summary>
public partial class MainWindow : Window
{
    private const int HotkeyToggleOverlays = 1;
    private const int HotkeyToggleLock = 2;

    private static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private readonly App _app = App.Current;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private bool _syncingUi;
    private bool _pawnIoMissing;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        SensorsPage.AddToOverlayRequested += OverlaysPage.AddSensor;
        OverlaysPage.Toast += ShowToast;
        SensorsPage.InstallPawnIoRequested += () => InstallPawnIo_Click(this, new RoutedEventArgs());
        CategoriesPage.CategoryChosen += (hardware, type) =>
        {
            SensorsPage.SetFilter(hardware, type);
            NavSensors.IsChecked = true;
        };

        BuildAccentSwatches();
        LoadSettingsUi();
        UpdateOverlayControls();
        UpdatePawnIoUi();
        Activated += (_, _) => UpdatePawnIoUi(); // picks up the startup prompt's result or an install done elsewhere

        _app.Overlays.StateChanged += UpdateOverlayControls;
        _toastTimer.Tick += (_, _) => HideToast();

        foreach (var box in new[] { HotkeyOverlaysBox, HotkeyLockBox })
        {
            box.HotkeyChanged += Hotkey_Changed;
            box.RecordingStarted += UnregisterHotkeys; // so the current shortcut can be pressed into the box
            box.RecordingEnded += RegisterHotkeys;
        }

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) =>
        {
            // Only when asked to: hiding on every minimise made the taskbar button vanish unexpectedly
            if (WindowState == WindowState.Minimized && _app.Settings.MinimizeToTray) Hide();
        };
        Closing += OnClosing;
    }

    // ---------- window plumbing ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ThemeManager.ApplyTitleBar(this);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkeys();
    }

    // ---------- global shortcuts ----------

    private string? _overlaysHotkeyError, _lockHotkeyError;

    /// <summary>(Re)registers both shortcuts from settings. A shortcut another program owns is reported, not silently dropped.</summary>
    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotkeys();
        _overlaysHotkeyError = Register(HotkeyToggleOverlays, _app.Settings.HotkeyOverlays);
        _lockHotkeyError = Register(HotkeyToggleLock, _app.Settings.HotkeyLock);
        UpdateHotkeyUi();
        UpdateOverlayControls();
    }

    private string? Register(int id, string text)
    {
        if (text.Length == 0) return null;
        if (Hotkey.Parse(text) is not { } hotkey) return "Not a valid shortcut.";
        if (RegisterHotKey(_hwnd, id, hotkey.NativeModifiers | MOD_NOREPEAT, hotkey.VirtualKey)) return null;
        Log.Warn($"Shortcut {text} is taken by another program");
        return "Another program already uses this shortcut, so it doesn't work for Kelvra. Pick a different one.";
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyToggleOverlays);
        UnregisterHotKey(_hwnd, HotkeyToggleLock);
    }

    private void Hotkey_Changed(HotkeyBox box)
    {
        if (box == HotkeyOverlaysBox) _app.Settings.HotkeyOverlays = box.Hotkey;
        else _app.Settings.HotkeyLock = box.Hotkey;
        _app.MarkDirty();
        // RecordingEnded re-registers right after this
    }

    private void UpdateHotkeyUi()
    {
        var s = _app.Settings;
        HotkeyOverlaysBox.Hotkey = s.HotkeyOverlays;
        HotkeyLockBox.Hotkey = s.HotkeyLock;
        ShowNote(HotkeyOverlaysNote, s.HotkeyOverlays, _overlaysHotkeyError);
        ShowNote(HotkeyLockNote, s.HotkeyLock, _lockHotkeyError);

        void ShowNote(TextBlock note, string text, string? error)
        {
            bool same = text.Length > 0 && text == (note == HotkeyOverlaysNote ? s.HotkeyLock : s.HotkeyOverlays);
            note.Text = error ?? (same ? "Both actions use the same shortcut." : Hotkey.Parse(text)?.Warning() ?? (text.Length == 0 ? "Off" : ""));
            note.Foreground = (System.Windows.Media.Brush)FindResource(error != null || same ? "HotBrush" : "MutedBrush");
        }
    }

    /// <summary>Hotkey problems for the diagnostics text.</summary>
    public IEnumerable<string> HotkeyNotes()
    {
        if (_overlaysHotkeyError != null) yield return $"Hotkey {_app.Settings.HotkeyOverlays}: {_overlaysHotkeyError}";
        if (_lockHotkeyError != null) yield return $"Hotkey {_app.Settings.HotkeyLock}: {_lockHotkeyError}";
    }

    private static string KeyHint(string hotkey) => hotkey.Length == 0 ? "" : $" ({hotkey})";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HotkeyToggleOverlays: _app.Overlays.Visible = !_app.Overlays.Visible; break;
            case HotkeyToggleLock: _app.Overlays.ToggleLockAll(); break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsQuitting) return;
        if (_app.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        UnregisterHotkeys();
        e.Cancel = true;
        Dispatcher.BeginInvoke(_app.Quit);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>All pages exist the whole time and are only shown/hidden, so they keep their state (scans, search text …).</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsPage == null) return; // during InitializeComponent (it's the last page created)
        OverviewPage.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SensorsPage.Visibility = NavSensors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CategoriesPage.Visibility = NavCategories.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = NavAlerts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = NavSystem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiskPage.Visibility = NavDisk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FansPage.Visibility = NavFans.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlaysPage.Visibility = NavOverlays.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- sensors page / PawnIO ----------

    private void UpdatePawnIoUi()
    {
        _pawnIoMissing = PawnIoInstaller.NeedsInstall;
        SensorsPage.PawnIoMissing = _pawnIoMissing;
        PawnIoStatus.Text = PawnIoInstaller.StatusText;
        SettingsInstallButton.Content = PawnIoInstaller.IsInstalled ? "Update" : "Install";
        SettingsInstallButton.Visibility = _pawnIoMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstallPawnIo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PawnIoPromptWindow(manual: true) { Owner = this };
        dialog.ShowDialog();
        UpdatePawnIoUi();
        if (dialog.Choice != PawnIoChoice.Installed) return;

        ShowToast("PawnIO installed – reconnecting to sensors…");
        await _app.Sensors.ReopenAsync();
        _app.Overlays.ResolveNames();
        _app.Overlays.RebuildAll();
        ShowToast("PawnIO installed – CPU sensors are now available");
    }

    // ---------- sidebar overlay controls ----------

    private void UpdateOverlayControls()
    {
        _syncingUi = true;
        bool visible = _app.Overlays.Visible;
        OverlaysSwitch.IsChecked = visible;
        _syncingUi = false;
        OverlaysSwitch.Content = visible ? "" : ""; // eye / crossed-out eye
        string overlaysTip = (visible ? "Overlays are shown · click to hide" : "Overlays are hidden · click to show") + KeyHint(_app.Settings.HotkeyOverlays);
        OverlaysSwitch.ToolTip = overlaysTip;
        AutomationProperties.SetName(OverlaysSwitch, "Show overlays");

        bool unlocked = _app.Overlays.AnyUnlocked;
        LockAllButton.Content = unlocked ? "" : ""; // open / closed padlock
        string lockTip = (unlocked ? "Overlays can be moved · click to lock them" : "Overlays are locked · click to unlock and move them") + KeyHint(_app.Settings.HotkeyLock);
        LockAllButton.ToolTip = lockTip;
        AutomationProperties.SetName(LockAllButton, unlocked ? "Lock overlays" : "Unlock overlays to move them");
    }

    private void OverlaysSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        _app.Overlays.Visible = OverlaysSwitch.IsChecked == true;
    }

    private void LockAll_Click(object sender, RoutedEventArgs e) => _app.Overlays.ToggleLockAll();

    private void Mini_Click(object sender, RoutedEventArgs e) => _app.ShowMini();

    // ---------- settings page ----------

    private void LoadSettingsUi()
    {
        _syncingUi = true;
        var s = _app.Settings;
        (s.Theme switch { "Dark" => ThemeDark, "Light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        RefreshSlider.Value = Math.Clamp(s.RefreshMs, 250, 5000);
        RefreshText.Text = $"{s.RefreshMs / 1000.0:0.00} s";
        StartMinimizedSwitch.IsChecked = s.StartMinimized;
        CloseToTraySwitch.IsChecked = s.CloseToTray;
        MinimizeToTraySwitch.IsChecked = s.MinimizeToTray;
        KeepHistorySwitch.IsChecked = s.KeepHistory;
        UpdateHotkeyUi();
        StartWithWindowsSwitch.IsChecked = s.StartWithWindows;
        (s.Fahrenheit ? UnitFahrenheit : UnitCelsius).IsChecked = true;
        foreach (RadioButton r in LogIntervalButtons.Children)
            r.IsChecked = int.Parse((string)r.Tag) == s.LogIntervalSeconds;
        AboutText.Text =
            $"Kelvra {typeof(App).Assembly.GetName().Version?.ToString(3)} · sensors by LibreHardwareMonitor\n" +
            $"Running as administrator: {(IsAdmin ? "yes" : "no")}\n" +
            $"Settings: {AppSettings.Dir}";
        _syncingUi = false;
    }

    /// <summary>One round swatch per accent colour (Settings → Appearance).</summary>
    private void BuildAccentSwatches()
    {
        foreach (var accent in Accents.All)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Accent",
                Background = Accents.Frozen(accent.Bright),
                ToolTip = accent.Name,
                IsChecked = string.Equals(_app.Settings.Accent, accent.Name, StringComparison.OrdinalIgnoreCase),
            };
            AutomationProperties.SetName(swatch, accent.Name);
            swatch.Checked += (_, _) =>
            {
                if (_app.Settings.Accent == accent.Name) return;
                _app.Settings.Accent = accent.Name;
                ThemeManager.ApplyAccent(accent.Name);
                _app.MarkDirty();
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || sender is not RadioButton rb) return;
        _app.Settings.Theme = (string)rb.Content;
        ThemeManager.Apply(_app.Settings.Theme);
        _app.MarkDirty();
    }

    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || RefreshText == null) return;
        _app.Settings.RefreshMs = (int)RefreshSlider.Value;
        RefreshText.Text = $"{RefreshSlider.Value / 1000.0:0.00} s";
        _app.MarkDirty();
    }

    private void BehaviourSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        _app.Settings.StartMinimized = StartMinimizedSwitch.IsChecked == true;
        _app.Settings.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _app.Settings.MinimizeToTray = MinimizeToTraySwitch.IsChecked == true;
        _app.MarkDirty();
    }

    private void KeepHistory_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        _app.Settings.KeepHistory = KeepHistorySwitch.IsChecked == true;
        _app.ApplyKeepHistory();
        _app.MarkDirty();
        ShowToast(_app.Settings.KeepHistory ? "History is now saved and kept for 24 hours" : "Saved history deleted");
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var notes = HotkeyNotes().Append($"Saving settings: {_app.Settings.LastSaveError ?? "OK"}");
        Clipboard.SetText(Diagnostics.Build(_app.Settings, _app.Sensors, notes));
        ShowToast("Diagnostics copied – paste them into your bug report");
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Folder}\"") { UseShellExecute = true });
    }

    private void Unit_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        _app.Settings.Fahrenheit = UnitFahrenheit.IsChecked == true;
        SensorFormat.UseFahrenheit = _app.Settings.Fahrenheit;
        foreach (var rule in _app.Settings.Alerts) rule.RefreshUnits();
        _app.MarkDirty();
    }

    private void LogInterval_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || sender is not RadioButton rb) return;
        _app.Settings.LogIntervalSeconds = int.Parse((string)rb.Tag);
        _app.MarkDirty();
    }

    private async void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        bool on = StartWithWindowsSwitch.IsChecked == true;
        if (!IsAdmin)
        {
            _syncingUi = true;
            StartWithWindowsSwitch.IsChecked = !on;
            _syncingUi = false;
            MessageBox.Show(this, "Kelvra needs to run as administrator to change this (it registers a sign-in task with admin rights).",
                "Kelvra", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartWithWindowsSwitch.IsEnabled = false;
        string error = "";
        bool ok = await Task.Run(() => on ? Autostart.Enable(out error) : Autostart.Disable(out error));
        StartWithWindowsSwitch.IsEnabled = true;

        if (!ok)
        {
            _syncingUi = true;
            StartWithWindowsSwitch.IsChecked = !on;
            _syncingUi = false;
            MessageBox.Show(this, $"Couldn't {(on ? "turn on" : "turn off")} Start with Windows.\n{error}", "Kelvra",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _app.Settings.StartWithWindows = on;
        _app.MarkDirty();
        ShowToast(on ? "Kelvra will start in the tray when you sign in" : "Kelvra won't start with Windows");
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Dir}\"") { UseShellExecute = true });
    }

    // ---------- toast ----------

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
    }
}
