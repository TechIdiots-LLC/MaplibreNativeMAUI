using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MapLibreNative.Maui.WPF;

/// <summary>
/// A lightweight, data-bindable marker model for <see cref="MlnMapImage.ItemsSource"/>. Populate an
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> of these from a view model and
/// bind it to <see cref="MlnMapImage.ItemsSource"/>; the map renders each marker as a coloured dot
/// with an optional text label. Changing any property (or the collection) updates the map live.
/// </summary>
public sealed class MlnMapMarker : INotifyPropertyChanged
{
    private double _latitude;
    private double _longitude;
    private string? _label;
    private Color? _color;

    /// <summary>Creates an empty marker.</summary>
    public MlnMapMarker() { }

    /// <summary>Creates a marker at the given coordinate with an optional label and colour.</summary>
    public MlnMapMarker(double latitude, double longitude, string? label = null, Color? color = null)
    {
        _latitude = latitude;
        _longitude = longitude;
        _label = label;
        _color = color;
    }

    /// <summary>Marker latitude in degrees.</summary>
    public double Latitude
    {
        get => _latitude;
        set => Set(ref _latitude, value);
    }

    /// <summary>Marker longitude in degrees.</summary>
    public double Longitude
    {
        get => _longitude;
        set => Set(ref _longitude, value);
    }

    /// <summary>Optional text label drawn next to the marker.</summary>
    public string? Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Optional marker fill colour. Defaults to a red dot when <see langword="null"/>.</summary>
    public Color? Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
