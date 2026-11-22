namespace MapLibreNative.Maui;

/// <summary>What the GPS control does with the location it's given (UpdateGpsLocation): its top button cycles these.</summary>
public enum GpsTrackingMode
{
    /// <summary>Nothing: no location shown.</summary>
    Off,
    /// <summary>The location is shown; the camera stays where it is.</summary>
    Show,
    /// <summary>The location is shown and the camera follows it. Panning the map drops back to <see cref="Show"/>.</summary>
    Follow,
}

/// <summary>Which way the camera faces as the GPS control moves it: its bottom button cycles these.</summary>
public enum GpsBearingMode
{
    /// <summary>The map keeps the bearing it has.</summary>
    Free,
    /// <summary>North up.</summary>
    NorthUp,
    /// <summary>Turned with the GPS fix's bearing (the direction of travel). Rotating the map drops back to <see cref="Free"/>.</summary>
    GpsBearing,
}
