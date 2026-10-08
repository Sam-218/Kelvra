using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace Kelvra;

/// <summary>
/// The overlay editor: the list of overlays, a live preview over a test background, and tabs for sensors, style,
/// layout, position and warnings. Changes apply to the on-screen overlay immediately.
/// </summary>
public partial class OverlaysPage : UserControl
{
    private readonly App _app = App.Current;
    private OverlayProfile? _selected;
    private OverlayView? _preview;

    /// <summary>Short confirmation messages for the main window's toast.</summary>
    public event Action<string>? Toast;

    public OverlaysPage()
    {
        InitializeComponent();

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
        foreach (var style in OverlayStyles.All)
        {
            var b = new Button { Content = style.Name, Margin = new Thickness(0, 0, 8, 8), ToolTip = style.Description };
            b.Click += (_, _) =>
            {
                if (_selected == null) return;
                style.Apply(_selected);
                Toast?.Invoke($"Applied the “{style.Name}” look");
            };
            StylePresets.Children.Add(b);
        }
        BuildFontLists();
        BuildAnchorGrid();
        ((RadioButton)BackdropButtons.Children[0]).IsChecked = true;

        _app.Sensors.Updated += () =>
        {
            if (IsVisible) _preview?.UpdateValues();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            BuildMonitorList();
            string unit = " " + SensorFormat.Unit(LibreHardwareMonitor.Hardware.SensorType.Temperature);
            TempUnit1.Text = TempUnit2.Text = unit;
            BindingOperations.GetBindingExpression(WarmTempSlider, RangeBase.ValueProperty)?.UpdateTarget();
            BindingOperations.GetBindingExpression(HotTempSlider, RangeBase.ValueProperty)?.UpdateTarget();
        };
        if (_app.Settings.Overlays.Count > 0) OverlayList.SelectedIndex = 0;
    }

    // ---------- list ----------

    private void OverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected != null) _selected.Changed -= OnSelectedChanged;
        _selected = OverlayList.SelectedItem as OverlayProfile;
        if (_selected != null) _selected.Changed += OnSelectedChanged;

        Editor.DataContext = _selected;
        Editor.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        NoOverlaySelected.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        BuildPreview();
        UpdateNoItems();
        UpdateGameModules();
        UpdateLockButton();
        UpdateAnchorGrid();
    }

    private void OnSelectedChanged(object? sender, string property)
    {
        switch (property)
        {
            case nameof(OverlayProfile.Items):
                UpdateNoItems();
                UpdateGameModules();
                _preview?.Rebuild(unlocked: false);
                break;
            case nameof(OverlayProfile.ShowOnlyInGames):
                UpdateGameModules();
                break;
            case nameof(OverlayProfile.Locked):
                UpdateLockButton();
                break;
            case nameof(OverlayProfile.Anchor):
                UpdateAnchorGrid();
                break;
            case nameof(OverlayProfile.X) or nameof(OverlayProfile.Y) or nameof(OverlayProfile.Width) or nameof(OverlayProfile.Height)
                or nameof(OverlayProfile.Enabled) or nameof(OverlayProfile.EdgeMargin) or nameof(OverlayProfile.Monitor):
                break; // position only: the preview doesn't change
            case nameof(OverlayProfile.Opacity):
                if (_preview != null) _preview.Opacity = _selected!.Opacity / 100.0;
                break;
            default:
                _preview?.Rebuild(unlocked: false);
                break;
        }
    }

    private void UpdateNoItems() =>
        NoItemsText.Visibility = _selected is { Items.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    private void UpdateLockButton()
    {
        bool locked = _selected?.Locked ?? true;
        LockIcon.Text = locked ? "" : "";
        LockText.Text = locked ? "Locked" : "Unlocked – drag it";
    }

    /// <summary>"+" on the Sensors page: adds to the overlay selected here (or the first one, or a new one).</summary>
    public void AddSensor(SensorVm sensor)
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
            Toast?.Invoke($"“{sensor.OverlayLabel}” is already in {target.Name}");
            return;
        }
        AddItem(target, sensor);
        Toast?.Invoke($"Added “{sensor.OverlayLabel}” to {target.Name}");
    }

    private void AddItem(OverlayProfile target, SensorVm sensor)
    {
        var item = new OverlayItem { SensorId = sensor.Id };
        _app.Overlays.Resolve(item);
        target.Items.Add(item);
    }

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
        string lockHint = _app.Settings.HotkeyLock.Length > 0 ? $" ({_app.Settings.HotkeyLock})" : " with the padlock in the left bar";
        Toast?.Invoke(p.ShowOnlyInGames
            ? $"Created “{name}”. Place it now and lock it{lockHint}; then it appears by itself in games"
            : $"Created “{name}”. Drag it into place, then lock it{lockHint}");
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
        Toast?.Invoke($"Duplicated as “{copy.Name}”");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var result = MessageBox.Show(Window.GetWindow(this), $"Delete the overlay “{_selected.Name}”?", "Delete overlay",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        int index = OverlayList.SelectedIndex;
        _app.Settings.Overlays.Remove(_selected);
        if (_app.Settings.Overlays.Count > 0)
            OverlayList.SelectedIndex = Math.Min(index, _app.Settings.Overlays.Count - 1);
    }

    private void ExportOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        string safeName = string.Concat(_selected.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export overlay",
            FileName = (safeName.Length > 0 ? safeName : "Overlay") + OverlayExchange.Extension,
            Filter = OverlayExchange.DialogFilter,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            OverlayExchange.ExportFile(_selected, dialog.FileName);
            Toast?.Invoke($"Exported “{_selected.Name}”");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't save the file:\n{ex.Message}", "Export overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportOverlay_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import overlay", Filter = OverlayExchange.DialogFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        OverlayProfile profile;
        try
        {
            profile = OverlayExchange.ImportFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Import overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        profile.Name = NextName(profile.Name);
        foreach (var item in profile.Items) _app.Overlays.Resolve(item);
        _app.Settings.Overlays.Add(profile);
        _app.Overlays.Visible = true;
        OverlayList.SelectedItem = profile;

        int missing = profile.Items.Count(i => OverlayExtras.NameOf(i.SensorId) == null && !_app.Sensors.ById.ContainsKey(i.SensorId));
        Toast?.Invoke(missing == 0
            ? $"Imported “{profile.Name}”. Drag it into place, then lock it"
            : $"Imported “{profile.Name}” · {missing} of {profile.Items.Count} sensors aren't on this PC (shown as “Sensor not found”)");
    }

    // ---------- gaming modules ----------

    private bool _syncingModules;

    private void BuildGameModules()
    {
        foreach (var module in GameOverlay.Modules)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = module.Name });
            text.Children.Add(new TextBlock { Text = module.Description, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
            var box = new CheckBox { Style = (Style)FindResource("Switch"), Content = text, Tag = module, Width = 300, Margin = new Thickness(0, 0, 16, 8) };
            AutomationProperties.SetName(box, module.Name);
            box.Checked += GameModule_Changed;
            box.Unchecked += GameModule_Changed;
            GameModuleSwitches.Children.Add(box);
        }
    }

    private void GameModule_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingModules || _selected == null || sender is not CheckBox { Tag: GameOverlay.Module module } box) return;
        bool on = box.IsChecked == true;
        if (on && module.Key == "hardware" && GameOverlay.HardwareIds(_app.Sensors.All).Length == 0)
        {
            Toast?.Invoke("No CPU or GPU sensors were found yet");
            UpdateGameModules();
            return;
        }
        GameOverlay.Set(_selected, module, on, _app.Sensors.All);
        foreach (var item in _selected.Items) _app.Overlays.Resolve(item);
    }

    /// <summary>Shows the module switches for gaming overlays, ticked for the modules the overlay has.</summary>
    private void UpdateGameModules()
    {
        if (GameModuleSwitches.Children.Count == 0) BuildGameModules();
        bool gaming = _selected is { ShowOnlyInGames: true };
        GameModulesPanel.Visibility = gaming ? Visibility.Visible : Visibility.Collapsed;
        if (!gaming) return;

        _syncingModules = true;
        foreach (CheckBox box in GameModuleSwitches.Children)
            box.IsChecked = GameOverlay.IsOn(_selected!, (GameOverlay.Module)box.Tag, _app.Sensors.All);
        _syncingModules = false;
        GameModulesHint.Text = _app.Settings.GameFpsEnabled
            ? "Turn on what you want to see. The preview shows sample numbers until a game runs."
            : "FPS measuring is off (Settings → Gaming), so the frame numbers stay empty. Hardware rows still work.";
    }

    // ---------- sensors tab ----------

    private void AddSensors_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var extras = OverlayExtras.PickerItems();
        var picker = new SensorPickerWindow(extras.Concat(_app.Sensors.All), _selected) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return;

        foreach (var id in picker.SelectedIds)
            if (_app.Sensors.ById.TryGetValue(id, out var s) || (s = extras.Find(x => x.Id == id)) != null) AddItem(_selected, s);
        if (picker.SelectedIds.Count > 0)
            Toast?.Invoke($"Added {picker.SelectedIds.Count} sensor(s) to {_selected.Name}");
    }

    private void PlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddPlace_Click(sender, e);
    }

    private void AddPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || PlaceBox.Text.Trim().Length == 0) return;
        if (TimeZonePlaces.Find(PlaceBox.Text) is not var (zoneId, label))
        {
            Toast?.Invoke($"Couldn't find “{PlaceBox.Text.Trim()}”. Try its country or a big city nearby.");
            return;
        }
        var item = new OverlayItem { SensorId = OverlayExtras.ZoneId(zoneId), Label = label };
        _app.Overlays.Resolve(item);
        _selected.Items.Add(item);
        PlaceBox.Clear();
        string zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId).DisplayName;
        Toast?.Invoke($"Added a clock for {label} · {zone}");
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

    private void ResetItemColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OverlayItem item) item.ValueColorText = "";
    }

    // ---------- tabs ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tab } || SensorsTab == null) return;
        SensorsTab.Visibility = tab == "Sensors" ? Visibility.Visible : Visibility.Collapsed;
        StyleTab.Visibility = tab == "Style" ? Visibility.Visible : Visibility.Collapsed;
        LayoutTab.Visibility = tab == "Layout" ? Visibility.Visible : Visibility.Collapsed;
        PositionTab.Visibility = tab == "Position" ? Visibility.Visible : Visibility.Collapsed;
        WarningsTab.Visibility = tab == "Warnings" ? Visibility.Visible : Visibility.Collapsed;
        TabScroller.ScrollToTop();
    }

    // ---------- preview ----------

    private void BuildPreview()
    {
        PreviewBox.Content = null;
        _preview = null;
        if (_selected == null) return;
        _preview = new OverlayView(_selected, _app.Sensors, preview: true) { Opacity = _selected.Opacity / 100.0 };
        _preview.Rebuild(unlocked: false);
        PreviewBox.Content = _preview;
    }

    private void Backdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string kind }) Backdrop.Background = BackdropBrush(kind);
    }

    /// <summary>Test backgrounds: a dark scene, a bright one (snow, sky, menus) and a busy, colourful one.</summary>
    private static Brush BackdropBrush(string kind)
    {
        Brush Checker(Color a, Color b)
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(a), null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null, new RectangleGeometry(new Rect(16, 16, 16, 16))));
            var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 32, 32), ViewportUnits = BrushMappingMode.Absolute };
            brush.Freeze();
            return brush;
        }

        if (kind == "busy")
        {
            var group = new DrawingGroup();
            var colors = new[] { Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0xF9, 0xA8, 0x25), Color.FromRgb(0x15, 0x65, 0xC0), Color.FromRgb(0xE5, 0xE7, 0xEB), Color.FromRgb(0xC6, 0x28, 0x28) };
            for (int i = 0; i < colors.Length; i++)
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(colors[i]), null, new RectangleGeometry(new Rect(i * 24, 0, 24, 120))));
            var stripes = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 120, 120), ViewportUnits = BrushMappingMode.Absolute,
                Transform = new RotateTransform(-30),
            };
            stripes.Freeze();
            return stripes;
        }
        return kind == "bright"
            ? Checker(Color.FromRgb(0xE9, 0xEE, 0xF3), Color.FromRgb(0xF7, 0xF9, 0xFB))
            : Checker(Color.FromRgb(0x18, 0x1E, 0x27), Color.FromRgb(0x1E, 0x25, 0x30));
    }

    // ---------- style tab: fonts ----------

    private void BuildFontLists()
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var name in names) FontBox.Items.Add(FontItem(name, name));
        ValueFontBox.Items.Add(FontItem("Same as labels", ""));
        foreach (var name in names) ValueFontBox.Items.Add(FontItem(name, name));
    }

    /// <summary>Each font is shown in itself, so picking one is visual.</summary>
    private static ComboBoxItem FontItem(string label, string family)
    {
        var item = new ComboBoxItem { Content = label, Tag = family };
        if (family.Length > 0) item.FontFamily = new FontFamily(family);
        TextSearch.SetText(item, label);
        return item;
    }

    // ---------- position tab ----------

    private static readonly (OverlayAnchor Anchor, string Glyph, string Name)[] Anchors =
    {
        (OverlayAnchor.TopLeft, "↖", "Top left"), (OverlayAnchor.TopCenter, "↑", "Top centre"), (OverlayAnchor.TopRight, "↗", "Top right"),
        (OverlayAnchor.MiddleLeft, "←", "Middle left"), (OverlayAnchor.Custom, "✥", "Free – drag it anywhere"), (OverlayAnchor.MiddleRight, "→", "Middle right"),
        (OverlayAnchor.BottomLeft, "↙", "Bottom left"), (OverlayAnchor.BottomCenter, "↓", "Bottom centre"), (OverlayAnchor.BottomRight, "↘", "Bottom right"),
    };

    private void BuildAnchorGrid()
    {
        foreach (var (anchor, glyph, name) in Anchors)
        {
            var b = new RadioButton { Style = (Style)FindResource("AnchorButton"), Content = glyph, Tag = anchor, ToolTip = name };
            AutomationProperties.SetName(b, name);
            b.Checked += (_, _) =>
            {
                if (_selected == null || _selected.Anchor == anchor) return;
                _selected.Anchor = anchor;
                if (anchor == OverlayAnchor.Custom && _selected.Locked)
                    Toast?.Invoke("Unlock the overlay (padlock) to drag it where you want it");
            };
            AnchorGrid.Children.Add(b);
        }
    }

    private void UpdateAnchorGrid()
    {
        var anchor = _selected?.Anchor ?? OverlayAnchor.Custom;
        foreach (RadioButton b in AnchorGrid.Children) b.IsChecked = (OverlayAnchor)b.Tag == anchor;
    }

    private void BuildMonitorList()
    {
        string? current = _selected?.Monitor;
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Main monitor", Tag = "" });
        int n = 1;
        foreach (var s in Screen.AllScreens)
        {
            string label = $"Display {n++} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}";
            MonitorBox.Items.Add(new ComboBoxItem { Content = label, Tag = s.DeviceName });
        }
        if (current != null) MonitorBox.SelectedValue = current;
    }

    private void FitSize_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Width = _selected.Height = 0;
        _app.Overlays.RebuildAll();
    }
}
