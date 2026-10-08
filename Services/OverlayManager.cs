using System.Collections.Specialized;

namespace Kelvra;

/// <summary>Creates, updates and closes one <see cref="OverlayWindow"/> per enabled overlay profile.</summary>
public sealed class OverlayManager
{
    private readonly AppSettings _settings;
    private readonly SensorStore _store;
    private readonly Dictionary<OverlayProfile, OverlayWindow> _windows = new();

    public event Action? Dirty;
    public event Action? StateChanged;

    /// <summary>Game numbers refresh 4× a second while a game runs; everything else follows the sensor poll.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>A game is being measured (set by the app); the fast refresh only runs then.</summary>
    public Func<bool> GameActive { get; set; } = () => false;

    public OverlayManager(AppSettings settings, SensorStore store)
    {
        _settings = settings;
        _store = store;
        _gameTimer.Tick += (_, _) =>
        {
            if (!GameActive()) return;
            foreach (var w in _windows.Values) w.UpdateGameValues();
        };
        _gameTimer.Start();
        foreach (var p in settings.Overlays) p.Changed += OnProfileChanged;
        settings.Overlays.CollectionChanged += OnOverlaysChanged;
    }

    public bool Visible
    {
        get => _settings.OverlaysVisible;
        set
        {
            if (_settings.OverlaysVisible == value) return;
            _settings.OverlaysVisible = value;
            Sync();
            Dirty?.Invoke();
            StateChanged?.Invoke();
        }
    }

    public bool AnyUnlocked => _settings.Overlays.Any(p => p.Enabled && !p.Locked);

    public void ToggleLockAll()
    {
        bool lockAll = AnyUnlocked;
        foreach (var p in _settings.Overlays) p.Locked = lockAll;
        if (!lockAll) Visible = true;
    }

    /// <summary>
    /// A game can be seen (set by the app from <see cref="GameMonitor.GameVisible"/>): in front, or on its own monitor
    /// while you use another one. Gaming overlays only show then.
    /// </summary>
    public Func<bool> GameVisible { get; set; } = () => false;

    /// <summary>Gaming overlays on/off (their own hotkey), separate from <see cref="Visible"/>.</summary>
    public bool GameOverlaysVisible
    {
        get => _settings.GameOverlaysVisible;
        set
        {
            if (_settings.GameOverlaysVisible == value) return;
            _settings.GameOverlaysVisible = value;
            Sync();
            Dirty?.Invoke();
        }
    }

    /// <summary>Whether a profile's window should be on screen now. Unlocked gaming overlays show anyway, so they can be placed.</summary>
    internal static bool Wanted(OverlayProfile p, bool visible, bool gameOverlaysVisible, bool gameVisible) =>
        visible && p.Enabled && (!p.ShowOnlyInGames || !p.Locked || (gameOverlaysVisible && gameVisible));

    public void Sync()
    {
        bool game = GameVisible();
        foreach (var p in _settings.Overlays)
        {
            bool want = Wanted(p, Visible, GameOverlaysVisible, game);
            bool has = _windows.ContainsKey(p);
            if (want && !has)
            {
                var w = new OverlayWindow(p, _store);
                _windows[p] = w;
                w.Show();
            }
            else if (!want && has)
            {
                _windows[p].Close();
                _windows.Remove(p);
            }
        }

        foreach (var orphan in _windows.Keys.Where(p => !_settings.Overlays.Contains(p)).ToList())
        {
            _windows[orphan].Close();
            _windows.Remove(orphan);
        }
    }

    public void UpdateValues()
    {
        foreach (var w in _windows.Values) w.UpdateValues();
    }

    public void RebuildAll()
    {
        foreach (var w in _windows.Values) w.Rebuild();
    }

    /// <summary>Fills in display names for overlay items (shown in the editor).</summary>
    public void ResolveNames()
    {
        foreach (var p in _settings.Overlays)
            foreach (var item in p.Items)
                Resolve(item);
    }

    public void Resolve(OverlayItem item)
    {
        if (OverlayExtras.NameOf(item.SensorId) is string extra)
        {
            item.SensorName = extra;
            item.HardwareName = OverlayExtras.GroupOf(item.SensorId);
        }
        else if (_store.ById.TryGetValue(item.SensorId, out var s))
        {
            item.SensorName = s.OverlayLabel;
            item.HardwareName = s.GroupName;
        }
        else if (_store.Loaded)
        {
            item.SensorName = "Sensor not found";
            item.HardwareName = item.SensorId;
        }
    }

    public void CloseAll()
    {
        foreach (var w in _windows.Values) w.Close();
        _windows.Clear();
    }

    private void OnOverlaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (OverlayProfile p in e.OldItems) p.Changed -= OnProfileChanged;
        if (e.NewItems != null)
        {
            foreach (OverlayProfile p in e.NewItems)
            {
                p.Changed += OnProfileChanged;
                foreach (var item in p.Items) Resolve(item);
            }
        }
        Sync();
        Dirty?.Invoke();
        StateChanged?.Invoke();
    }

    private void OnProfileChanged(object? sender, string property)
    {
        if (property is nameof(OverlayProfile.Enabled) or nameof(OverlayProfile.ShowOnlyInGames)) Sync();
        else if (property == nameof(OverlayProfile.Locked) && sender is OverlayProfile { ShowOnlyInGames: true }) Sync();
        if (property is nameof(OverlayProfile.Enabled) or nameof(OverlayProfile.Locked)) StateChanged?.Invoke();
        Dirty?.Invoke();
    }
}
