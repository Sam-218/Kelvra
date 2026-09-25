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

    public OverlayManager(AppSettings settings, SensorStore store)
    {
        _settings = settings;
        _store = store;
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

    public void Sync()
    {
        foreach (var p in _settings.Overlays)
        {
            bool want = Visible && p.Enabled;
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
        if (_store.ById.TryGetValue(item.SensorId, out var s))
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
        if (property == nameof(OverlayProfile.Enabled)) Sync();
        if (property is nameof(OverlayProfile.Enabled) or nameof(OverlayProfile.Locked)) StateChanged?.Invoke();
        Dirty?.Invoke();
    }
}
