using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Screen = System.Windows.Forms.Screen;

namespace Kelvra;

/// <summary>One switch of a gaming overlay on the Overlays page ("FPS · big number").</summary>
public sealed class ModuleRow : ObservableObject
{
    private readonly GameOverlay.Module _module;
    private readonly OverlayProfile _profile;
    private readonly Action<GameOverlay.Module, bool> _set;

    public ModuleRow(GameOverlay.Module module, OverlayProfile profile, Action<GameOverlay.Module, bool> set) =>
        (_module, _profile, _set) = (module, profile, set);

    public string Name => _module.Name;
    public string Hint => _module.Hint;
    public string Description => _module.Description;

    public bool IsOn
    {
        get => GameOverlay.IsOn(_profile, _module, App.Current.Sensors.All);
        set
        {
            _set(_module, value);
            OnPropertyChanged();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsOn));
}

/// <summary>
/// The overlay editor: your overlays and templates on the left, a live preview on a scene the shape of the monitor in
/// the middle (drag the overlay there, or snap it to a corner), and its look, what it shows and every other setting on
/// the right. Changes apply to the on-screen overlay immediately.
/// </summary>
public partial class OverlaysPage : UserControl
{
    private readonly App _app = App.Current;
    private OverlayProfile? _selected;
    private OverlayView? _preview;
    private List<ModuleRow> _modules = new();

    /// <summary>Short confirmation messages for the main window's toast.</summary>
    public event Action<string>? Toast;

    // The looks in the mockup's order; "Kelvra classic" is shown as "Original"
    private static readonly (string Style, string Label)[] Looks =
        { ("Instrument", "Instrument"), ("Minimal", "Minimal"), ("Glass", "Glass"), ("Classic", "Classic"), ("Neon", "Neon"), ("Kelvra classic", "Original") };

    private static readonly (OverlayAnchor Anchor, string Name)[] Snaps =
        { (OverlayAnchor.TopLeft, "Top left"), (OverlayAnchor.TopRight, "Top right"), (OverlayAnchor.BottomLeft, "Bottom left"), (OverlayAnchor.BottomRight, "Bottom right") };

    public OverlaysPage()
    {
        InitializeComponent();

        OverlayList.ItemsSource = _app.Settings.Overlays;
        foreach (var name in OverlayTemplates.Names.Where(n => n != "Blank"))
        {
            var b = new Button { Style = (Style)FindResource("TemplateRow"), Content = name, ToolTip = $"Start from the “{name}” template" };
            b.Click += (_, _) => CreateOverlay(name);
            TemplateButtons.Children.Add(b);
        }
        BuildLookTiles();
        foreach (var (anchor, name) in Snaps)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = name, Tag = anchor, GroupName = "Snap", Margin = new Thickness(0, 0, 8, 6) };
            chip.Click += (_, _) =>
            {
                if (_selected != null) _selected.Anchor = anchor;
            };
            SnapChips.Children.Add(chip);
        }
        BuildFontLists();
        BuildAnchorGrid();
        ((RadioButton)BackdropButtons.Children[2]).IsChecked = true;

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
            string key = _app.Settings.HotkeyOverlays;
            HotkeyRun.Text = key.Replace("+", " ");
            HotkeyTail.Text = key.Length > 0 ? " shows or hides them." : "Show or hide them with the eye in the sidebar.";
            UpdateModules();
        };
        if (_app.Settings.Overlays.Count > 0) OverlayList.SelectedIndex = 0;
    }

    // ---------- list ----------

    private void OverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected != null) _selected.Changed -= OnSelectedChanged;
        _selected = OverlayList.SelectedItem as OverlayProfile;
        if (_selected != null) _selected.Changed += OnSelectedChanged;

        Editor.DataContext = PreviewCard.DataContext = _selected;
        var shown = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = PreviewCard.Visibility = shown;
        NoOverlaySelected.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        BuildPreview();
        UpdateItemsUi();
        UpdateLockButton();
        UpdateAnchors();
        UpdateMonitorText();
        SyncLook();
        EditorScroller.ScrollToTop();
    }

    private void OnSelectedChanged(object? sender, string property)
    {
        switch (property)
        {
            case nameof(OverlayProfile.Items):
                UpdateItemsUi();
                _preview?.Rebuild(unlocked: false);
                break;
            case nameof(OverlayProfile.ShowOnlyInGames):
                UpdateItemsUi();
                break;
            case nameof(OverlayProfile.Locked):
                UpdateLockButton();
                break;
            case nameof(OverlayProfile.Anchor):
                UpdateAnchors();
                PlaceHud();
                break;
            case nameof(OverlayProfile.Monitor):
                UpdateMonitorText();
                PlaceHud();
                break;
            case nameof(OverlayProfile.X) or nameof(OverlayProfile.Y):
                if (!_dragging) PlaceHud();
                break;
            case nameof(OverlayProfile.Width) or nameof(OverlayProfile.Height) or nameof(OverlayProfile.Enabled) or nameof(OverlayProfile.EdgeMargin)
                or nameof(OverlayProfile.Name) or nameof(OverlayProfile.OnDesktop):
                break; // nothing in the preview changes
            case nameof(OverlayProfile.Opacity):
                if (_preview != null) _preview.Opacity = _selected!.Opacity / 100.0;
                break;
            default:
                _preview?.Rebuild(unlocked: false);
                SyncLook();
                break;
        }
    }

    /// <summary>A gaming overlay shows its module switches; any other overlay its sensors (and the clock-for-a-place box).</summary>
    private void UpdateItemsUi()
    {
        bool gaming = _selected is { ShowOnlyInGames: true };
        ModuleList.Visibility = gaming ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = ClockRow.Visibility = gaming ? Visibility.Collapsed : Visibility.Visible;
        NoItemsText.Visibility = !gaming && _selected is { Items.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
        GameModulesHint.Visibility = gaming && !_app.Settings.GameFpsEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateModules();
    }

    private void UpdateModules()
    {
        if (_selected is not { ShowOnlyInGames: true } p)
        {
            ModuleList.ItemsSource = null;
            return;
        }
        if (_modules.Count == 0 || ModuleList.ItemsSource == null || !ReferenceEquals(_modulesFor, p))
        {
            _modulesFor = p;
            _modules = GameOverlay.Modules.Select(m => new ModuleRow(m, p, SetModule)).ToList();
            ModuleList.ItemsSource = _modules;
        }
        else foreach (var row in _modules) row.Refresh();
    }

    private OverlayProfile? _modulesFor;

    private void SetModule(GameOverlay.Module module, bool on)
    {
        if (_selected == null) return;
        if (on && module.Key == "hardware" && GameOverlay.HardwareIds(_app.Sensors.All).Length == 0)
        {
            Toast?.Invoke("No CPU or GPU sensors were found yet");
            return;
        }
        GameOverlay.Set(_selected, module, on, _app.Sensors.All);
        foreach (var item in _selected.Items) _app.Overlays.Resolve(item);
    }

    private void UpdateLockButton()
    {
        bool locked = _selected?.Locked ?? true;
        LockIcon.Text = locked ? "" : "";
        LockText.Text = locked ? "Locked" : "Unlocked – drag it on screen";
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

    private void NewOverlay_Click(object sender, RoutedEventArgs e) => CreateOverlay("Blank");

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
        string safeName = string.Concat(_selected.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
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

    // ---------- look ----------

    private void BuildLookTiles()
    {
        foreach (var (styleName, label) in Looks)
        {
            if (OverlayStyles.Find(styleName) is not { } style) continue;
            var sample = new OverlayProfile();
            style.Apply(sample);

            var tile = new RadioButton { Style = (Style)FindResource("LookTile"), GroupName = "Look", Tag = style, ToolTip = style.Description };
            AutomationProperties.SetName(tile, label);
            var content = new StackPanel();
            content.Children.Add(Swatch(sample));
            content.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            tile.Content = content;
            tile.Click += (_, _) =>
            {
                if (_selected == null) return;
                style.Apply(_selected);
                Toast?.Invoke($"Applied the “{label}” look");
            };
            LookTiles.Children.Add(tile);
        }
    }

    /// <summary>A little piece of the look's panel: its colour and opacity, its edge; dashed when there's no panel at all.</summary>
    private static UIElement Swatch(OverlayProfile p)
    {
        if (p.BackgroundOpacity == 0)
        {
            var dashed = new Rectangle { Height = 26, RadiusX = 4, RadiusY = 4, StrokeDashArray = new DoubleCollection { 3, 2 }, StrokeThickness = 1 };
            dashed.SetResourceReference(Shape.StrokeProperty, "Line2Brush");
            return dashed;
        }
        var back = ColorUtil.Parse(p.BackgroundColor, Colors.Black);
        return new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb((byte)(p.BackgroundOpacity * 255 / 100), back.R, back.G, back.B)),
            BorderBrush = new SolidColorBrush(ColorUtil.Parse(p.BorderColor, Colors.Transparent)),
            BorderThickness = new Thickness(Math.Min(1, p.BorderThickness)),
        };
    }

    /// <summary>Ticks the look the overlay has now, if it's exactly one of them (its colours, panel and fonts).</summary>
    private void SyncLook()
    {
        foreach (RadioButton tile in LookTiles.Children)
        {
            bool same = false;
            if (_selected != null && tile.Tag is OverlayStyles.Style style)
            {
                var probe = _selected.Clone(_selected.Name);
                style.Apply(probe);
                same = Key(probe) == Key(_selected);
            }
            tile.IsChecked = same;
        }

        static string Key(OverlayProfile p) => string.Join("|", p.BackgroundColor, p.BackgroundOpacity, p.CornerRadius, p.BorderColor, p.BorderThickness,
            p.AccentColor, p.LabelColor, p.ValueColor, p.FontFamily, p.ValueFontFamily, p.ValueWeight, p.TextEffect, p.Padding);
    }

    // ---------- what it shows ----------

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

    // Drag a row by its grip onto another row to put it there
    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OverlayItem item)
            DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
    }

    private void Items_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(OverlayItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Items_Drop(object sender, DragEventArgs e)
    {
        if (_selected == null || e.Data.GetData(typeof(OverlayItem)) is not OverlayItem dragged
            || (e.OriginalSource as FrameworkElement)?.DataContext is not OverlayItem target || ReferenceEquals(dragged, target)) return;
        int from = _selected.Items.IndexOf(dragged), to = _selected.Items.IndexOf(target);
        if (from >= 0 && to >= 0) _selected.Items.Move(from, to);
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

    // ---------- preview: the overlay at real size on a scene the shape of its monitor ----------

    private void BuildPreview()
    {
        PreviewBox.Content = null;
        _preview = null;
        if (_selected == null) return;
        _preview = new OverlayView(_selected, _app.Sensors, preview: true) { Opacity = _selected.Opacity / 100.0 };
        _preview.Rebuild(unlocked: false);
        PreviewBox.Content = _preview;
    }

    private Screen MonitorOf(OverlayProfile p) => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == p.Monitor) ?? Screen.PrimaryScreen!;

    /// <summary>The monitor in WPF units. ponytail: one DPI for all monitors; mixed-DPI setups land a little off.</summary>
    private Rect MonitorRect(OverlayProfile p)
    {
        var b = MonitorOf(p).Bounds;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new Rect(b.Left / scale, b.Top / scale, b.Width / scale, b.Height / scale);
    }

    private void UpdateMonitorText()
    {
        if (_selected == null) return;
        var screens = Screen.AllScreens;
        var screen = MonitorOf(_selected);
        int n = Array.FindIndex(screens, s => s.DeviceName == screen.DeviceName) + 1;
        MonitorText.Text = $"Monitor {Math.Max(1, n)} · {screen.Bounds.Width} × {screen.Bounds.Height}";
    }

    private const double StageMargin = 24, StageTop = 44; // room for the "Drag me" tag above the overlay

    /// <summary>Puts the overlay on the stage where it sits on its monitor: at its corner or edge, or where it was dragged.</summary>
    private void PlaceHud()
    {
        if (_selected == null || Stage.ActualWidth < 50 || Hud.ActualWidth <= 0) return;
        // Real size when it fits; a big overlay is scaled down so all of it shows
        double scale = Math.Min(1, Math.Min((Stage.ActualWidth - 2 * StageMargin) / Hud.ActualWidth,
                                            (Stage.ActualHeight - StageTop - StageMargin) / Hud.ActualHeight));
        scale = Math.Max(0.1, scale);
        if (Math.Abs(HudScale - scale) > 0.001) Hud.LayoutTransform = scale < 1 ? new ScaleTransform(scale, scale) : Transform.Identity;
        SizeNote.Text = scale < 1 ? $"scaled to {scale:P0} to fit" : "real size";
        double freeW = Math.Max(0, Stage.ActualWidth - Hud.ActualWidth * scale - 2 * StageMargin);
        double freeH = Math.Max(0, Stage.ActualHeight - Hud.ActualHeight * scale - StageTop - StageMargin);
        double fx, fy;
        if (_selected.Anchor == OverlayAnchor.Custom)
        {
            var mon = MonitorRect(_selected);
            fx = (_selected.X - mon.Left) / Math.Max(1, mon.Width - Hud.ActualWidth);
            fy = (_selected.Y - mon.Top) / Math.Max(1, mon.Height - Hud.ActualHeight);
        }
        else
        {
            fx = _selected.Anchor switch
            {
                OverlayAnchor.TopCenter or OverlayAnchor.BottomCenter => 0.5,
                OverlayAnchor.TopRight or OverlayAnchor.MiddleRight or OverlayAnchor.BottomRight => 1,
                _ => 0,
            };
            fy = _selected.Anchor switch
            {
                OverlayAnchor.MiddleLeft or OverlayAnchor.MiddleRight => 0.5,
                OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight => 1,
                _ => 0,
            };
        }
        MoveHud(StageMargin + Math.Clamp(fx, 0, 1) * freeW, StageTop + Math.Clamp(fy, 0, 1) * freeH);
    }

/// <summary>Moves the overlay on the stage, with its selection ring and the "Drag me" tag above it.</summary>
    private void MoveHud(double left, double top)
    {
        double w = Hud.ActualWidth * HudScale, h = Hud.ActualHeight * HudScale;
        Canvas.SetLeft(Hud, left);
        Canvas.SetTop(Hud, top);
        foreach (var (frame, grow) in new[] { (HudRing, 1.5), (HudGlow, 6.0) })
        {
            Canvas.SetLeft(frame, left - grow);
            Canvas.SetTop(frame, top - grow);
            (frame.Width, frame.Height) = (w + 2 * grow, h + 2 * grow);
        }
        Canvas.SetLeft(HudTag, left);
        Canvas.SetTop(HudTag, top - 30);
    }

    /// <summary>How much the preview is shrunk to fit the stage (1 = real size).</summary>
    private double HudScale => (Hud.LayoutTransform as ScaleTransform)?.ScaleX ?? 1;

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceHud();
    private void Hud_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceHud();

    private bool _dragging;
    private Point _grab;

    private void Hud_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected == null) return;
        _dragging = true;
        _grab = e.GetPosition(Hud);
        Hud.CaptureMouse();
        e.Handled = true;
    }

    private void Hud_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var at = e.GetPosition(StageCanvas);
        double s = HudScale;
        double maxX = Stage.ActualWidth - Hud.ActualWidth * s - StageMargin, maxY = Stage.ActualHeight - Hud.ActualHeight * s - StageMargin;
        MoveHud(Math.Clamp(at.X - _grab.X * s, StageMargin, Math.Max(StageMargin, maxX)), Math.Clamp(at.Y - _grab.Y * s, StageTop, Math.Max(StageTop, maxY)));
    }

    /// <summary>Dropped: the same spot on the monitor, as a free position.</summary>
    private void Hud_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Hud.ReleaseMouseCapture();
        if (_selected == null) return;
        double s = HudScale;
        double freeW = Math.Max(1, Stage.ActualWidth - Hud.ActualWidth * s - 2 * StageMargin);
        double freeH = Math.Max(1, Stage.ActualHeight - Hud.ActualHeight * s - StageTop - StageMargin);
        double fx = Math.Clamp((Canvas.GetLeft(Hud) - StageMargin) / freeW, 0, 1), fy = Math.Clamp((Canvas.GetTop(Hud) - StageTop) / freeH, 0, 1);
        var mon = MonitorRect(_selected);
        _selected.Anchor = OverlayAnchor.Custom;
        _selected.X = Math.Round(mon.Left + fx * Math.Max(0, mon.Width - Hud.ActualWidth));
        _selected.Y = Math.Round(mon.Top + fy * Math.Max(0, mon.Height - Hud.ActualHeight));
    }

    private void Backdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string kind }) Stage.Background = BackdropBrush(kind);
    }

    /// <summary>Test backgrounds: a dark scene, a bright one (snow, sky, menus) and a game scene (a sunset over mountains).</summary>
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

        if (kind == "scene")
        {
            static Color C(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var scene = new DrawingGroup();
            var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            foreach (var (rgb, at) in new[] { (0x1B2440u, 0.0), (0x5A3D5Cu, 0.42), (0xD07A54u, 0.62), (0x2A1D22u, 0.63), (0x120D12u, 1.0) })
                sky.GradientStops.Add(new GradientStop(C(rgb), at));
            scene.Children.Add(new GeometryDrawing(sky, null, new RectangleGeometry(new Rect(0, 0, 800, 500))));
            var sun = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
            sun.GradientStops.Add(new GradientStop(C(0xFFCF8A), 0));
            sun.GradientStops.Add(new GradientStop(C(0xFF9A55), 0.16));
            sun.GradientStops.Add(new GradientStop(C(0xFF783C, 0), 1));
            scene.Children.Add(new GeometryDrawing(sun, null, new EllipseGeometry(new Point(576, 290), 180, 180)));
            foreach (var (path, rgb) in new[]
            {
                ("M0,410 L90,350 L160,400 L250,320 L330,390 L420,340 L520,410 L600,360 L700,420 L800,370 V500 H0 Z", 0x2B1F2Cu),
                ("M0,450 L120,410 L220,440 L330,400 L470,455 L560,425 L680,465 L800,430 V500 H0 Z", 0x181218u),
                ("M0,485 L800,465 V500 H0 Z", 0x0D0A0Eu),
            })
                scene.Children.Add(new GeometryDrawing(new SolidColorBrush(C(rgb)), null, Geometry.Parse(path)));
            var brush = new DrawingBrush(scene) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        return kind == "bright"
            ? Checker(Color.FromRgb(0xE9, 0xEE, 0xF3), Color.FromRgb(0xF7, 0xF9, 0xFB))
            : Checker(Color.FromRgb(0x18, 0x1E, 0x27), Color.FromRgb(0x1E, 0x25, 0x30));
    }

    // ---------- text: fonts ----------

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

    // ---------- position ----------

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
            };
            AnchorGrid.Children.Add(b);
        }
    }

    /// <summary>The anchor grid and the snap chips show where the overlay is pinned.</summary>
    private void UpdateAnchors()
    {
        var anchor = _selected?.Anchor ?? OverlayAnchor.Custom;
        foreach (RadioButton b in AnchorGrid.Children) b.IsChecked = (OverlayAnchor)b.Tag == anchor;
        foreach (var chip in SnapChips.Children.OfType<RadioButton>()) chip.IsChecked = (OverlayAnchor)chip.Tag == anchor;
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
