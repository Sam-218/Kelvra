using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Kelvra.NativeMethods;

namespace Kelvra;

public partial class MainWindow : Window
{
    private const int HotkeyToggleOverlays = 1;
    private const int HotkeyToggleLock = 2;

    private static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private readonly App _app = App.Current;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private OverlayProfile? _selected;
    private bool _syncingUi;
    private bool _pawnIoMissing;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        SensorsPage.AddToOverlayRequested += AddSensorToOverlay;
        SensorsPage.InstallPawnIoRequested += () => InstallPawnIo_Click(this, new RoutedEventArgs());
        CategoriesPage.CategoryChosen += (hardware, type) =>
        {
            SensorsPage.SetFilter(hardware, type);
            NavSensors.IsChecked = true;
        };

        OverlayList.ItemsSource = _app.Settings.Overlays;
        foreach (var name in OverlayTemplates.Names)
        {
            var b = new Button
            {
                Content = name == "Blank" ? "+ Blank" : name,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12,
                ToolTip = name == "Blank" ? "Start from an empty overlay" : $"Start from the “{name}” template",
            };
            b.Click += (_, _) => CreateOverlay(name);
            TemplateButtons.Children.Add(b);
        }
        if (_app.Settings.Overlays.Count > 0) OverlayList.SelectedIndex = 0;

        LoadSettingsUi();
        UpdateOverlayControls();
        UpdatePawnIoUi();
        Activated += (_, _) => UpdatePawnIoUi(); // picks up the startup prompt's result or an install done elsewhere

        _app.Overlays.StateChanged += UpdateOverlayControls;
        _toastTimer.Tick += (_, _) => HideToast();

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Hide();
        };
        Closing += OnClosing;
    }

    // ---------- window plumbing ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ThemeManager.ApplyTitleBar(this);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        const uint mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
        RegisterHotKey(_hwnd, HotkeyToggleOverlays, mods, 0x4F); // O
        RegisterHotKey(_hwnd, HotkeyToggleLock, mods, 0x4C);     // L
    }

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
        UnregisterHotKey(_hwnd, HotkeyToggleOverlays);
        UnregisterHotKey(_hwnd, HotkeyToggleLock);
        e.Cancel = true;
        Dispatcher.BeginInvoke(_app.Quit);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (SensorsPage == null || CategoriesPage == null || HistoryPage == null || AlertsPage == null || SystemPage == null || AppsPage == null || DiskPage == null || OverlaysPage == null || SettingsPage == null) return; // during InitializeComponent
        SensorsPage.Visibility = NavSensors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CategoriesPage.Visibility = NavCategories.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = NavAlerts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = NavSystem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiskPage.Visibility = NavDisk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

    private void AddSensorToOverlay(SensorVm sensor)
    {
        var target = _selected ?? _app.Settings.Overlays.FirstOrDefault();
        if (target == null)
        {
            target = new OverlayProfile { Name = NextName("Overlay") };
            _app.Settings.Overlays.Add(target);
            OverlayList.SelectedItem = target;
        }

        if (target.ContainsSensor(sensor.Id))
        {
            ShowToast($"“{sensor.OverlayLabel}” is already in {target.Name}");
            return;
        }
        AddItem(target, sensor);
        ShowToast($"Added “{sensor.OverlayLabel}” to {target.Name}");
    }

    private void AddItem(OverlayProfile target, SensorVm sensor)
    {
        var item = new OverlayItem { SensorId = sensor.Id };
        _app.Overlays.Resolve(item);
        target.Items.Add(item);
    }

    // ---------- overlays page ----------

    private void OverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected != null) _selected.Changed -= OnSelectedChanged;
        _selected = OverlayList.SelectedItem as OverlayProfile;
        if (_selected != null) _selected.Changed += OnSelectedChanged;

        Editor.DataContext = _selected;
        Editor.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        NoOverlaySelected.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoItems();
    }

    private void OnSelectedChanged(object? sender, string property)
    {
        if (property == nameof(OverlayProfile.Items)) UpdateNoItems();
    }

    private void UpdateNoItems() =>
        NoItemsText.Visibility = _selected is { Items.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    private void CreateOverlay(string template)
    {
        string name = NextName(template == "Blank" ? "Overlay" : template);
        var p = OverlayTemplates.Create(template, _app.Sensors.All, name);
        int n = _app.Settings.Overlays.Count;
        p.X = 20 + n * 30;
        p.Y = 20 + n * 30;
        p.Locked = false; // new overlays start movable so they can be placed
        _app.Settings.Overlays.Add(p);
        _app.Overlays.Visible = true;
        OverlayList.SelectedItem = p;
        ShowToast($"Created “{name}”. Drag it into place, then lock it (Ctrl+Shift+L)");
    }

    private string NextName(string baseName)
    {
        var names = _app.Settings.Overlays.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        int i = 2;
        while (names.Contains($"{baseName} {i}")) i++;
        return $"{baseName} {i}";
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var copy = _selected.Clone(NextName(_selected.Name));
        _app.Settings.Overlays.Add(copy);
        OverlayList.SelectedItem = copy;
        ShowToast($"Duplicated as “{copy.Name}”");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var result = MessageBox.Show(this, $"Delete the overlay “{_selected.Name}”?", "Delete overlay",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        int index = OverlayList.SelectedIndex;
        _app.Settings.Overlays.Remove(_selected);
        if (_app.Settings.Overlays.Count > 0)
            OverlayList.SelectedIndex = Math.Min(index, _app.Settings.Overlays.Count - 1);
    }

    private void AddSensors_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var picker = new SensorPickerWindow(_app.Sensors.All, _selected) { Owner = this };
        if (picker.ShowDialog() != true) return;

        foreach (var id in picker.SelectedIds)
            if (_app.Sensors.ById.TryGetValue(id, out var s)) AddItem(_selected, s);
        if (picker.SelectedIds.Count > 0)
            ShowToast($"Added {picker.SelectedIds.Count} sensor(s) to {_selected.Name}");
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveItem(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveItem(sender, +1);

    private void MoveItem(object sender, int delta)
    {
        if (_selected == null || (sender as FrameworkElement)?.DataContext is not OverlayItem item) return;
        int from = _selected.Items.IndexOf(item);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _selected.Items.Count) return;
        _selected.Items.Move(from, to);
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || (sender as FrameworkElement)?.DataContext is not OverlayItem item) return;
        _selected.Items.Remove(item);
    }

    // ---------- sidebar overlay controls ----------

    private void UpdateOverlayControls()
    {
        _syncingUi = true;
        OverlaysSwitch.IsChecked = _app.Overlays.Visible;
        _syncingUi = false;
        LockAllButton.Content = _app.Overlays.AnyUnlocked ? "Lock overlays" : "Unlock to move";
    }

    private void OverlaysSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi) return;
        _app.Overlays.Visible = OverlaysSwitch.IsChecked == true;
    }

    private void LockAll_Click(object sender, RoutedEventArgs e) => _app.Overlays.ToggleLockAll();

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
        _app.MarkDirty();
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
