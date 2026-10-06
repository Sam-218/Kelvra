using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;

namespace Kelvra;

/// <summary>
/// Mini mode: a small window (optionally always on top) with CPU, GPU, memory and network at a glance, for a second
/// screen or a corner of the desktop. An alternative to in-game overlays for people who don't want them in the game.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<MiniCard> _cards = new();
    private readonly Dictionary<MiniCard, DevicePanel> _devices = new();
    private readonly SideStat _memory = new("MEMORY");
    private readonly SideStat _network = new("NETWORK");
    private MiniCard? _memoryCard, _networkCard;
    private float _networkPeak;
    private int _builtForCount = -1;

    public MiniWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;

        var s = _app.Settings;
        Width = Math.Max(MinWidth, s.MiniWidth);
        Height = Math.Max(MinHeight, s.MiniHeight);
        if (s.MiniLeft is double left && s.MiniTop is double top && OnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            // First time: bottom right of the work area
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = SystemParameters.WorkArea.Bottom - Height - 16;
        }
        PinButton.IsChecked = s.MiniTopmost;
        Topmost = s.MiniTopmost;
        UpdatePin();

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _app.Sensors.Updated += OnUpdated;
        _app.Overlays.StateChanged += UpdateButtons;
        Closed += (_, _) =>
        {
            _app.Sensors.Updated -= OnUpdated;
            _app.Overlays.StateChanged -= UpdateButtons;
        };
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
        UpdateButtons();
        OnUpdated();
    }

    private static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 40 && top > SystemParameters.VirtualScreenTop - 10 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void Remember()
    {
        if (WindowState != WindowState.Normal || !IsLoaded) return;
        var s = _app.Settings;
        (s.MiniLeft, s.MiniTop, s.MiniWidth, s.MiniHeight) = (Left, Top, ActualWidth, ActualHeight);
        _app.MarkDirty();
    }

    private void OnUpdated()
    {
        var sensors = _app.Sensors;
        LiveDot.Fill = (System.Windows.Media.Brush)FindResource(sensors.Error != null ? "HotBrush" : "GoodBrush");
        if (!sensors.Loaded || !IsVisible) return;

        if (sensors.All.Count != _builtForCount)
        {
            _builtForCount = sensors.All.Count;
            _cards.Clear();
            _devices.Clear();
            foreach (var p in Overview.Panels(sensors.All))
            {
                var card = new MiniCard(p.Kind, p.Device, "LOAD", "CLOCK", "POWER");
                _devices[card] = p;
                _cards.Add(card);
            }
            _cards.Add(_memoryCard = new MiniCard("MEMORY", "", "IN USE"));
            _cards.Add(_networkCard = new MiniCard("NETWORK", "All adapters", "UPLOAD"));
        }

        foreach (var (card, panel) in _devices)
        {
            panel.Update(sensors.All);
            card.From(panel);
        }

        Overview.UpdateMemory(_memory, sensors.All);
        if (_memoryCard != null)
        {
            _memoryCard.Hero = _memory.Value.Replace(" %", "");
            _memoryCard.HeroUnit = "%";
            _memoryCard.Stats[0].Value = _memory.Detail.Replace(" in use", "");
            _memoryCard.SparkId = sensors.All.FirstOrDefault(x => x.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory
                                                                 && x.Type == LibreHardwareMonitor.Hardware.SensorType.Load
                                                                 && !x.GroupName.Contains("Virtual"))?.Id;
        }

        Overview.UpdateNetwork(_network, sensors.All, ref _networkPeak);
        if (_networkCard != null)
        {
            _networkCard.Hero = _network.Value;
            _networkCard.HeroUnit = "";
            _networkCard.Stats[0].Value = _network.Detail.TrimStart('↑', ' ');
        }
    }

    // ---------- title bar ----------

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _app.Settings.MiniTopmost = PinButton.IsChecked == true;
        Topmost = _app.Settings.MiniTopmost;
        _app.MarkDirty();
        UpdatePin();
    }

    private void UpdatePin()
    {
        string tip = PinButton.IsChecked == true ? "Always on top · click to let other windows cover it" : "Keep this window on top of others";
        PinButton.ToolTip = tip;
        AutomationProperties.SetName(PinButton, "Keep on top");
    }

    private void OpenFull_Click(object sender, RoutedEventArgs e) => _app.MainView.ShowFromTray();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.MiniOpen = false;
        _app.MarkDirty();
        Close();
    }

    // ---------- quick actions ----------

    private void Overlays_Click(object sender, RoutedEventArgs e) => _app.Overlays.Visible = !_app.Overlays.Visible;

    private void Lock_Click(object sender, RoutedEventArgs e) => _app.Overlays.ToggleLockAll();

    private void UpdateButtons()
    {
        OverlaysButton.Style = (Style)FindResource(_app.Overlays.Visible ? "AccentButton" : typeof(System.Windows.Controls.Button));
        OverlaysButton.Content = _app.Overlays.Visible ? "Overlays on" : "Overlays off";
        LockButton.Content = _app.Overlays.AnyUnlocked ? "Lock overlays" : "Unlock to move";
    }
}
