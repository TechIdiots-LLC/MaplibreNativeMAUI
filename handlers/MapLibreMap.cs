using System.Text.Json;
using System.Windows.Input;
using GeoJSON.Text.Feature;
using MapLibreNative.Maui.Handlers.EventArgs;
using MapLibreNative.Maui.Handlers.Geometry;
using MapLibreNative.Maui.Handlers.Properties;
using Map = MapLibreNative.Maui.Handlers.Maps.Map;
using Style = MapLibreNative.Maui.Handlers.Maps.Style;

namespace MapLibreNative.Maui.Handlers;

// All the code in this file is included in all platforms.
public partial class MapLibreMap : StackLayout
{
    public static readonly BindableProperty StyleUrlProperty = BindableProperty.Create(nameof(StyleUrl), typeof(string), typeof(MapLibreMap));
    public static readonly BindableProperty MinZoomProperty = BindableProperty.Create(nameof(MinZoom), typeof(float), typeof(MapLibreMap));
    public static readonly BindableProperty MaxZoomProperty = BindableProperty.Create(nameof(MaxZoom), typeof(float), typeof(MapLibreMap));
    public static readonly BindableProperty RotateGesturesEnabledProperty = BindableProperty.Create(nameof(RotateGesturesEnabled), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    public static readonly BindableProperty ScrollGesturesEnabledProperty = BindableProperty.Create(nameof(ScrollGesturesEnabled), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    public static readonly BindableProperty TiltGesturesEnabledProperty = BindableProperty.Create(nameof(TiltGesturesEnabled), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    public static readonly BindableProperty TrackCameraPositionProperty = BindableProperty.Create(nameof(TrackCameraPosition), typeof(bool), typeof(MapLibreMap));
    public static readonly BindableProperty ZoomGesturesEnabledProperty = BindableProperty.Create(nameof(ZoomGesturesEnabled), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    public static readonly BindableProperty MyLocationEnabledProperty = BindableProperty.Create(nameof(MyLocationEnabled), typeof(bool), typeof(MapLibreMap));
    public static readonly BindableProperty MyLocationTrackingModeProperty = BindableProperty.Create(nameof(MyLocationTrackingMode), typeof(int), typeof(MapLibreMap));
    public static readonly BindableProperty MyLocationRenderModeProperty = BindableProperty.Create(nameof(MyLocationRenderMode), typeof(int), typeof(MapLibreMap));
    public static readonly BindableProperty LogoViewMarginsProperty = BindableProperty.Create(nameof(LogoViewMargins), typeof(int?[]), typeof(MapLibreMap));
    public static readonly BindableProperty CompassGravityProperty = BindableProperty.Create(nameof(CompassGravity), typeof(int), typeof(MapLibreMap));
    public static readonly BindableProperty CompassViewMarginsProperty = BindableProperty.Create(nameof(CompassViewMargins), typeof(int?[]), typeof(MapLibreMap));
    public static readonly BindableProperty AttributionButtonGravityProperty = BindableProperty.Create(nameof(AttributionButtonGravity), typeof(int), typeof(MapLibreMap));
    public static readonly BindableProperty AttributionButtonMarginsProperty = BindableProperty.Create(nameof(AttributionButtonMargins), typeof(int?[]), typeof(MapLibreMap));
    /// <summary>Show zoom-in, zoom-out and compass/reset-north buttons. Default <c>true</c>.</summary>
    public static readonly BindableProperty ShowNavigationControlsProperty =
        BindableProperty.Create(nameof(ShowNavigationControls), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    /// <summary>
    /// Show the GPS tracking control overlay (3-state location button + bearing reset).
    /// Default <c>true</c>.
    /// </summary>
    public static readonly BindableProperty ShowGpsControlProperty =
        BindableProperty.Create(nameof(ShowGpsControl), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    /// <summary>
    /// Show an always-visible attribution overlay (OSM requires this).
    /// Attributions are collected from all loaded TileJSON sources plus
    /// <see cref="CustomAttribution"/>. Default <c>true</c>.
    /// </summary>
    public static readonly BindableProperty ShowAttributionControlProperty =
        BindableProperty.Create(nameof(ShowAttributionControl), typeof(bool), typeof(MapLibreMap), defaultValue: true);
    /// <summary>Extra attribution text appended after source-derived attributions.</summary>
    public static readonly BindableProperty CustomAttributionProperty =
        BindableProperty.Create(nameof(CustomAttribution), typeof(string), typeof(MapLibreMap));
    /// <summary>Corner the navigation control is anchored to. Default <see cref="MapControlCorner.TopRight"/>.</summary>
    public static readonly BindableProperty NavigationControlPositionProperty =
        BindableProperty.Create(nameof(NavigationControlPosition), typeof(MapControlCorner), typeof(MapLibreMap), defaultValue: MapControlCorner.TopRight);
    /// <summary>Corner the GPS control is anchored to. Default <see cref="MapControlCorner.TopRight"/>.</summary>
    public static readonly BindableProperty GpsControlPositionProperty =
        BindableProperty.Create(nameof(GpsControlPosition), typeof(MapControlCorner), typeof(MapLibreMap), defaultValue: MapControlCorner.TopRight);
    /// <summary>Corner the attribution control is anchored to. Default <see cref="MapControlCorner.BottomLeft"/>.</summary>
    public static readonly BindableProperty AttributionControlPositionProperty =
        BindableProperty.Create(nameof(AttributionControlPosition), typeof(MapControlCorner), typeof(MapLibreMap), defaultValue: MapControlCorner.BottomLeft);
    /// <summary>
    /// Extra multiplier applied to the platform pixel ratio (display density) when the
    /// native map is created. Default <c>1.0</c>. Everything sized in style pixels —
    /// text, icons, circles, line widths — scales by it, so apps can honour the OS
    /// font-scale / accessibility setting (which MapLibre otherwise ignores), e.g. by
    /// setting it to the system font scale on Android.
    /// Read once at platform-view creation; changing it on a live map has no effect.
    /// </summary>
    public static readonly BindableProperty UiScaleProperty =
        BindableProperty.Create(nameof(UiScale), typeof(double), typeof(MapLibreMap), defaultValue: 1.0);

    public static readonly BindableProperty MapReadyCommandProperty = BindableProperty.Create(nameof(MapReadyCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty StyleLoadedCommandProperty = BindableProperty.Create(nameof(StyleLoadedCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty DidBecomeIdleCommandProperty = BindableProperty.Create(nameof(DidBecomeIdleCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty CameraMoveStartedCommandProperty = BindableProperty.Create(nameof(CameraMoveStartedCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty CameraMoveCommandProperty = BindableProperty.Create(nameof(CameraMoveCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty CameraIdleCommandProperty = BindableProperty.Create(nameof(CameraIdleCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty CameraTrackingChangedCommandProperty = BindableProperty.Create(nameof(CameraTrackingChangedCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty CameraTrackingDismissedCommandProperty = BindableProperty.Create(nameof(CameraTrackingDismissedCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty MapClickCommandProperty = BindableProperty.Create(nameof(MapClickCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty MapLongClickCommandProperty = BindableProperty.Create(nameof(MapLongClickCommand), typeof(ICommand), typeof(MapLibreMap));
    public static readonly BindableProperty UserLocationUpdateCommandProperty = BindableProperty.Create(nameof(UserLocationUpdateCommand), typeof(ICommand), typeof(MapLibreMap));
    
    public ICommand? MapReadyCommand
    {
        get => (ICommand?)GetValue(MapReadyCommandProperty);
        set => SetValue(MapReadyCommandProperty, value);
    }

    public ICommand? StyleLoadedCommand
    {
        get => (ICommand?)GetValue(StyleLoadedCommandProperty);
        set => SetValue(StyleLoadedCommandProperty, value);
    }

    public ICommand? DidBecomeIdleCommand
    {
        get => (ICommand?)GetValue(DidBecomeIdleCommandProperty);
        set => SetValue(DidBecomeIdleCommandProperty, value);
    }

    public ICommand? CameraMoveStartedCommand
    {
        get => (ICommand?)GetValue(CameraMoveStartedCommandProperty);
        set => SetValue(CameraMoveStartedCommandProperty, value);
    }

    public ICommand? CameraMoveCommand
    {
        get => (ICommand?)GetValue(CameraMoveCommandProperty);
        set => SetValue(CameraMoveCommandProperty, value);
    }

    public ICommand? CameraIdleCommand
    {
        get => (ICommand?)GetValue(CameraIdleCommandProperty);
        set => SetValue(CameraIdleCommandProperty, value);
    }

    public ICommand? CameraTrackingChangedCommand
    {
        get => (ICommand?)GetValue(CameraTrackingChangedCommandProperty);
        set => SetValue(CameraTrackingChangedCommandProperty, value);
    }

    public ICommand? CameraTrackingDismissedCommand
    {
        get => (ICommand?)GetValue(CameraTrackingDismissedCommandProperty);
        set => SetValue(CameraTrackingDismissedCommandProperty, value);
    }

    public ICommand? MapClickCommand
    {
        get => (ICommand?)GetValue(MapClickCommandProperty);
        set =>  SetValue(MapClickCommandProperty, value);
    }

    public ICommand? MapLongClickCommand
    {
        get => (ICommand?)GetValue(MapLongClickCommandProperty);
        set =>   SetValue(MapLongClickCommandProperty, value);
    }

    public ICommand? UserLocationUpdateCommand
    {
        get => (ICommand?)GetValue(UserLocationUpdateCommandProperty);
        set =>   SetValue(UserLocationUpdateCommandProperty, value);
    }

    public string StyleUrl
    {
        get => (string)GetValue(StyleUrlProperty);
        set => SetValue(StyleUrlProperty, value);
    }
    
    public float MinZoom
    {
        get => (float)GetValue(MinZoomProperty);
        set => SetValue(MinZoomProperty, value);
    }
    
    public float MaxZoom
    {
        get => (float)GetValue(MaxZoomProperty);
        set => SetValue(MaxZoomProperty, value);
    }
    
    public bool RotateGesturesEnabled
    {
        get => (bool)GetValue(RotateGesturesEnabledProperty);
        set => SetValue(RotateGesturesEnabledProperty, value);
    }
    
    public bool ScrollGesturesEnabled
    {
        get => (bool)GetValue(ScrollGesturesEnabledProperty);
        set => SetValue(ScrollGesturesEnabledProperty, value);
    }
    
    public bool TiltGesturesEnabled
    {
        get => (bool)GetValue(TiltGesturesEnabledProperty);
        set => SetValue(TiltGesturesEnabledProperty, value);
    }
    
    public bool TrackCameraPosition
    {
        get => (bool)GetValue(TrackCameraPositionProperty);
        set => SetValue(TrackCameraPositionProperty, value);
    }
    
    public bool ZoomGesturesEnabled
    {
        get => (bool)GetValue(ZoomGesturesEnabledProperty);
        set => SetValue(ZoomGesturesEnabledProperty, value);
    }
    
    public bool MyLocationEnabled
    {
        get => (bool)GetValue(MyLocationEnabledProperty);
        set => SetValue(MyLocationEnabledProperty, value);
    }
    
    public int MyLocationTrackingMode
    {
        get => (int)GetValue(MyLocationTrackingModeProperty);
        set => SetValue(MyLocationTrackingModeProperty, value);
    }
    
    public int MyLocationRenderMode
    {
        get => (int)GetValue(MyLocationRenderModeProperty);
        set => SetValue(MyLocationRenderModeProperty, value);
    }
    
    public int?[]? LogoViewMargins
    {
        get => (int?[])GetValue(LogoViewMarginsProperty);
        set => SetValue(LogoViewMarginsProperty, value);
    }
    
    public int CompassGravity
    {
        get => (int)GetValue(CompassGravityProperty);
        set => SetValue(CompassGravityProperty, value);
    }
    
    public int?[]? CompassViewMargins
    {
        get => (int?[])GetValue(CompassViewMarginsProperty);
        set => SetValue(CompassViewMarginsProperty, value);
    }
    
    public int AttributionButtonGravity
    {
        get => (int)GetValue(AttributionButtonGravityProperty);
        set => SetValue(AttributionButtonGravityProperty, value);
    }
    
    public int?[]? AttributionButtonMargins
    {
        get => (int?[])GetValue(AttributionButtonMarginsProperty);
        set => SetValue(AttributionButtonMarginsProperty, value);
    }

    public bool ShowNavigationControls
    {
        get => (bool)GetValue(ShowNavigationControlsProperty);
        set => SetValue(ShowNavigationControlsProperty, value);
    }

    public bool ShowGpsControl
    {
        get => (bool)GetValue(ShowGpsControlProperty);
        set => SetValue(ShowGpsControlProperty, value);
    }

    public bool ShowAttributionControl
    {
        get => (bool)GetValue(ShowAttributionControlProperty);
        set => SetValue(ShowAttributionControlProperty, value);
    }

    public string? CustomAttribution
    {
        get => (string?)GetValue(CustomAttributionProperty);
        set => SetValue(CustomAttributionProperty, value);
    }

    /// <summary>
    /// Extra multiplier applied to the platform pixel ratio at map creation (scales
    /// all style-pixel sizes: text, icons, circles, line widths). Default 1.0.
    /// Set before the map is displayed — read once when the platform view is created.
    /// </summary>
    public double UiScale
    {
        get => (double)GetValue(UiScaleProperty);
        set => SetValue(UiScaleProperty, value);
    }

    /// <summary>
    /// Corner the navigation control is anchored to. When multiple controls share
    /// a corner they stack (navigation, then GPS, then attribution).
    /// </summary>
    public MapControlCorner NavigationControlPosition
    {
        get => (MapControlCorner)GetValue(NavigationControlPositionProperty);
        set => SetValue(NavigationControlPositionProperty, value);
    }

    /// <summary>
    /// Corner the GPS control is anchored to. When multiple controls share a
    /// corner they stack (navigation, then GPS, then attribution).
    /// </summary>
    public MapControlCorner GpsControlPosition
    {
        get => (MapControlCorner)GetValue(GpsControlPositionProperty);
        set => SetValue(GpsControlPositionProperty, value);
    }

    /// <summary>
    /// Corner the attribution control is anchored to. When multiple controls share
    /// a corner they stack (navigation, then GPS, then attribution).
    /// </summary>
    public MapControlCorner AttributionControlPosition
    {
        get => (MapControlCorner)GetValue(AttributionControlPositionProperty);
        set => SetValue(AttributionControlPositionProperty, value);
    }

    public void AddGeoJsonSource(string sourceName, FeatureCollection collection)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var json = JsonSerializer.Serialize(collection);
        controller.AddGeoJsonSource(sourceName, json);
    }

    /// <summary>Replaces the GeoJSON data of an existing source.</summary>
    public void SetGeoJsonSource(string sourceName, FeatureCollection collection)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var json = JsonSerializer.Serialize(collection);
        handler.Controller.SetGeoJsonSource(sourceName, json);
    }

    /// <summary>Removes a source previously added to the style. No-op if it does not exist.</summary>
    public void RemoveSource(string sourceName)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        handler.Controller.RemoveSource(sourceName);
    }

    /// <summary>Removes a layer previously added to the style. No-op if it does not exist.</summary>
    public void RemoveLayer(string layerId)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        handler.Controller.RemoveLayer(layerId);
    }

    public void AddImageSource(string sourceName, string imageUri, LatLngQuad? coordinates)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        controller.AddImageSource(sourceName, imageUri, coordinates);
    }

    public void AddRasterSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates, int tileSize,
        int minZoom, int maxZoom)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        controller.AddRasterSource(sourceName, tileUrl, tileUrlTemplates, tileSize, minZoom, maxZoom);
    }

    public void AddRasterDemSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates, int tileSize, int minZoom, int maxZoom)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        controller.AddRasterDemSource(sourceName, tileUrl, tileUrlTemplates, tileSize, minZoom, maxZoom);
    }

    public void AddVectorSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates,
        int minZoom, int maxZoom)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        controller.AddVectorSource(sourceName, tileUrl, tileUrlTemplates, minZoom, maxZoom);
    }

    public void AddLineLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        LineLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddLineLayer(layerName, sourceName, belowLayerId, sourceLayer, propertyValues, minZoom, maxZoom, enableInteraction);
    }
    
    public void AddSymbolLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        SymbolLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddSymbolLayer(layerName, sourceName, belowLayerId, sourceLayer, propertyValues, minZoom, maxZoom, enableInteraction);
    }
    
    public void AddCircleLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        CircleLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddCircleLayer(layerName, sourceName, belowLayerId, sourceLayer, propertyValues, minZoom, maxZoom, enableInteraction);
    }

    public void AddFillLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        FillLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddFillLayer(layerName, sourceName, belowLayerId, sourceLayer, propertyValues, minZoom, maxZoom, enableInteraction);
    }

    public void AddFillExtrusionLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        string? sourceLayer,
        FillExtrusionLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0,
        bool enableInteraction = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddFillExtrusionLayer(layerName, sourceName, belowLayerId, sourceLayer, propertyValues, minZoom, maxZoom, enableInteraction);
    }
    
    public void AddRasterLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        RasterLayerProperties properties,
        float minZoom = 0,
        float maxZoom = 0)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddRasterLayer(layerName, sourceName, propertyValues, minZoom, maxZoom, belowLayerId);
    }

    public void AddHeatmapLayer(
        string layerName,
        string sourceName,
        string? belowLayerId,
        HeatmapProperties properties,
        float minZoom = 0,
        float maxZoom = 0)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        var controller = handler.Controller;
        var propertyValues = properties.ToDictionary();
        controller.AddHeatmapLayer(layerName, sourceName, propertyValues, minZoom, maxZoom, belowLayerId);
    }
    // ── Sprite images ───────────────────────────────────────────────────────────

    /// <summary>Register a named sprite image for use with <c>icon-image</c> in SymbolLayers.
    /// <paramref name="rgba"/> must be <c>width × height × 4</c> bytes of premultiplied RGBA.</summary>
    public void AddSpriteImage(string imageId, int width, int height, byte[] rgba, float pixelRatio = 1f, bool sdf = false)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        handler.Controller.AddSpriteImage(imageId, width, height, rgba, pixelRatio, sdf);
    }

    /// <summary>Remove a sprite image previously registered with <see cref="AddSpriteImage"/>.</summary>
    public void RemoveSpriteImage(string imageId)
    {
        if (Handler is not MapLibreMapHandler handler) return;
        handler.Controller.RemoveSpriteImage(imageId);
    }
    // ── Feature queries ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns GeoJSON of rendered features within a box centred on
    /// (<paramref name="cx"/>, <paramref name="cy"/>) with <paramref name="thresholdPx"/>
    /// pixels on each side, optionally filtered to <paramref name="layerIds"/>.
    /// </summary>
    public string? QueryRenderedFeaturesInBox(double cx, double cy, double thresholdPx = 5,
        string? layerIds = null)
    {
        if (Handler is not MapLibreMapHandler handler) return null;
        return handler.Controller.QueryRenderedFeaturesInBox(
            cx - thresholdPx, cy - thresholdPx,
            cx + thresholdPx, cy + thresholdPx,
            layerIds);
    }

    /// <summary>Returns GeoJSON of rendered features within the given screen-space box.</summary>
    public string? QueryRenderedFeaturesInBox(double x1, double y1, double x2, double y2,
        string? layerIds = null)
    {
        if (Handler is not MapLibreMapHandler handler) return null;
        return handler.Controller.QueryRenderedFeaturesInBox(x1, y1, x2, y2, layerIds);
    }

    /// <summary>Converts a geographic coordinate to a screen point (physical pixels).</summary>
    public (double X, double Y) LatLngToScreenPoint(double latitude, double longitude)
    {
        if (Handler is not MapLibreMapHandler handler) return (0, 0);
        return handler.Controller.LatLngToScreenPoint(latitude, longitude);
    }

    // ── Visible region ───────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> once the map's style has finished loading (the <see cref="StyleLoaded"/>
    /// event has fired at least once). Declarative overlay elements use this to materialise
    /// immediately when they are added after the map is already ready.
    /// </summary>
    public bool IsStyleLoaded { get; private set; }

    private MapLibreNative.Maui.Geometry.MapSpan? _visibleRegion;

    /// <summary>
    /// The map region currently visible on screen, as a
    /// <see cref="MapLibreNative.Maui.Geometry.MapSpan"/>. Refreshed whenever the camera becomes
    /// idle and <see langword="null"/> until the map has rendered its first frame. Mirrors
    /// <c>Microsoft.Maui.Controls.Maps.Map.VisibleRegion</c>.
    /// </summary>
    public MapLibreNative.Maui.Geometry.MapSpan? VisibleRegion
    {
        get => _visibleRegion;
        private set
        {
            if (Equals(_visibleRegion, value)) return;
            _visibleRegion = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Reads the map's currently visible region on demand. Returns <see langword="null"/> when the
    /// handler is not connected or the map is not yet ready.
    /// </summary>
    public MapLibreNative.Maui.Geometry.MapSpan? GetVisibleRegion()
    {
        if (Handler is not MapLibreMapHandler handler) return null;
        var (latSW, lonSW, latNE, lonNE) = handler.Controller.GetVisibleBounds();
        if (double.IsNaN(latSW) || double.IsNaN(lonSW) || double.IsNaN(latNE) || double.IsNaN(lonNE))
            return null;
        double centerLat = (latSW + latNE) / 2.0;
        double centerLon = (lonSW + lonNE) / 2.0;
        double latDegrees = System.Math.Abs(latNE - latSW);
        double lonDegrees = System.Math.Abs(lonNE - lonSW);
        return new MapLibreNative.Maui.Geometry.MapSpan(
            new MapLibreNative.Maui.Geometry.MapCoordinate(centerLat, centerLon), latDegrees, lonDegrees);
    }

    // TODO Map parameter may want to return the controller here. 
    public event EventHandler<MapReadyEventArgs>? MapReady;
    public event EventHandler? DidBecomeIdle;
    // TODO int parameter
    public event EventHandler<CameraMoveStartedEventArgs>? CameraMoveStarted;
    public event EventHandler? CameraMove;
    public event EventHandler? CameraIdle;
    // TODO int parameter
    public event EventHandler<CameraTrackingChangedEventArgs>? CameraTrackingChanged;
    public event EventHandler? CameraTrackingDismissed;
    // LatLng and bool parameter
    public event EventHandler<MapClickEventArgs>? MapClick;
    // LatLng and bool parameter
    public event EventHandler<MapClickEventArgs>? MapLongClick;
    // TODO style parameter
    public event EventHandler<StyleLoadedEventArgs>? StyleLoaded;
    // TODO Location parameter
    public event EventHandler<UserLocationUpdateEventArgs>? UserLocationUpdate;
    
    internal void OnMapReady(Map map)
    {
        var args = new MapReadyEventArgs
        {
            Map = map
        };
        MapReady?.Invoke(this, args);
        MapReadyCommand?.Execute(map);
    }
    
    internal void OnStyleLoaded(Style style)
    {
        IsStyleLoaded = true;
        var args = new StyleLoadedEventArgs
        {
            Style = style
        };
        StyleLoaded?.Invoke(this, args);
        StyleLoadedCommand?.Execute(style);
    }

    internal void OnDidBecomeIdle()
    {
        DidBecomeIdle?.Invoke(this, System.EventArgs.Empty);
        DidBecomeIdleCommand?.Execute(null);
    }

    internal void OnCameraMoveStarted(int reason)
    {
        var args = new CameraMoveStartedEventArgs
        {
            Reason = reason
        };
        CameraMoveStarted?.Invoke(this, args);
        CameraMoveCommand?.Execute(reason);
    }

    internal void OnCameraMove()
    {
        CameraMove?.Invoke(this, System.EventArgs.Empty);
        CameraMoveCommand?.Execute(null);
    }

    internal void OnCameraIdle()
    {
        VisibleRegion = GetVisibleRegion();
        CameraIdle?.Invoke(this, System.EventArgs.Empty);
        CameraIdleCommand?.Execute(null);
    }

    internal void OnCameraTrackingChanged(int mode)
    {
        var args = new CameraTrackingChangedEventArgs
        {
            Mode = mode
        };
        CameraTrackingChanged?.Invoke(this, args);
        CameraTrackingChangedCommand?.Execute(mode);
    }

    internal void OnCameraTrackingDismissed()
    {
        CameraTrackingDismissed?.Invoke(this, System.EventArgs.Empty);
        CameraTrackingDismissedCommand?.Execute(null);
    }

    internal bool OnMapClick(LatLng latLng, double screenX = 0, double screenY = 0)
    {
        var args = new MapClickEventArgs
        {
            LatLng  = latLng,
            ScreenX = screenX,
            ScreenY = screenY,
        };
        MapClick?.Invoke(this, args);
        MapClickCommand?.Execute(latLng);
        return false;
    }

    internal bool OnMapLongClick(LatLng latLng, double screenX = 0, double screenY = 0)
    {
        var args = new MapClickEventArgs
        {
            LatLng  = latLng,
            ScreenX = screenX,
            ScreenY = screenY,
        };
        MapLongClick?.Invoke(this, args);
        MapLongClickCommand?.Execute(latLng);
        return false;
    }

    internal void OnUserLocationUpdate(Location location)
    {
        var args = new UserLocationUpdateEventArgs
        {
            Location = location
        };
        UserLocationUpdate?.Invoke(this, args);
        UserLocationUpdateCommand?.Execute(location);
    }
}



