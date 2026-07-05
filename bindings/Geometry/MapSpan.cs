// Adapted from dotnet/maui (MIT License):
// src/Core/maps/src/Primitives/MapSpan.cs
// Ported to be UI-agnostic (uses MapCoordinate instead of Microsoft.Maui.Devices.Sensors.Location)
// and adds ToZoomLevel() to bridge a span to a MapLibre slippy-map zoom level.
using System;

namespace MapLibreNative.Maui.Geometry;

/// <summary>
/// Represents a rectangular region on the map, defined by a center coordinate and a span in degrees.
/// </summary>
public sealed class MapSpan
{
    const double EarthCircumferenceKm = GeographyUtils.EarthRadiusKm * 2 * Math.PI;
    const double MinimumRangeDegrees = 0.001 / EarthCircumferenceKm * 360; // 1 meter

    /// <summary>
    /// Initializes a new instance of the <see cref="MapSpan"/> class with the specified center and span in degrees.
    /// </summary>
    /// <param name="center">The center coordinate of the span.</param>
    /// <param name="latitudeDegrees">The latitude span in degrees.</param>
    /// <param name="longitudeDegrees">The longitude span in degrees.</param>
    public MapSpan(MapCoordinate center, double latitudeDegrees, double longitudeDegrees)
    {
        Center = center;
        LatitudeDegrees = Math.Min(Math.Max(latitudeDegrees, MinimumRangeDegrees), 90.0);
        LongitudeDegrees = Math.Min(Math.Max(longitudeDegrees, MinimumRangeDegrees), 180.0);
    }

    /// <summary>Gets the center coordinate of this span.</summary>
    public MapCoordinate Center { get; }

    /// <summary>Gets the latitude span in degrees.</summary>
    public double LatitudeDegrees { get; }

    /// <summary>Gets the longitude span in degrees.</summary>
    public double LongitudeDegrees { get; }

    /// <summary>Gets the approximate radius of the span.</summary>
    public Distance Radius
    {
        get
        {
            double latKm = LatitudeDegreesToKm(LatitudeDegrees);
            double longKm = LongitudeDegreesToKm(Center, LongitudeDegrees);
            return new Distance(1000 * Math.Min(latKm, longKm) / 2);
        }
    }

    /// <summary>
    /// Converts this span to a MapLibre slippy-map zoom level using the Spherical Mercator
    /// relationship <c>zoom = log2(360 / degrees)</c>, taking the tighter of the two axes and
    /// clamping to the 0–24 range MapLibre supports.
    /// </summary>
    /// <returns>A zoom level suitable for passing to camera methods (JumpTo/EaseTo/FlyTo).</returns>
    public double ToZoomLevel()
    {
        double zoomLat = Math.Log2(360.0 / LatitudeDegrees);
        double zoomLon = Math.Log2(360.0 / LongitudeDegrees);
        return Math.Clamp(Math.Min(zoomLat, zoomLon), 0, 24);
    }

    /// <summary>
    /// Creates a new <see cref="MapSpan"/> with latitude clamped to the specified bounds.
    /// </summary>
    public MapSpan ClampLatitude(double north, double south)
    {
        north = Math.Min(Math.Max(north, 0), 90);
        south = Math.Max(Math.Min(south, 0), -90);
        double lat = Math.Max(Math.Min(Center.Latitude, north), south);
        double maxDLat = Math.Min(north - lat, -south + lat) * 2;
        return new MapSpan(new MapCoordinate(lat, Center.Longitude), Math.Min(LatitudeDegrees, maxDLat), LongitudeDegrees);
    }

    /// <summary>Creates a new <see cref="MapSpan"/> from a center coordinate and radius.</summary>
    public static MapSpan FromCenterAndRadius(MapCoordinate center, Distance radius)
    {
        return new MapSpan(center, 2 * DistanceToLatitudeDegrees(radius), 2 * DistanceToLongitudeDegrees(center, radius));
    }

    /// <summary>Creates a new <see cref="MapSpan"/> with the specified zoom factor applied.</summary>
    /// <param name="zoomFactor">The zoom factor. Values greater than 1 zoom in, values less than 1 zoom out.</param>
    public MapSpan WithZoom(double zoomFactor)
    {
        double maxDLat = Math.Min(90 - Center.Latitude, 90 + Center.Latitude) * 2;
        return new MapSpan(Center, Math.Min(LatitudeDegrees / zoomFactor, maxDLat), LongitudeDegrees / zoomFactor);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (obj is null)
            return false;
        if (ReferenceEquals(this, obj))
            return true;
        return obj is MapSpan other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = Center.GetHashCode();
            hashCode = (hashCode * 397) ^ LongitudeDegrees.GetHashCode();
            hashCode = (hashCode * 397) ^ LatitudeDegrees.GetHashCode();
            return hashCode;
        }
    }

    /// <summary>Determines whether two <see cref="MapSpan"/> instances are equal.</summary>
    public static bool operator ==(MapSpan? left, MapSpan? right) => Equals(left, right);

    /// <summary>Determines whether two <see cref="MapSpan"/> instances are not equal.</summary>
    public static bool operator !=(MapSpan? left, MapSpan? right) => !Equals(left, right);

    static double DistanceToLatitudeDegrees(Distance distance)
    {
        return distance.Kilometers / EarthCircumferenceKm * 360;
    }

    static double DistanceToLongitudeDegrees(MapCoordinate location, Distance distance)
    {
        double latCircumference = LatitudeCircumferenceKm(location);
        return distance.Kilometers / latCircumference * 360;
    }

    bool Equals(MapSpan other)
    {
        return Center.Equals(other.Center) && LongitudeDegrees.Equals(other.LongitudeDegrees) && LatitudeDegrees.Equals(other.LatitudeDegrees);
    }

    static double LatitudeCircumferenceKm(MapCoordinate location)
    {
        return EarthCircumferenceKm * Math.Cos(location.Latitude * Math.PI / 180.0);
    }

    static double LatitudeDegreesToKm(double latitudeDegrees)
    {
        return EarthCircumferenceKm * latitudeDegrees / 360;
    }

    static double LongitudeDegreesToKm(MapCoordinate location, double longitudeDegrees)
    {
        double latCircumference = LatitudeCircumferenceKm(location);
        return latCircumference * longitudeDegrees / 360;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Center}, {LatitudeDegrees}, {LongitudeDegrees}";
    }
}
