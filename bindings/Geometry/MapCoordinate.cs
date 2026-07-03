namespace MapLibreNative.Maui.Geometry;

/// <summary>
/// A geographic coordinate (WGS84) used by the map primitives (<see cref="MapSpan"/>,
/// <see cref="Distance"/>, <see cref="GeographyUtils"/>).
/// </summary>
/// <remarks>
/// This is the UI-agnostic coordinate type for the shared primitives, so they can be
/// consumed from both the WPF host and the MAUI handlers without depending on
/// <c>Microsoft.Maui.Devices.Sensors.Location</c>. It interoperates with the
/// <c>(double Lat, double Lon)</c> tuples used throughout the bindings via implicit
/// conversions.
/// </remarks>
public readonly record struct MapCoordinate(double Latitude, double Longitude)
{
    /// <summary>Creates a coordinate from a <c>(Lat, Lon)</c> tuple.</summary>
    public static implicit operator MapCoordinate((double Lat, double Lon) t)
        => new(t.Lat, t.Lon);

    /// <summary>Converts the coordinate to a <c>(Lat, Lon)</c> tuple.</summary>
    public static implicit operator (double Lat, double Lon)(MapCoordinate c)
        => (c.Latitude, c.Longitude);

    /// <inheritdoc/>
    public override string ToString() => $"{Latitude}, {Longitude}";
}
