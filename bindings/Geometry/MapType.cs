// Adapted from dotnet/maui (MIT License):
// src/Core/maps/src/Primitives/MapType.cs
namespace MapLibreNative.Maui.Geometry;

/// <summary>
/// Specifies which visual representation should be shown for a map.
/// </summary>
/// <remarks>
/// MapLibre selects the visual representation via its style URL/JSON, so this enum is provided
/// primarily for API parity with <c>Microsoft.Maui.Maps</c>. Consumers map each value to an
/// appropriate style themselves.
/// </remarks>
public enum MapType
{
    /// <summary>A schematic overview of all roads, streets, etc.</summary>
    Street,

    /// <summary>Satellite imagery.</summary>
    Satellite,

    /// <summary>Satellite imagery with a street map overlay.</summary>
    Hybrid
}
