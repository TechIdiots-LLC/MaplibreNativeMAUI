using MapLibreNative.Maui.Handlers.Geometry;
using MapLibreNative.Maui;
using Map = MapLibreNative.Maui.Handlers.Maps.Map;
using Style = MapLibreNative.Maui.Handlers.Maps.Style;

namespace MapLibreNative.Maui.Handlers;

public interface IMapLibreMapController : IMapLibreMapOptionsSink
{
    // Events
    public event Action<Map>? OnMapReadyReceived;
    public event Action? OnDidBecomeIdleReceived;
    public event Action<int>? OnCameraMoveStartedReceived;
    public event Action? OnCameraMoveReceived;
    public event Action? OnCameraIdleReceived;
    public event Action<int>? OnCameraTrackingChangedReceived;
    public event Action? OnCameraTrackingDismissedReceived;
    public event Func<LatLng, double, double, bool>? OnMapClickReceived;
    public event Func<LatLng, double, double, bool>? OnMapLongClickReceived;
    public event Action<Style>? OnStyleLoadedReceived;
    public event Action<Location>? OnUserLocationUpdateReceived;
    /// <summary>Fired when the map fails to load its style. The string is the error message.</summary>
    public event Action<string>? OnDidFailLoadingMapReceived;
    /// <summary>Fired when a style image is missing. The string is the image ID.</summary>
    public event Action<string>? OnStyleImageMissingReceived;
    
    // GPS control
    /// <summary>
    /// Feed a GPS location fix to the GPS control overlay.  The current GPS
    /// tracking mode (Off / Show / Follow) determines whether the location
    /// indicator is shown and whether the camera follows the position.
    /// Safe to call before the style is loaded; the position is cached and
    /// applied once the style is ready.
    /// </summary>
    void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10);

    // Sources
    public void AddGeoJsonSource(string sourceName, string source);

    public void AddRasterSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates, int tileSize,
        int minZoom, int maxZoom);

    public void AddRasterDemSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates, int tileSize,
        int minZoom, int maxZoom);

    public void AddVectorSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates, int minZoom,
        int maxZoom);
    public void AddImageSource(string sourceName, string url, LatLngQuad? coordinates);
    public void SetGeoJsonSource(string sourceName, string source);
    public void SetGeoJsonFeature(string sourceName, string geojsonFeature);
    public void RemoveSource(string sourceId);
    
    // Layers
    public void AddSymbolLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false);

    public void AddLineLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false);

    public void AddFillLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false);

    public void AddFillExtrusionLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false);

    public void AddCircleLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false);

    public void AddRasterLayer(
        string layerName,
        string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        string? belowLayerId = null);

    public void AddHillshadeLayer(
        string layerName,
        string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        string? belowLayerId = null);

    public void AddHeatmapLayer(
        string layerName,
        string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        string? belowLayerId = null);
    
    public void RemoveLayer(string layerId);

    // Camera – movement
    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0);

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 300);

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 500);

    // Camera – MapSpan convenience overloads.
    // Default interface methods so every platform controller gets them for free by
    // delegating to the primitive lat/lon/zoom overloads above. The span's extent is
    // converted to a MapLibre zoom level via MapSpan.ToZoomLevel().
    public void JumpTo(MapLibreNative.Maui.Geometry.MapSpan span, double bearing = 0, double pitch = 0)
        => JumpTo(span.Center.Latitude, span.Center.Longitude, span.ToZoomLevel(), bearing, pitch);

    public void EaseTo(MapLibreNative.Maui.Geometry.MapSpan span, double bearing = 0, double pitch = 0, long durationMs = 300)
        => EaseTo(span.Center.Latitude, span.Center.Longitude, span.ToZoomLevel(), bearing, pitch, durationMs);

    public void FlyTo(MapLibreNative.Maui.Geometry.MapSpan span, double bearing = 0, double pitch = 0, long durationMs = 500)
        => FlyTo(span.Center.Latitude, span.Center.Longitude, span.ToZoomLevel(), bearing, pitch, durationMs);

    // Camera – constraints
    public void SetCameraTargetBounds(LatLngBounds bounds,
        double minZoom = double.NaN, double maxZoom = double.NaN,
        double minPitch = double.NaN, double maxPitch = double.NaN);

    // Camera – read state
    public double GetZoom();
    public double GetBearing();
    public double GetPitch();
    public LatLng GetCenter();

    // Projection
    public (double X, double Y) LatLngToScreenPoint(double latitude, double longitude);
    public LatLng ScreenPointToLatLng(double x, double y);

    // Feature queries
    public string? QueryRenderedFeaturesAtPoint(double x, double y, string? layerIds = null);
    public string? QueryRenderedFeaturesInBox(double x1, double y1, double x2, double y2,
        string? layerIds = null);

    // Map state
    void CancelTransitions();

    // ── Tier 1 – gesture / interactive movement ───────────────────────────────
    void SetGestureInProgress(bool inProgress);
    void MoveBy(double dx, double dy, long durationMs = 0);
    void RotateBy(double x0, double y0, double x1, double y1);
    void PitchBy(double deltaDegrees, long durationMs = 0);

    // ── Tier 1 – map option setters ───────────────────────────────────────────
    /// <param name="orientation">0=Upwards 1=Rightwards 2=Downwards 3=Leftwards</param>
    void SetNorthOrientation(int orientation);
    /// <param name="mode">0=None 1=HeightOnly 2=WidthAndHeight 3=Screen</param>
    void SetConstrainMode(int mode);
    /// <param name="mode">0=Default 1=FlippedY</param>
    void SetViewportMode(int mode);

    // ── Tier 1 – bounds read-back ─────────────────────────────────────────────
    BoundOptions GetBounds();

    /// <summary>
    /// Returns the map's currently visible region as a lat/lng bounding box
    /// (south-west and north-east corners). Corners are <see cref="double.NaN"/>
    /// when the map is not yet ready.
    /// </summary>
    (double LatSW, double LonSW, double LatNE, double LonNE) GetVisibleBounds();

    // ── Tier 2 – tile LOD / prefetch ─────────────────────────────────────────
    void SetPrefetchZoomDelta(int delta);
    int  GetPrefetchZoomDelta();
    void SetTileLodMinRadius(double radius);
    void SetTileLodScale(double scale);
    void SetTileLodPitchThreshold(double thresholdRadians);
    void SetTileLodZoomShift(double shift);
    /// <param name="mode">0=Default 1=Distance</param>
    void SetTileLodMode(int mode);

    // ── Tier 2 – camera / projection ─────────────────────────────────────────
    CameraResult CameraForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points,
        double padTop = 0, double padLeft = 0,
        double padBottom = 0, double padRight = 0);

    (double X, double Y)[] PixelsForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points);

    (double Lat, double Lon)[] LatLngsForPixels(
        IReadOnlyList<(double X, double Y)> pixels);

    // ── Debug overlays ────────────────────────────────────────────────────────
    /// <summary>Get current debug overlay bitmask (see <c>MbglDebugOptions</c>).</summary>
    int  GetDebugOptions();
    /// <summary>Set debug overlay bitmask. Use 0 to disable all overlays.</summary>
    void SetDebugOptions(int options);

    // ── Style inspection ──────────────────────────────────────────────────────
    /// <summary>URL from which the current style was loaded, or empty string.</summary>
    string   GetStyleUrl();
    /// <summary>All source IDs in the current style, or empty array if no style is loaded.</summary>
    string[] GetStyleSourceIds();
    /// <summary>All layer IDs in draw order, or empty array if no style is loaded.</summary>
    string[] GetStyleLayerIds();

    // ── Layer read-back + visibility ──────────────────────────────────────────
    /// <summary>Returns the JSON-encoded value of a paint property, or null if not set.</summary>
    string? GetLayerPaintProperty(string layerId, string name);
    /// <summary>Returns the JSON-encoded value of a layout property, or null if not set.</summary>
    string? GetLayerLayoutProperty(string layerId, string name);
    /// <summary>Returns true if the layer is currently visible.</summary>
    bool GetLayerVisibility(string layerId);
    /// <summary>Show or hide an existing layer.</summary>
    void SetLayerVisibility(string layerId, bool visible);

    // ── Location indicator ("blue dot") ──────────────────────────────────────
    /// <summary>When true, each GPS fix also re-centres the map.</summary>
    bool FollowLocation { get; set; }
    /// <summary>When false the bearing arrow is suppressed — indicator always points north.</summary>
    bool ShowBearing { get; set; }
    /// <summary>
    /// Show (or update) the user-location indicator at the given position.
    /// Safe to call before the style is fully loaded; the position is queued and applied on StyleLoaded.
    /// </summary>
    void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10);
    /// <summary>Remove the location indicator layer and reset state.</summary>
    void ClearLocationIndicator();

    // ── Sprite images (for SymbolLayer icon-image) ────────────────────────────
    /// <summary>Register a named sprite image for use with <c>icon-image</c> in SymbolLayers.
    /// <paramref name="rgba"/> must be <c>width × height × 4</c> bytes of premultiplied RGBA.</summary>
    void AddSpriteImage(string imageId, int width, int height, byte[] rgba, float pixelRatio = 1f, bool sdf = false);
    /// <summary>Remove a sprite image previously registered with <see cref="AddSpriteImage"/>.</summary>
    void RemoveSpriteImage(string imageId);
}