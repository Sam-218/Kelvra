using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kelvra;

/// <summary>Minimal INotifyPropertyChanged base for the bindable models (sensors, overlays, alert rules …).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stores the value and raises PropertyChanged. Returns false (and stays silent) if nothing changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
