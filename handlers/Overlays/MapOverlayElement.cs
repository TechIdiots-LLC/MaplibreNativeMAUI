using System.Globalization;
using GeoJSON.Text.Feature;
using GeoJSON.Text.Geometry;
using MapLibreNative.Maui.Handlers.Annotation;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MapLibreNative.Maui.Handlers.Overlays;

/// <summary>
/// Base class for declarative "draw over the map" overlay elements
/// (<see cref="Circle"/>, <see cref="Polyline"/>, <see cref="Polygon"/>, <see cref="Pin"/>).
/// </summary>
/// <remarks>
/// The element model mirrors <c>Microsoft.Maui.Controls.Maps</c> (Pin/Polyline/Polygon/Circle),
/// but each element is wired as a declarative child of <see cref="MapLibreMap"/> — the same
/// <see cref="StyleView"/> pattern used by the low-level sources and layers. On the map's
/// <c>StyleLoaded</c> event the element materialises itself as a GeoJSON source plus one or more
/// style layers, so it renders <em>inside</em> the MapLibre surface and therefore composites
/// correctly on every platform. Changing a visual or geometry property after the element has been
/// added rebuilds it in place.
/// </remarks>
public abstract class MapOverlayElement : StyleView
{
    /// <summary>Stable per-element id used to derive source and layer ids.</summary>
    protected string ElementId { get; } = "ovl_" + Guid.NewGuid().ToString("N");

    /// <summary>The GeoJSON source id backing this element.</summary>
    protected string SourceId => ElementId + "_src";

    /// <summary>The parent map, cached once the element is materialised.</summary>
    protected MapLibreMap? Map { get; private set; }

    // ── Shared stroke properties (parity with MAUI MapElement) ────────────────

    public static readonly BindableProperty StrokeColorProperty = BindableProperty.Create(
        nameof(StrokeColor), typeof(Color), typeof(MapOverlayElement), null, propertyChanged: OnVisualChanged);

    public Color? StrokeColor
    {
        get => (Color?)GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    public static readonly BindableProperty StrokeWidthProperty = BindableProperty.Create(
        nameof(StrokeWidth), typeof(double), typeof(MapOverlayElement), 2.0, propertyChanged: OnVisualChanged);

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected sealed override void AddLayerToParentMap()
    {
        Map = FindParentMapLibreMap(this);
        if (Map == null) return;
        BuildOverlay(Map);
    }

    /// <summary>Rebuilds the element in place. Called when a bindable property changes after add.</summary>
    protected void Refresh()
    {
        if (Map == null || !IsAdded) return;
        RemoveOverlay(Map);
        BuildOverlay(Map);
    }

    /// <summary>Adds this element's source and layer(s) to <paramref name="map"/>.</summary>
    protected abstract void BuildOverlay(MapLibreMap map);

    /// <summary>Removes this element's layer(s) and source from <paramref name="map"/>.</summary>
    protected abstract void RemoveOverlay(MapLibreMap map);

    /// <summary>Property-changed handler that rebuilds the element when a visual/geometry property changes.</summary>
    protected static void OnVisualChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is MapOverlayElement e)
            e.Refresh();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Converts a MAUI <see cref="Color"/> to a MapLibre <c>rgba(r,g,b,a)</c> style string.</summary>
    protected static string ToMlnColor(Color? color, Color fallback)
    {
        var c = color ?? fallback;
        int r = (int)Math.Round(c.Red * 255);
        int g = (int)Math.Round(c.Green * 255);
        int b = (int)Math.Round(c.Blue * 255);
        string a = c.Alpha.ToString(CultureInfo.InvariantCulture);
        return $"rgba({r},{g},{b},{a})";
    }

    /// <summary>Builds a single-feature <see cref="FeatureCollection"/> for the given geometry.</summary>
    protected FeatureCollection ToFeatureCollection(IGeometryObject geometry, IDictionary<string, object>? properties = null)
        => new(new List<Feature> { new(geometry, properties) });

    /// <summary>Converts a list of <see cref="Location"/> to GeoJSON positions.</summary>
    protected static List<IPosition> ToPositions(IEnumerable<Location> locations)
    {
        var list = new List<IPosition>();
        foreach (var l in locations)
            list.Add(new Position(l.Latitude, l.Longitude));
        return list;
    }
}
