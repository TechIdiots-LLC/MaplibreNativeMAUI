/**
 * NativeMethods.cs — P/Invoke declarations for mln_cabi native library.
 *
 * All handles (RunLoop, Map, Frontend, Style, Source, Layer) are opaque IntPtr.
 * Thread-safety: Map must be used on the same thread as its RunLoop.
 */
using System.Runtime.InteropServices;

namespace MapLibreNative.Maui;

/// <summary>Return status from every mutating C ABI function. Non-zero means failure;
/// call <see cref="NativeMethods.GetLastError"/> for a diagnostic message.</summary>
public enum MbglStatus : int
{
    Ok           =  0,
    InvalidArg   = -1,
    InvalidState = -2,
    WrongThread  = -3,
    Unsupported  = -4,
    NativeError  = -5,
}

/// <summary>Log severity levels emitted by MapLibre Native.</summary>
public enum MbglLogLevel : int
{
    Debug   = 0,
    Info    = 1,
    Warning = 2,
    Error   = 3,
}

/// <summary>Bitmask of debug visualisation overlays. OR together the flags you want.</summary>
[Flags]
public enum MbglDebugOptions : int
{
    None        = 0,
    TileBorders = 1 << 1,
    ParseStatus = 1 << 2,
    Timestamps  = 1 << 3,
    Collision   = 1 << 4,
    Overdraw    = 1 << 5,
    StencilClip = 1 << 6,
    DepthBuffer = 1 << 7,
}

/// <summary>Raw P/Invoke bindings — prefer the typed wrappers in MbglMap etc.</summary>
public static partial class NativeMethods
{
#if IOS || MACCATALYST
    private const string Lib = "__Internal";
#elif ANDROID
    private const string Lib = "mln-cabi";
#else
    private const string Lib = "mln-cabi";

    // On Windows the NuGet package places mln-cabi.dll in native\win-x64\ (or
    // win-arm64\) relative to the app directory — the standard RID-specific layout.
    // P/Invoke does not probe subdirectories by default, so we register a resolver
    // that tries that path before falling back to the OS search order.
    static NativeMethods()
    {
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(NativeMethods).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                if (libraryName != "mln-cabi")
                    return IntPtr.Zero;

                string rid = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64   => "win-x64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
                    _ => string.Empty
                };

                if (rid.Length > 0)
                {
                    string probe = System.IO.Path.Combine(
                        AppContext.BaseDirectory, "native", rid, "mln-cabi.dll");
                    if (System.IO.File.Exists(probe) &&
                        System.Runtime.InteropServices.NativeLibrary.TryLoad(probe, out IntPtr h))
                        return h;
                }

                // Fall through to default OS search (app dir, PATH, etc.)
                return IntPtr.Zero;
            });
    }
#endif

    // ── Callbacks ─────────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MapObserverFn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string  eventName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? detail,
        IntPtr userdata);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RenderFn(IntPtr userdata);

    /// <summary>Log intercept callback. Return non-zero to consume the record (suppress default output).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int LogFn(
        MbglLogLevel level,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string category,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
        IntPtr userdata);

    // ── Diagnostics ───────────────────────────────────────────────────────────
    /// <summary>Returns a thread-local string describing the most recent non-OK status.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_get_last_error")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    public static partial string GetLastError();

    /// <summary>Install a process-global log callback. Pass null to restore default logging.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_install_log_callback")]
    public static partial MbglStatus InstallLogCallback(LogFn? fn, IntPtr userdata);

    // ── Network status ────────────────────────────────────────────────────────
    /// <summary>Toggle the process-global network state. Pass 0 to force offline
    /// mode (serve only cached resources), 1 to restore online mode.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_network_status_set")]
    public static partial MbglStatus NetworkStatusSet(int online);

    /// <summary>Returns 1 if the network is in online mode, 0 if offline.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_network_status_get")]
    public static partial int NetworkStatusGet();

    // ── RunLoop ───────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_runloop_create")]
    public static partial IntPtr RunLoopCreate();

    [LibraryImport(Lib, EntryPoint = "mbgl_runloop_destroy")]
    public static partial MbglStatus RunLoopDestroy(IntPtr rl);

    [LibraryImport(Lib, EntryPoint = "mbgl_runloop_run_once")]
    public static partial MbglStatus RunLoopRunOnce(IntPtr rl);

    // ── Frontend ──────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_frontend_create_gl")]
    public static partial IntPtr FrontendCreateGl(
        IntPtr surfaceHandle,
        IntPtr glContext,
        int    widthPx,
        int    heightPx,
        float  pixelRatio,
        RenderFn renderCallback,
        IntPtr   renderUserdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_frontend_destroy")]
    public static partial MbglStatus FrontendDestroy(IntPtr fe);

    [LibraryImport(Lib, EntryPoint = "mbgl_frontend_render")]
    public static partial MbglStatus FrontendRender(IntPtr fe);

    [LibraryImport(Lib, EntryPoint = "mbgl_frontend_set_size")]
    public static partial MbglStatus FrontendSetSize(IntPtr fe, int widthPx, int heightPx);

    [LibraryImport(Lib, EntryPoint = "mbgl_frontend_get_native_view")]
    public static partial IntPtr FrontendGetNativeView(IntPtr fe);

    // ── Map ───────────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_create",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapCreate(
        IntPtr fe,
        IntPtr rl,
        string? cachePath,
        string? assetPath,
        float   pixelRatio,
        MapObserverFn? observer,
        IntPtr  observerUserdata);

    /// <summary>Extended map factory: adds an API key and a maximum disk-cache size
    /// (bytes; 0 = MapLibre default) on top of <see cref="MapCreate"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_map_create2",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapCreate2(
        IntPtr fe,
        IntPtr rl,
        string? cachePath,
        string? assetPath,
        string? apiKey,
        ulong   maxCacheSizeBytes,
        float   pixelRatio,
        MapObserverFn? observer,
        IntPtr  observerUserdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_destroy")]
    public static partial MbglStatus MapDestroy(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_style_url",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus MapSetStyleUrl(IntPtr map, string url);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_style_json",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus MapSetStyleJson(IntPtr map, string json);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_size")]
    public static partial MbglStatus MapSetSize(IntPtr map, int widthPx, int heightPx);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_jump_to")]
    public static partial MbglStatus MapJumpTo(IntPtr map, double lat, double lon, double zoom, double bearing, double pitch);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_ease_to")]
    public static partial MbglStatus MapEaseTo(IntPtr map, double lat, double lon, double zoom, double bearing, double pitch, long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_zoom")]
    public static partial double MapGetZoom(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_bearing")]
    public static partial double MapGetBearing(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_pitch")]
    public static partial double MapGetPitch(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_center")]
    public static partial void MapGetCenter(IntPtr map, out double lat, out double lon);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_min_zoom")]
    public static partial MbglStatus MapSetMinZoom(IntPtr map, double zoom);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_max_zoom")]
    public static partial MbglStatus MapSetMaxZoom(IntPtr map, double zoom);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_scroll")]
    public static partial MbglStatus MapOnScroll(IntPtr map, double delta, double cx, double cy);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_double_tap")]
    public static partial MbglStatus MapOnDoubleTap(IntPtr map, double x, double y);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_pan_start")]
    public static partial MbglStatus MapOnPanStart(IntPtr map, double x, double y);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_pan_move")]
    public static partial MbglStatus MapOnPanMove(IntPtr map, double dx, double dy);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_pan_end")]
    public static partial MbglStatus MapOnPanEnd(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_on_pinch")]
    public static partial MbglStatus MapOnPinch(IntPtr map, double scaleFactor, double cx, double cy);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_trigger_repaint")]
    public static partial MbglStatus MapTriggerRepaint(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_cancel_transitions")]
    public static partial MbglStatus MapCancelTransitions(IntPtr map);

    // ── Map – debug overlays ──────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_debug_options")]
    public static partial int MapGetDebugOptions(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_debug_options")]
    public static partial MbglStatus MapSetDebugOptions(IntPtr map, int options);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_is_fully_loaded")]
    public static partial int MapIsFullyLoaded(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_fly_to")]
    public static partial MbglStatus MapFlyTo(IntPtr map, double lat, double lon,
        double zoom, double bearing, double pitch, long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_bounds")]
    public static partial MbglStatus MapSetBounds(IntPtr map,
        double latSw, double lonSw, double latNe, double lonNe,
        double minZoom, double maxZoom, double minPitch, double maxPitch);

    // ── Map – camera with edge padding ────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_jump_to_padded")]
    public static partial MbglStatus MapJumpToPadded(IntPtr map,
        double lat, double lon, double zoom, double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_ease_to_padded")]
    public static partial MbglStatus MapEaseToPadded(IntPtr map,
        double lat, double lon, double zoom, double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_fly_to_padded")]
    public static partial MbglStatus MapFlyToPadded(IntPtr map,
        double lat, double lon, double zoom, double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_camera")]
    public static partial MbglStatus MapGetCamera(IntPtr map,
        double padTop, double padLeft, double padBottom, double padRight,
        out double outLat, out double outLon,
        out double outZoom, out double outBearing, out double outPitch);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_scale_by")]
    public static partial MbglStatus MapScaleBy(IntPtr map,
        double scale, double anchorX, double anchorY, long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_camera_for_bounds")]
    public static partial MbglStatus MapCameraForBounds(IntPtr map,
        double latSw, double lonSw, double latNe, double lonNe,
        double padTop, double padLeft, double padBottom, double padRight,
        out double outLat, out double outLon,
        out double outZoom, out double outBearing, out double outPitch);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_pixel_for_latlng")]
    public static partial void MapPixelForLatLng(IntPtr map, double lat, double lon,
        out double outX, out double outY);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_latlng_for_pixel")]
    public static partial void MapLatLngForPixel(IntPtr map, double x, double y,
        out double outLat, out double outLon);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_projection_mode")]
    public static partial MbglStatus MapSetProjectionMode(IntPtr map,
        int axonometric, double xSkew, double ySkew);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_query_rendered_features_at_point",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapQueryRenderedFeaturesAtPoint(IntPtr map,
        double x, double y, string? layerIds);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_query_rendered_features_in_box",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapQueryRenderedFeaturesInBox(IntPtr map,
        double x1, double y1, double x2, double y2, string? layerIds);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_query_source_features",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapQuerySourceFeatures(IntPtr map,
        string sourceId, string? sourceLayerIds, string? filterJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_query_feature_extensions",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapQueryFeatureExtensions(IntPtr map,
        string sourceId, string featureJson, string extension, string extensionField,
        string? argsJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_free_string")]
    public static partial void FreeString(IntPtr str);

    // ── Style ─────────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_style")]
    public static partial IntPtr MapGetStyle(IntPtr map);

    // ── Sources ───────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_geojson_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddGeoJsonSource(IntPtr style, string sourceId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_geojson_source_url",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddGeoJsonSourceUrl(IntPtr style, string sourceId, string url);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_geojson_source_options",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddGeoJsonSourceOptions(IntPtr style, string sourceId, string? optionsJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_geojson_source_set_data",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus GeoJsonSourceSetData(IntPtr source, string geojson);

    [LibraryImport(Lib, EntryPoint = "mbgl_geojson_source_set_url",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus GeoJsonSourceSetUrl(IntPtr source, string url);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_vector_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddVectorSource(IntPtr style, string sourceId, string url);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_raster_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddRasterSource(IntPtr style, string sourceId, string url, int tileSize);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_rasterdem_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddRasterDemSource(IntPtr style, string sourceId, string url, int tileSize);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_image_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddImageSource(IntPtr style, string sourceId, string url,
        double lat0, double lon0, double lat1, double lon1,
        double lat2, double lon2, double lat3, double lon3);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_remove_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleRemoveSource(IntPtr style, string sourceId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_has_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int StyleHasSource(IntPtr style, string sourceId);

    // ── Layers ────────────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_fill_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddFillLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_line_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddLineLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_circle_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddCircleLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_symbol_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddSymbolLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_raster_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddRasterLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_heatmap_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddHeatmapLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_hillshade_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddHillshadeLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_fill_extrusion_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddFillExtrusionLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_background_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddBackgroundLayer(IntPtr style, string layerId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_location_indicator_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddLocationIndicatorLayer(IntPtr style, string layerId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_color_relief_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddColorReliefLayer(IntPtr style, string layerId, string sourceId, string? beforeLayerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_image",
        StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial MbglStatus StyleAddImage(IntPtr style, string imageId,
        int width, int height, float pixelRatio, int sdf, byte* rgbaPremultiplied);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_remove_image",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleRemoveImage(IntPtr style, string imageId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_json")]
    public static partial IntPtr StyleGetJson(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_set_transition")]
    public static partial MbglStatus StyleSetTransition(IntPtr style, long durationMs, long delayMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_set_light_property",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleSetLightProperty(IntPtr style, string name, string valueJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_remove_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleRemoveLayer(IntPtr style, string layerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_has_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int StyleHasLayer(IntPtr style, string layerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_source_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus LayerSetSourceLayer(IntPtr layer, string sourceLayer);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_filter",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus LayerSetFilter(IntPtr layer, string filterJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_min_zoom")]
    public static partial MbglStatus LayerSetMinZoom(IntPtr layer, float zoom);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_max_zoom")]
    public static partial MbglStatus LayerSetMaxZoom(IntPtr layer, float zoom);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_visibility")]
    public static partial MbglStatus LayerSetVisibility(IntPtr layer, int visible);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_paint_property",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus LayerSetPaintProperty(IntPtr layer, string name, string valueJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_set_layout_property",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus LayerSetLayoutProperty(IntPtr layer, string name, string valueJson);

    // ── Map – gesture / interactive movement (Tier 1) ─────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_gesture_in_progress")]
    public static partial MbglStatus MapSetGestureInProgress(IntPtr map, int inProgress);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_is_gesture_in_progress")]
    public static partial int MapIsGestureInProgress(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_is_rotating")]
    public static partial int MapIsRotating(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_is_scaling")]
    public static partial int MapIsScaling(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_is_panning")]
    public static partial int MapIsPanning(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_move_by")]
    public static partial MbglStatus MapMoveBy(IntPtr map, double dx, double dy, long durationMs);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_rotate_by")]
    public static partial MbglStatus MapRotateBy(IntPtr map, double x0, double y0, double x1, double y1);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_pitch_by")]
    public static partial MbglStatus MapPitchBy(IntPtr map, double deltaDegrees, long durationMs);

    // ── Map – option setters (Tier 1) ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_north_orientation")]
    public static partial MbglStatus MapSetNorthOrientation(IntPtr map, int orientation);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_constrain_mode")]
    public static partial MbglStatus MapSetConstrainMode(IntPtr map, int mode);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_viewport_mode")]
    public static partial MbglStatus MapSetViewportMode(IntPtr map, int mode);

    // ── Map – bounds read-back (Tier 1) ───────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_bounds")]
    public static partial void MapGetBounds(IntPtr map,
        out double latSw, out double lonSw,
        out double latNe, out double lonNe,
        out double minZoom, out double maxZoom,
        out double minPitch, out double maxPitch);

    // ── Map – tile LOD controls (Tier 2) ─────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_prefetch_zoom_delta")]
    public static partial MbglStatus MapSetPrefetchZoomDelta(IntPtr map, int delta);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_prefetch_zoom_delta")]
    public static partial int MapGetPrefetchZoomDelta(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_tile_lod_min_radius")]
    public static partial MbglStatus MapSetTileLodMinRadius(IntPtr map, double radius);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_tile_lod_scale")]
    public static partial MbglStatus MapSetTileLodScale(IntPtr map, double scale);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_tile_lod_pitch_threshold")]
    public static partial MbglStatus MapSetTileLodPitchThreshold(IntPtr map, double thresholdRad);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_tile_lod_zoom_shift")]
    public static partial MbglStatus MapSetTileLodZoomShift(IntPtr map, double shift);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_tile_lod_mode")]
    public static partial MbglStatus MapSetTileLodMode(IntPtr map, int mode);

    // ── Map – camera for point set (Tier 2) ───────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_camera_for_latlngs")]
    public static unsafe partial MbglStatus MapCameraForLatLngs(IntPtr map,
        double* latLngs, int count,
        double padTop, double padLeft, double padBottom, double padRight,
        out double outLat, out double outLon,
        out double outZoom, out double outBearing, out double outPitch);

    // ── Map – batch projection (Tier 2) ───────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_pixels_for_latlngs")]
    public static unsafe partial MbglStatus MapPixelsForLatLngs(IntPtr map,
        double* latLngs, int count, double* outXy);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_latlngs_for_pixels")]
    public static unsafe partial MbglStatus MapLatLngsForPixels(IntPtr map,
        double* xy, int count, double* outLatLngs);

    // ── Style – enumeration (Tier 1) ─────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_url")]
    public static partial IntPtr StyleGetUrl(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_name")]
    public static partial IntPtr StyleGetName(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_source_ids")]
    public static partial IntPtr StyleGetSourceIds(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_layer_ids")]
    public static partial IntPtr StyleGetLayerIds(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_layer",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleGetLayer(IntPtr style, string layerId);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_get_source",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleGetSource(IntPtr style, string sourceId);

    /// <summary>Returns the attribution text of a source (may be NULL). Caller frees with FreeString.</summary>
    [LibraryImport(Lib, EntryPoint = "mbgl_source_get_attribution")]
    public static partial IntPtr SourceGetAttribution(IntPtr source);

    // ── Layer – read-back (Tier 1) ────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_layer_get_paint_property",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr LayerGetPaintProperty(IntPtr layer, string name);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_get_layout_property",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr LayerGetLayoutProperty(IntPtr layer, string name);

    [LibraryImport(Lib, EntryPoint = "mbgl_layer_get_visibility")]
    public static partial int LayerGetVisibility(IntPtr layer);

    // ── Viewport bounds ────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_latlng_bounds_for_camera")]
    public static unsafe partial MbglStatus MapLatLngBoundsForCamera(IntPtr map,
        double* outLatSw, double* outLonSw, double* outLatNe, double* outLonNe);

    // ── Memory / debug ─────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_reduce_memory_use")]
    public static partial MbglStatus MapReduceMemoryUse(IntPtr map);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_dump_debug_logs")]
    public static partial MbglStatus MapDumpDebugLogs(IntPtr map);

    // ── Feature state ──────────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_map_set_feature_state",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus MapSetFeatureState(IntPtr map,
        string sourceId, string? sourceLayerId, string featureId, string stateJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_get_feature_state",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MapGetFeatureState(IntPtr map,
        string sourceId, string? sourceLayerId, string featureId);

    [LibraryImport(Lib, EntryPoint = "mbgl_map_remove_feature_state",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus MapRemoveFeatureState(IntPtr map,
        string sourceId, string? sourceLayerId, string? featureId, string? stateKey);

    // ── Style – generic JSON add ───────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_source_json",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleAddSourceJson(IntPtr style, string sourceId, string sourceJson);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_add_layer_json",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr StyleAddLayerJson(IntPtr style, string layerJson, string? beforeLayerId);

    // ── Style – 3D terrain ─────────────────────────────────────────────────────
    [LibraryImport(Lib, EntryPoint = "mbgl_style_set_terrain",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus StyleSetTerrain(IntPtr style, string sourceId, float exaggeration);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_remove_terrain")]
    public static partial MbglStatus StyleRemoveTerrain(IntPtr style);

    [LibraryImport(Lib, EntryPoint = "mbgl_style_is_terrain_enabled")]
    public static partial int StyleIsTerrainEnabled(IntPtr style);

#if ANDROID
    // ── Android ANativeWindow helpers ──────────────────────────────────────────
    [DllImport(Lib, EntryPoint = "mbgl_android_acquire_window")]
    public static extern IntPtr AndroidAcquireWindow(IntPtr jniEnv, IntPtr surface);

    [DllImport(Lib, EntryPoint = "mbgl_android_release_window")]
    public static extern void AndroidReleaseWindow(IntPtr window);

    // ── Android HTTP provider ──────────────────────────────────────────────────

    /// <summary>
    /// Callback signature for the HTTP provider.  Called by the native layer
    /// when it needs to fetch a URL.  Respond with <see cref="HttpRespond"/>.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HttpProviderDelegate(
        ulong       requestId,
        IntPtr      urlPtr,
        IntPtr      etagPtr,
        IntPtr      modifiedPtr,
        long        rangeStart,
        long        rangeEnd,
        IntPtr      userdata);

    /// <summary>Error codes for <see cref="HttpRespond"/>.</summary>
    public enum MbglHttpError : int
    {
        None       = 0,
        NotFound   = 2,
        Server     = 3,
        Connection = 4,
        RateLimit  = 5,
        Other      = 6,
    }

    /// <summary>
    /// Register the C# HTTP provider.  Must be called before the first map is
    /// created.  The delegate must be kept alive for the lifetime of the map.
    /// </summary>
    [DllImport(Lib, EntryPoint = "mbgl_set_http_provider")]
    public static extern void SetHttpProvider(
        HttpProviderDelegate? fn,
        IntPtr                userdata);

    /// <summary>
    /// Invoked when the native layer cancels a previously-started request
    /// (mbgl superseded/dropped the tile). The host must abort the matching
    /// in-flight fetch so its connection is freed. Keep the delegate alive for
    /// the map's lifetime.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HttpCancelDelegate(ulong requestId, IntPtr userdata);

    [DllImport(Lib, EntryPoint = "mbgl_set_http_cancel_provider")]
    public static extern void SetHttpCancelProvider(
        HttpCancelDelegate? fn,
        IntPtr              userdata);

    /// <summary>
    /// Deliver a completed HTTP response to the native layer.
    /// Safe to call from any thread.  All string parameters may be IntPtr.Zero.
    /// </summary>
    [DllImport(Lib, EntryPoint = "mbgl_http_respond")]
    public static extern void HttpRespond(
        ulong         requestId,
        MbglHttpError error,
        IntPtr        errorMessage,
        int           httpStatus,
        IntPtr        data,
        int           dataLen,
        IntPtr        etag,
        IntPtr        modified,
        IntPtr        expires,
        IntPtr        cacheControl,
        int           noContent,
        int           notModified,
        int           mustRevalidate);

    /// <summary>Cancel a pending HTTP request.</summary>
    [DllImport(Lib, EntryPoint = "mbgl_http_cancel")]
    public static extern void HttpCancel(ulong requestId);
#endif

    // ── Offline regions + ambient cache ───────────────────────────────────────
    // All offline callbacks are invoked on MapLibre's internal database thread.

    /// <summary>One-shot completion callback for offline operations with no payload.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OfflineDoneFn(
        MbglStatus status,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? errorMessage,
        IntPtr userdata);

    /// <summary>One-shot callback delivering a JSON array of offline regions.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OfflineRegionsFn(
        MbglStatus status,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? errorMessage,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? regionsJson,
        IntPtr userdata);

    /// <summary>One-shot callback delivering a region status JSON object.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OfflineStatusFn(
        MbglStatus status,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? errorMessage,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? statusJson,
        IntPtr userdata);

    /// <summary>Recurring download-progress callback for an observed region.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OfflineProgressFn(
        long regionId,
        int downloadState,
        ulong completedResources,
        ulong completedBytes,
        ulong completedTiles,
        ulong requiredResources,
        int requiredIsPrecise,
        int complete,
        IntPtr userdata);

    /// <summary>Recurring download-error callback for an observed region.
    /// <paramref name="reason"/> matches mbgl's Response::Error::Reason values
    /// (2=NotFound 3=Server 4=Connection 5=RateLimit 6=Other), or 100 when the
    /// Mapbox tile count limit was exceeded.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OfflineRegionErrorFn(
        long regionId,
        int reason,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? message,
        IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_manager_create",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr OfflineManagerCreate(
        string? cachePath, string? assetPath, string? apiKey, ulong maxCacheSizeBytes);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_manager_destroy")]
    public static partial MbglStatus OfflineManagerDestroy(IntPtr m);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_list_regions")]
    public static partial MbglStatus OfflineListRegions(IntPtr m, OfflineRegionsFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_create_region",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus OfflineCreateRegion(IntPtr m,
        string styleUrl,
        double latSw, double lonSw, double latNe, double lonNe,
        double minZoom, double maxZoom, float pixelRatio, int includeIdeographs,
        byte[]? metadata, int metadataLen,
        OfflineRegionsFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_create_region_geometry",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus OfflineCreateRegionGeometry(IntPtr m,
        string styleUrl, string geometryGeoJson,
        double minZoom, double maxZoom, float pixelRatio, int includeIdeographs,
        byte[]? metadata, int metadataLen,
        OfflineRegionsFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_delete_region")]
    public static partial MbglStatus OfflineDeleteRegion(IntPtr m, long regionId,
        OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_invalidate_region")]
    public static partial MbglStatus OfflineInvalidateRegion(IntPtr m, long regionId,
        OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_set_region_download_state")]
    public static partial MbglStatus OfflineSetRegionDownloadState(IntPtr m, long regionId, int active);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_set_region_observer")]
    public static partial MbglStatus OfflineSetRegionObserver(IntPtr m, long regionId,
        OfflineProgressFn? progress, OfflineRegionErrorFn? error, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_get_region_status")]
    public static partial MbglStatus OfflineGetRegionStatus(IntPtr m, long regionId,
        OfflineStatusFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_update_region_metadata")]
    public static partial MbglStatus OfflineUpdateRegionMetadata(IntPtr m, long regionId,
        byte[]? metadata, int metadataLen, OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_region_get_metadata")]
    public static partial IntPtr OfflineRegionGetMetadata(IntPtr m, long regionId, out int outLen);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_merge_database",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial MbglStatus OfflineMergeDatabase(IntPtr m, string sideDbPath,
        OfflineRegionsFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_set_tile_count_limit")]
    public static partial MbglStatus OfflineSetTileCountLimit(IntPtr m, ulong limit);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_set_maximum_ambient_cache_size")]
    public static partial MbglStatus OfflineSetMaximumAmbientCacheSize(IntPtr m, ulong bytes,
        OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_clear_ambient_cache")]
    public static partial MbglStatus OfflineClearAmbientCache(IntPtr m, OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_invalidate_ambient_cache")]
    public static partial MbglStatus OfflineInvalidateAmbientCache(IntPtr m, OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_pack_database")]
    public static partial MbglStatus OfflinePackDatabase(IntPtr m, OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_reset_database")]
    public static partial MbglStatus OfflineResetDatabase(IntPtr m, OfflineDoneFn cb, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = "mbgl_offline_set_pack_database_automatically")]
    public static partial MbglStatus OfflineSetPackDatabaseAutomatically(IntPtr m, int enabled);
}
