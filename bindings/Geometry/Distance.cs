// Adapted from dotnet/maui (MIT License):
// src/Core/maps/src/Primitives/Distance.cs
// Ported to be UI-agnostic (uses MapCoordinate instead of Microsoft.Maui.Devices.Sensors.Location).
using System;
using System.Diagnostics;

namespace MapLibreNative.Maui.Geometry;

/// <summary>
/// Represents a distance in meters, miles, or kilometers.
/// </summary>
public readonly struct Distance
{
    const double MetersPerMile = 1609.344;
    const double MetersPerKilometer = 1000.0;

    /// <summary>
    /// Initializes a new instance of the <see cref="Distance"/> struct with a value in meters.
    /// </summary>
    /// <param name="meters">The distance in meters.</param>
    public Distance(double meters)
    {
        Meters = meters;
    }

    /// <summary>Gets the distance in meters.</summary>
    public double Meters { get; }

    /// <summary>Gets the distance in miles.</summary>
    public double Miles => Meters / MetersPerMile;

    /// <summary>Gets the distance in kilometers.</summary>
    public double Kilometers => Meters / MetersPerKilometer;

    /// <summary>Creates a new <see cref="Distance"/> from a value in miles.</summary>
    public static Distance FromMiles(double miles)
    {
        if (miles < 0)
        {
            Debug.WriteLine("Negative values for distance not supported");
            miles = 0;
        }
        return new Distance(miles * MetersPerMile);
    }

    /// <summary>Creates a new <see cref="Distance"/> from a value in meters.</summary>
    public static Distance FromMeters(double meters)
    {
        if (meters < 0)
        {
            Debug.WriteLine("Negative values for distance not supported");
            meters = 0;
        }
        return new Distance(meters);
    }

    /// <summary>Creates a new <see cref="Distance"/> from a value in kilometers.</summary>
    public static Distance FromKilometers(double kilometers)
    {
        if (kilometers < 0)
        {
            Debug.WriteLine("Negative values for distance not supported");
            kilometers = 0;
        }
        return new Distance(kilometers * MetersPerKilometer);
    }

    /// <summary>
    /// Calculates the great-circle distance between two coordinates using the Haversine formula.
    /// </summary>
    /// <param name="position1">The first coordinate.</param>
    /// <param name="position2">The second coordinate.</param>
    /// <returns>The distance between the two coordinates.</returns>
    public static Distance BetweenPositions(MapCoordinate position1, MapCoordinate position2)
    {
        var latitude1 = position1.Latitude.ToRadians();
        var longitude1 = position1.Longitude.ToRadians();

        var latitude2 = position2.Latitude.ToRadians();
        var longitude2 = position2.Longitude.ToRadians();

        var distance = Math.Sin((latitude2 - latitude1) / 2.0);
        distance *= distance;

        var intermediate = Math.Sin((longitude2 - longitude1) / 2.0);
        intermediate *= intermediate;

        distance = distance + Math.Cos(latitude1) * Math.Cos(latitude2) * intermediate;
        distance = 2 * GeographyUtils.EarthRadiusKm * Math.Atan2(Math.Sqrt(distance), Math.Sqrt(1 - distance));

        return FromKilometers(distance);
    }

    /// <summary>Determines whether this instance is equal to another <see cref="Distance"/>.</summary>
    public bool Equals(Distance other) => Meters.Equals(other.Meters);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Distance other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Meters.GetHashCode();

    /// <summary>Determines whether two <see cref="Distance"/> instances are equal.</summary>
    public static bool operator ==(Distance left, Distance right) => left.Equals(right);

    /// <summary>Determines whether two <see cref="Distance"/> instances are not equal.</summary>
    public static bool operator !=(Distance left, Distance right) => !left.Equals(right);
}
