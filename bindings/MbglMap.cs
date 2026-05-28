/**
 * MbglMap.cs — Typed C# wrapper around the native mbgl_map_t handle.
 *
 * Lifetime: must be disposed on the same thread as its MbglRunLoop.
 * The MbglFrontend must outlive the MbglMap.
 */
using System.Runtime.InteropServices;

namespace Maui.MapLibre.Native;

/// <summary>Wraps <c>mbgl_map_t*</c>. Dispose on the render thread.</summary>
public sealed class MbglMap : IDisposable
{
    internal IntPtr Handle { get; private set; }

    // Keep a GC handle to the native delegate so it isn't collected
    private GCHandle _observerHandle;

    public MbglMap(
        MbglFrontend frontend,
        MbglRunLoop runLoop,
        string? cachePath = null,
        string? assetPath = null,
        float   pixelRatio = 1.0f,
        Action<string, string?>? observer = null)
    {
        NativeMethods.MapObserverFn? nativeObserver = null;
        if (observer != null)
        {
            nativeObserver = (eventName, detail, _) => observer(eventName, detail);
            _observerHandle = GCHandle.Alloc(nativeObserver);
        }

        Handle = NativeMethods.MapCreate(
            frontend.Handle, runLoop.Handle,
            cachePath, assetPath,
            pixelRatio,
            nativeObserver, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("mbgl_map_create returned null.");

        // mbgl_map_create transfers ownership of the frontend pointer into the
        // native CabiMap struct. Calling mbgl_frontend_destroy afterwards would
        // be a double-free (0xc0000374 heap corruption). Zero the C# handle so
        // MbglFrontend.Dispose() becomes a no-op from this point forward.
        frontend.TransferOwnership();
    }

    public void SetStyleUrl(string url)  => NativeMethods.MapSetStyleUrl(Handle, url);
    public void SetStyleJson(string json) => NativeMethods.MapSetStyleJson(Handle, json);

    public void SetSize(int widthPx, int heightPx)
        => NativeMethods.MapSetSize(Handle, widthPx, heightPx);

    public void JumpTo(double lat, double lon, double zoom, double bearing = 0, double pitch = 0)
        => NativeMethods.MapJumpTo(Handle, lat, lon, zoom, bearing, pitch);

    public void EaseTo(double lat, double lon, double zoom, double bearing, double pitch, long durationMs)
        => NativeMethods.MapEaseTo(Handle, lat, lon, zoom, bearing, pitch, durationMs);

    public void FlyTo(double lat, double lon, double zoom, double bearing, double pitch, long durationMs)
        => NativeMethods.MapFlyTo(Handle, lat, lon, zoom, bearing, pitch, durationMs);

    /// <summary>Set geographic constraints and zoom/pitch limits.
    /// Pass <see cref="double.NaN"/> for any parameter to leave it unconstrained.</summary>
    public void SetBounds(double latSw = double.NaN, double lonSw = double.NaN,
                          double latNe = double.NaN, double lonNe = double.NaN,
                          double minZoom = double.NaN, double maxZoom = double.NaN,
                          double minPitch = double.NaN, double maxPitch = double.NaN)
        => NativeMethods.MapSetBounds(Handle, latSw, lonSw, latNe, lonNe,
                                      minZoom, maxZoom, minPitch, maxPitch);

    /// <summary>Returns the CameraOptions (lat, lon, zoom, bearing, pitch) that fits the
    /// given bounds with optional screen padding (top, left, bottom, right in pixels).</summary>
    public (double Lat, double Lon, double Zoom, double Bearing, double Pitch)
        CameraForBounds(double latSw, double lonSw, double latNe, double lonNe,
                        double padTop = 0, double padLeft = 0,
                        double padBottom = 0, double padRight = 0)
    {
        NativeMethods.MapCameraForBounds(Handle, latSw, lonSw, latNe, lonNe,
            padTop, padLeft, padBottom, padRight,
            out var lat, out var lon, out var zoom, out var bearing, out var pitch);
        return (lat, lon, zoom, bearing, pitch);
    }

    public (double X, double Y) PixelForLatLng(double lat, double lon)
    {
        NativeMethods.MapPixelForLatLng(Handle, lat, lon, out var x, out var y);
        return (x, y);
    }

    public (double Lat, double Lon) LatLngForPixel(double x, double y)
    {
        NativeMethods.MapLatLngForPixel(Handle, x, y, out var lat, out var lon);
        return (lat, lon);
    }

    public void SetProjectionMode(bool axonometric = false, double xSkew = 0.0, double ySkew = 1.0)
        => NativeMethods.MapSetProjectionMode(Handle, axonometric ? 1 : 0, xSkew, ySkew);

    /// <summary>Query rendered features at a screen point. Returns a GeoJSON FeatureCollection string,
    /// or null if the renderer is not ready.</summary>
    /// <param name="layerIds">Optional comma-separated layer IDs to restrict the query.</param>
    public string? QueryRenderedFeaturesAtPoint(double x, double y, string? layerIds = null)
    {
        var ptr = NativeMethods.MapQueryRenderedFeaturesAtPoint(Handle, x, y, layerIds);
        if (ptr == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Query rendered features in a screen bounding box.</summary>
    public string? QueryRenderedFeaturesInBox(double x1, double y1, double x2, double y2,
                                               string? layerIds = null)
    {
        var ptr = NativeMethods.MapQueryRenderedFeaturesInBox(Handle, x1, y1, x2, y2, layerIds);
        if (ptr == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        NativeMethods.FreeString(ptr);
        return result;
    }

    public double Zoom    => NativeMethods.MapGetZoom(Handle);
    public double Bearing => NativeMethods.MapGetBearing(Handle);
    public double Pitch   => NativeMethods.MapGetPitch(Handle);

    public (double Lat, double Lon) Center
    {
        get
        {
            NativeMethods.MapGetCenter(Handle, out var lat, out var lon);
            return (lat, lon);
        }
    }

    public void SetMinZoom(double zoom) => NativeMethods.MapSetMinZoom(Handle, zoom);
    public void SetMaxZoom(double zoom) => NativeMethods.MapSetMaxZoom(Handle, zoom);

    public void OnScroll(double delta, double cx, double cy)
        => NativeMethods.MapOnScroll(Handle, delta, cx, cy);
    public void OnDoubleTap(double x, double y)
        => NativeMethods.MapOnDoubleTap(Handle, x, y);
    public void OnPanStart(double x, double y)
        => NativeMethods.MapOnPanStart(Handle, x, y);
    public void OnPanMove(double dx, double dy)
        => NativeMethods.MapOnPanMove(Handle, dx, dy);
    public void OnPanEnd()
        => NativeMethods.MapOnPanEnd(Handle);
    public void OnPinch(double scaleFactor, double cx, double cy)
        => NativeMethods.MapOnPinch(Handle, scaleFactor, cx, cy);

    public void TriggerRepaint() => NativeMethods.MapTriggerRepaint(Handle);
    public void CancelTransitions() => NativeMethods.MapCancelTransitions(Handle);
    public bool IsFullyLoaded => NativeMethods.MapIsFullyLoaded(Handle) != 0;

    // ── Debug overlays ─────────────────────────────────────────────────────────────

    /// <summary>Get the current debug overlay bitmask (<see cref="MbglDebugOptions"/>).</summary>
    public int GetDebugOptions() => NativeMethods.MapGetDebugOptions(Handle);

    /// <summary>Set the debug overlay bitmask. Pass <see cref="MbglDebugOptions.None"/> to disable all.</summary>
    public void SetDebugOptions(int options) => NativeMethods.MapSetDebugOptions(Handle, options);

    // ── Tier 1 – gesture / interactive movement ───────────────────────────────
    public void SetGestureInProgress(bool inProgress)
        => NativeMethods.MapSetGestureInProgress(Handle, inProgress ? 1 : 0);

    public void MoveBy(double dx, double dy, long durationMs = 0)
        => NativeMethods.MapMoveBy(Handle, dx, dy, durationMs);

    public void RotateBy(double x0, double y0, double x1, double y1)
        => NativeMethods.MapRotateBy(Handle, x0, y0, x1, y1);

    public void PitchBy(double deltaDegrees, long durationMs = 0)
        => NativeMethods.MapPitchBy(Handle, deltaDegrees, durationMs);

    // ── Tier 1 – map option setters ───────────────────────────────────────────
    /// <param name="orientation">0=Upwards 1=Rightwards 2=Downwards 3=Leftwards</param>
    public void SetNorthOrientation(int orientation)
        => NativeMethods.MapSetNorthOrientation(Handle, orientation);

    /// <param name="mode">0=None 1=HeightOnly 2=WidthAndHeight 3=Screen</param>
    public void SetConstrainMode(int mode)
        => NativeMethods.MapSetConstrainMode(Handle, mode);

    /// <param name="mode">0=Default 1=FlippedY</param>
    public void SetViewportMode(int mode)
        => NativeMethods.MapSetViewportMode(Handle, mode);

    // ── Tier 1 – bounds read-back ─────────────────────────────────────────────
    public BoundOptions GetBounds()
    {
        NativeMethods.MapGetBounds(Handle,
            out double latSw, out double lonSw,
            out double latNe, out double lonNe,
            out double minZoom, out double maxZoom,
            out double minPitch, out double maxPitch);
        return new BoundOptions(latSw, lonSw, latNe, lonNe,
                                minZoom, maxZoom, minPitch, maxPitch);
    }

    // ── Tier 2 – prefetch zoom delta ──────────────────────────────────────────
    public void SetPrefetchZoomDelta(int delta)
        => NativeMethods.MapSetPrefetchZoomDelta(Handle, delta);

    public int GetPrefetchZoomDelta()
        => NativeMethods.MapGetPrefetchZoomDelta(Handle);

    // ── Tier 2 – tile LOD controls ────────────────────────────────────────────
    public void SetTileLodMinRadius(double radius)
        => NativeMethods.MapSetTileLodMinRadius(Handle, radius);

    public void SetTileLodScale(double scale)
        => NativeMethods.MapSetTileLodScale(Handle, scale);

    public void SetTileLodPitchThreshold(double thresholdRadians)
        => NativeMethods.MapSetTileLodPitchThreshold(Handle, thresholdRadians);

    public void SetTileLodZoomShift(double shift)
        => NativeMethods.MapSetTileLodZoomShift(Handle, shift);

    /// <param name="mode">0=Default 1=Distance</param>
    public void SetTileLodMode(int mode)
        => NativeMethods.MapSetTileLodMode(Handle, mode);

    // ── Tier 2 – camera for point set ────────────────────────────────────────
    public unsafe CameraResult CameraForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points,
        double padTop = 0, double padLeft = 0,
        double padBottom = 0, double padRight = 0)
    {
        var flat = new double[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            flat[i * 2]     = points[i].Lat;
            flat[i * 2 + 1] = points[i].Lon;
        }
        fixed (double* ptr = flat)
        {
            NativeMethods.MapCameraForLatLngs(Handle, ptr, points.Count,
                padTop, padLeft, padBottom, padRight,
                out double lat, out double lon,
                out double zoom, out double bearing, out double pitch);
            return new CameraResult(lat, lon, zoom, bearing, pitch);
        }
    }

    // ── Tier 2 – batch projection ─────────────────────────────────────────────
    public unsafe (double X, double Y)[] PixelsForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points)
    {
        var flat = new double[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            flat[i * 2]     = points[i].Lat;
            flat[i * 2 + 1] = points[i].Lon;
        }
        var outXy = new double[points.Count * 2];
        fixed (double* inPtr = flat, outPtr = outXy)
            NativeMethods.MapPixelsForLatLngs(Handle, inPtr, points.Count, outPtr);
        var result = new (double X, double Y)[points.Count];
        for (int i = 0; i < points.Count; i++)
            result[i] = (outXy[i * 2], outXy[i * 2 + 1]);
        return result;
    }

    public unsafe (double Lat, double Lon)[] LatLngsForPixels(
        IReadOnlyList<(double X, double Y)> pixels)
    {
        var flat = new double[pixels.Count * 2];
        for (int i = 0; i < pixels.Count; i++)
        {
            flat[i * 2]     = pixels[i].X;
            flat[i * 2 + 1] = pixels[i].Y;
        }
        var outLl = new double[pixels.Count * 2];
        fixed (double* inPtr = flat, outPtr = outLl)
            NativeMethods.MapLatLngsForPixels(Handle, inPtr, pixels.Count, outPtr);
        var result = new (double Lat, double Lon)[pixels.Count];
        for (int i = 0; i < pixels.Count; i++)
            result[i] = (outLl[i * 2], outLl[i * 2 + 1]);
        return result;
    }

    public MbglStyle GetStyle()
    {
        var styleHandle = NativeMethods.MapGetStyle(Handle);
        if (styleHandle == IntPtr.Zero)
            throw new InvalidOperationException("Style is not yet loaded.");
        return new MbglStyle(styleHandle);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.MapDestroy(Handle);
            Handle = IntPtr.Zero;
        }
        if (_observerHandle.IsAllocated)
            _observerHandle.Free();
    }
}

/// <summary>Camera bounds returned by <see cref="MbglMap.GetBounds"/>.</summary>
/// <param name="LatSw">South latitude of the bounding box, or NaN if unset.</param>
/// <param name="LonSw">West longitude of the bounding box, or NaN if unset.</param>
/// <param name="LatNe">North latitude of the bounding box, or NaN if unset.</param>
/// <param name="LonNe">East longitude of the bounding box, or NaN if unset.</param>
/// <param name="MinZoom">Minimum zoom, or NaN if unset.</param>
/// <param name="MaxZoom">Maximum zoom, or NaN if unset.</param>
/// <param name="MinPitch">Minimum pitch (degrees), or NaN if unset.</param>
/// <param name="MaxPitch">Maximum pitch (degrees), or NaN if unset.</param>
public readonly record struct BoundOptions(
    double LatSw, double LonSw,
    double LatNe, double LonNe,
    double MinZoom, double MaxZoom,
    double MinPitch, double MaxPitch);

/// <summary>Camera result from a fit-to-points operation.</summary>
/// <param name="Lat">Center latitude, or NaN if no result.</param>
/// <param name="Lon">Center longitude, or NaN if no result.</param>
/// <param name="Zoom">Zoom level, or NaN if no result.</param>
/// <param name="Bearing">Bearing in degrees, or NaN if no result.</param>
/// <param name="Pitch">Pitch in degrees, or NaN if no result.</param>
public readonly record struct CameraResult(
    double Lat, double Lon,
    double Zoom, double Bearing, double Pitch);
