using System;

namespace MapLibreNative.Maui.WPF;

/// <summary>Event args for <see cref="MlnMapImage.MapClicked"/>.</summary>
public sealed class MlnMapClickEventArgs : EventArgs
{
    /// <summary>Physical pixel X within the map viewport at the time of the click.</summary>
    public double ScreenX { get; }
    /// <summary>Physical pixel Y within the map viewport at the time of the click.</summary>
    public double ScreenY { get; }
    /// <summary>Geographic latitude corresponding to the click position.</summary>
    public double Latitude { get; }
    /// <summary>Geographic longitude corresponding to the click position.</summary>
    public double Longitude { get; }

    internal MlnMapClickEventArgs(double screenX, double screenY, double latitude, double longitude)
    {
        ScreenX   = screenX;
        ScreenY   = screenY;
        Latitude  = latitude;
        Longitude = longitude;
    }
}
