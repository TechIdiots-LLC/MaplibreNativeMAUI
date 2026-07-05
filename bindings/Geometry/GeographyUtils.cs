// Adapted from dotnet/maui (MIT License):
// src/Core/maps/src/Primitives/GeographyUtils.cs
// Ported to be UI-agnostic (uses MapCoordinate instead of Microsoft.Maui.Devices.Sensors.Location)
// and the ICircleMapElement overload is replaced with an explicit center/radius overload.
using System;
using System.Collections.Generic;

namespace MapLibreNative.Maui.Geometry;

/// <summary>
/// Provides geography-related utility methods.
/// </summary>
public static class GeographyUtils
{
    internal const double EarthRadiusKm = 6371;

    /// <summary>Converts degrees to radians.</summary>
    public static double ToRadians(this double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Converts radians to degrees.</summary>
    public static double ToDegrees(this double radians) => radians / Math.PI * 180.0;

    /// <summary>
    /// Calculates the circumference positions that approximate a circle on the map, given its
    /// center and radius. Useful for rendering a circle as a GeoJSON polygon ring.
    /// </summary>
    /// <param name="center">The center of the circle.</param>
    /// <param name="radius">The radius of the circle.</param>
    /// <param name="segments">The number of segments used to approximate the circle (default 360).</param>
    /// <returns>A closed ring of positions approximating the circle's circumference.</returns>
    public static List<MapCoordinate> ToCircumferencePositions(MapCoordinate center, Distance radius, int segments = 360)
    {
        var positions = new List<MapCoordinate>(segments + 1);
        double centerLatitude = center.Latitude.ToRadians();
        double centerLongitude = center.Longitude.ToRadians();
        double distance = radius.Kilometers / EarthRadiusKm;

        for (int i = 0; i < segments; i++)
        {
            double angleInRadians = (i * 360.0 / segments).ToRadians();
            double latitude = Math.Asin(Math.Sin(centerLatitude) * Math.Cos(distance) +
                                        Math.Cos(centerLatitude) * Math.Sin(distance) * Math.Cos(angleInRadians));
            double longitude = centerLongitude +
                               Math.Atan2(Math.Sin(angleInRadians) * Math.Sin(distance) * Math.Cos(centerLatitude),
                                          Math.Cos(distance) - Math.Sin(centerLatitude) * Math.Sin(latitude));

            positions.Add(new MapCoordinate(latitude.ToDegrees(), longitude.ToDegrees()));
        }

        // Close the ring with a copy of the first position rather than computing the 360°
        // vertex trigonometrically: cos(2π) is not bit-identical to cos(0), so for some radii
        // the computed endpoint differs by one ulp and GeoJSON consumers that require exactly
        // closed rings (e.g. GeoJSON.Text's Polygon) reject or throw.
        positions.Add(positions[0]);

        return positions;
    }
}
