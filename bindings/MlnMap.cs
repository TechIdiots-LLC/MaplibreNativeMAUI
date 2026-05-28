/**
 * MlnMap.cs — Typed wrapper for mln_map* (maplibre-native-ffi).
 *
 * Sketch for the ffi-upstream branch. Replaces MbglMap.cs + MbglFrontend.cs.
 *
 * Design notes:
 *  - mln_map is created from an mln_runtime (no separate frontend step).
 *  - The render session (Metal/Vulkan surface) is attached after map creation
 *    via AttachMetalSurface() / AttachVulkanSurface().
 *  - Camera commands use mln_camera_options (field-mask struct) instead of
 *    flat lat/lon/zoom arguments.  The public C# API still exposes friendly
 *    overloads that build the struct internally.
 *  - Style operations live directly on the map (no separate MbglStyle handle).
 */
using System.Runtime.InteropServices;

namespace Maui.MapLibre.Native.Upstream;

/// <summary>
/// Wraps <c>mln_map*</c>. Must be created and disposed on the runtime owner thread.
/// </summary>
public sealed class MlnMap : IDisposable
{
    internal IntPtr Handle { get; private set; }
    private IntPtr _sessionHandle;
    private bool _disposed;

    public MlnMap(MlnRuntime runtime, MlnMapMode mode = MlnMapMode.Continuous)
    {
        unsafe
        {
            var opts = MlnMethods.MapOptionsDefault();
            opts.MapMode = (uint)mode;
            MlnMethods.ThrowIfFailed(
                MlnMethods.MapCreate(runtime.Handle, &opts, out var handle),
                nameof(MlnMap));
            Handle = handle;
        }
    }

    // ── Style ─────────────────────────────────────────────────────────────────

    public void SetStyleUrl(string url)
        => MlnMethods.ThrowIfFailed(MlnMethods.MapSetStyleUrl(Handle, url));

    public void SetStyleJson(string json)
        => MlnMethods.ThrowIfFailed(MlnMethods.MapSetStyleJson(Handle, json));

    // ── Render session ────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches a Metal render session. Call on iOS / macCatalyst after the
    /// CAMetalLayer is available.
    /// </summary>
    /// <param name="layer">CAMetalLayer* (retained by the session).</param>
    /// <param name="width">Logical width in UI pixels.</param>
    /// <param name="height">Logical height in UI pixels.</param>
    /// <param name="scaleFactor">UI-to-device pixel ratio.</param>
    /// <param name="device">Optional MTLDevice* (may be IntPtr.Zero).</param>
    public void AttachMetalSurface(
        IntPtr layer, uint width, uint height, double scaleFactor,
        IntPtr device = default)
    {
        unsafe
        {
            var desc = MlnMethods.MetalSurfaceDescriptorDefault();
            desc.Layer              = layer;
            desc.Extent.Width       = width;
            desc.Extent.Height      = height;
            desc.Extent.ScaleFactor = scaleFactor;
            desc.Context.Device     = device;
            MlnMethods.ThrowIfFailed(
                MlnMethods.MetalSurfaceAttach(Handle, &desc, out _sessionHandle),
                nameof(AttachMetalSurface));
        }
    }

    /// <summary>
    /// Attaches a Vulkan render session. Call on Android / Windows once the
    /// Vulkan instance, device, and surface are ready.
    /// </summary>
    public void AttachVulkanSurface(
        IntPtr instance, IntPtr physicalDevice, IntPtr device,
        IntPtr graphicsQueue, uint graphicsQueueFamilyIndex,
        IntPtr vkSurface,
        uint width, uint height, double scaleFactor)
    {
        unsafe
        {
            var desc = MlnMethods.VulkanSurfaceDescriptorDefault();
            desc.Context.Instance                 = instance;
            desc.Context.PhysicalDevice           = physicalDevice;
            desc.Context.Device                   = device;
            desc.Context.GraphicsQueue            = graphicsQueue;
            desc.Context.GraphicsQueueFamilyIndex = graphicsQueueFamilyIndex;
            desc.Surface                          = vkSurface;
            desc.Extent.Width                     = width;
            desc.Extent.Height                    = height;
            desc.Extent.ScaleFactor               = scaleFactor;
            MlnMethods.ThrowIfFailed(
                MlnMethods.VulkanSurfaceAttach(Handle, &desc, out _sessionHandle),
                nameof(AttachVulkanSurface));
        }
    }

    /// <summary>
    /// Attaches an EGL render session (Android / Linux). Requires an OpenGL build.
    /// </summary>
    public void AttachEglSurface(
        IntPtr display, IntPtr context, IntPtr surface,
        uint width, uint height, double scaleFactor)
    {
        unsafe
        {
            var desc = MlnMethods.EglSurfaceDescriptorDefault();
            desc.Display     = display;
            desc.Context     = context;
            desc.Surface     = surface;
            desc.Width       = width;
            desc.Height      = height;
            desc.ScaleFactor = scaleFactor;
            MlnMethods.ThrowIfFailed(
                MlnMethods.EglSurfaceAttach(Handle, &desc, out _sessionHandle),
                nameof(AttachEglSurface));
        }
    }

    /// <summary>
    /// Attaches a WGL render session (Windows OpenGL). Requires an OpenGL build.
    /// HDC and HGLRC are borrowed and must remain valid until the session is destroyed.
    /// </summary>
    public void AttachWglSurface(
        IntPtr hdc, IntPtr hglrc,
        uint width, uint height, double scaleFactor)
    {
        unsafe
        {
            var desc = MlnMethods.WglSurfaceDescriptorDefault();
            desc.Hdc         = hdc;
            desc.Hglrc       = hglrc;
            desc.Width       = width;
            desc.Height      = height;
            desc.ScaleFactor = scaleFactor;
            MlnMethods.ThrowIfFailed(
                MlnMethods.WglSurfaceAttach(Handle, &desc, out _sessionHandle),
                nameof(AttachWglSurface));
        }
    }

    /// <summary>
    /// Resize the attached render session. Call when the view dimensions change.
    /// </summary>
    public void Resize(uint width, uint height, double scaleFactor)
    {
        if (_sessionHandle == IntPtr.Zero) return;
        MlnMethods.ThrowIfFailed(
            MlnMethods.RenderSessionResize(_sessionHandle, width, height, scaleFactor),
            nameof(Resize));
    }

    /// <summary>
    /// Process the latest render update. Call when the runtime raises
    /// <see cref="MlnRuntime.RenderUpdateAvailable"/>.
    /// </summary>
    public void RenderUpdate()
    {
        if (_sessionHandle == IntPtr.Zero) return;
        MlnMethods.ThrowIfFailed(
            MlnMethods.RenderSessionRenderUpdate(_sessionHandle),
            nameof(RenderUpdate));
    }

    public void RequestRepaint()
        => MlnMethods.ThrowIfFailed(MlnMethods.MapRequestRepaint(Handle));

    // ── Camera ────────────────────────────────────────────────────────────────

    public void JumpTo(double lat, double lon, double zoom,
                       double bearing = 0, double pitch = 0)
    {
        unsafe
        {
            var cam = BuildCamera(lat, lon, zoom, bearing, pitch);
            MlnMethods.ThrowIfFailed(MlnMethods.MapJumpTo(Handle, &cam));
        }
    }

    public void EaseTo(double lat, double lon, double zoom,
                       double bearing, double pitch, double durationMs)
    {
        unsafe
        {
            var cam  = BuildCamera(lat, lon, zoom, bearing, pitch);
            var anim = BuildAnimation(durationMs);
            MlnMethods.ThrowIfFailed(MlnMethods.MapEaseTo(Handle, &cam, &anim));
        }
    }

    public void FlyTo(double lat, double lon, double zoom,
                      double bearing, double pitch, double durationMs)
    {
        unsafe
        {
            var cam  = BuildCamera(lat, lon, zoom, bearing, pitch);
            var anim = BuildAnimation(durationMs);
            MlnMethods.ThrowIfFailed(MlnMethods.MapFlyTo(Handle, &cam, &anim));
        }
    }

    public void CancelTransitions()
        => MlnMethods.ThrowIfFailed(MlnMethods.MapCancelTransitions(Handle));

    /// <summary>Returns the camera that fits the given lat/lng bounds.</summary>
    public (double Lat, double Lon, double Zoom, double Bearing, double Pitch)
        CameraForBounds(
            double latSw, double lonSw, double latNe, double lonNe,
            double padTop = 0, double padLeft = 0,
            double padBottom = 0, double padRight = 0)
    {
        unsafe
        {
            var bounds = new MlnLatLngBounds
            {
                Sw = new MlnLatLng { Latitude = latSw, Longitude = lonSw },
                Ne = new MlnLatLng { Latitude = latNe, Longitude = lonNe },
            };
            var fit = MlnMethods.CameraFitOptionsDefault();
            fit.Fields  = 1u; // padding bit
            fit.Padding = new MlnEdgeInsets { Top = padTop, Left = padLeft, Bottom = padBottom, Right = padRight };

            var outCam = MlnMethods.CameraOptionsDefault();
            MlnMethods.ThrowIfFailed(
                MlnMethods.MapCameraForLatLngBounds(Handle, &bounds, &fit, &outCam),
                nameof(CameraForBounds));
            return (outCam.Latitude, outCam.Longitude, outCam.Zoom,
                    outCam.Bearing, outCam.Pitch);
        }
    }

    public void SetBounds(
        double latSw = double.NaN, double lonSw = double.NaN,
        double latNe = double.NaN, double lonNe = double.NaN,
        double minZoom = double.NaN, double maxZoom = double.NaN,
        double minPitch = double.NaN, double maxPitch = double.NaN)
    {
        unsafe
        {
            var b = MlnMethods.BoundOptionsDefault();
            uint fields = 0;
            if (!double.IsNaN(latSw) && !double.IsNaN(lonSw) &&
                !double.IsNaN(latNe) && !double.IsNaN(lonNe))
            {
                b.Bounds = new MlnLatLngBounds
                {
                    Sw = new MlnLatLng { Latitude = latSw, Longitude = lonSw },
                    Ne = new MlnLatLng { Latitude = latNe, Longitude = lonNe },
                };
                fields |= 1u; // MLN_BOUND_OPTION_BOUNDS
            }
            if (!double.IsNaN(minZoom)) { b.MinZoom = minZoom; fields |= 1u << 1; }
            if (!double.IsNaN(maxZoom)) { b.MaxZoom = maxZoom; fields |= 1u << 2; }
            if (!double.IsNaN(minPitch)) { b.MinPitch = minPitch; fields |= 1u << 3; }
            if (!double.IsNaN(maxPitch)) { b.MaxPitch = maxPitch; fields |= 1u << 4; }
            b.Fields = fields;
            MlnMethods.ThrowIfFailed(MlnMethods.MapSetBoundOptions(Handle, &b));
        }
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    public void SetDebugOptions(MlnMapDebugOption options)
        => MlnMethods.ThrowIfFailed(MlnMethods.MapSetDebugOptions(Handle, (uint)options));

    // ── GeoJSON sources ───────────────────────────────────────────────────────

    public void AddGeoJsonSource(string sourceId, string geoJson)
        => MlnMethods.ThrowIfFailed(
               MlnMethods.MapAddGeoJsonSourceData(Handle, sourceId, geoJson));

    public void UpdateGeoJsonSource(string sourceId, string geoJson)
        => MlnMethods.ThrowIfFailed(
               MlnMethods.MapSetGeoJsonSourceData(Handle, sourceId, geoJson));

    public void RemoveSource(string sourceId)
        => MlnMethods.ThrowIfFailed(MlnMethods.MapRemoveStyleSource(Handle, sourceId));

    // ── Layers ────────────────────────────────────────────────────────────────

    public void AddLayer(string layerJson, string? beforeLayerId = null)
        => MlnMethods.ThrowIfFailed(
               MlnMethods.MapAddStyleLayerJson(Handle, layerJson, beforeLayerId));

    public void RemoveLayer(string layerId)
        => MlnMethods.ThrowIfFailed(MlnMethods.MapRemoveStyleLayer(Handle, layerId));

    public void SetLayerProperty(string layerId, string property, string valueJson)
        => MlnMethods.ThrowIfFailed(
               MlnMethods.MapSetStyleLayerProperty(Handle, layerId, property, valueJson));

    // ── Helpers ───────────────────────────────────────────────────────────────

    // All five camera fields set; field mask = bits 0..3 (center, zoom, bearing, pitch).
    private static MlnCameraOptions BuildCamera(
        double lat, double lon, double zoom, double bearing, double pitch)
    {
        var c = MlnMethods.CameraOptionsDefault();
        c.Fields    = 0b1111u; // center | zoom | bearing | pitch
        c.Latitude  = lat;
        c.Longitude = lon;
        c.Zoom      = zoom;
        c.Bearing   = bearing;
        c.Pitch     = pitch;
        return c;
    }

    private static MlnAnimationOptions BuildAnimation(double durationMs)
    {
        var a = MlnMethods.AnimationOptionsDefault();
        a.Fields     = 1u; // MLN_ANIMATION_OPTION_DURATION
        a.DurationMs = durationMs;
        return a;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sessionHandle != IntPtr.Zero)
        {
            MlnMethods.RenderSessionDetach(_sessionHandle);
            MlnMethods.RenderSessionDestroy(_sessionHandle);
            _sessionHandle = IntPtr.Zero;
        }
        if (Handle != IntPtr.Zero)
        {
            MlnMethods.MapDestroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
