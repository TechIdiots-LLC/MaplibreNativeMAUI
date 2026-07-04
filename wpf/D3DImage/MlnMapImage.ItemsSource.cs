// Data-bound markers for MlnMapImage (WPF).
//
// WPF has no MAUI overlay-element model, so instead of templating items into Pin/Polyline views,
// the control materialises an ItemsSource of MlnMapMarker into a single managed GeoJSON source with
// a circle layer (the dot) and a symbol layer (the label). Collections implementing
// INotifyCollectionChanged, and markers implementing INotifyPropertyChanged (MlnMapMarker does),
// update the map live.
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MapLibreNative.Maui;

namespace MapLibreNative.Maui.WPF;

public partial class MlnMapImage
{
    private const string ItemsSourceId = "__mln_items";
    private const string ItemsCircleLayerId = "__mln_items_circles";
    private const string ItemsLabelLayerId = "__mln_items_labels";
    private static readonly Color DefaultMarkerColor = Color.FromRgb(0xE5, 0x5E, 0x5E); // red

    private readonly HashSet<INotifyPropertyChanged> _subscribedItems = new();

    /// <summary>
    /// A collection of <see cref="MlnMapMarker"/> rendered as data-bound markers on the map. Bind an
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> from a view model; markers
    /// are drawn as coloured dots with optional text labels and kept in sync when the collection or an
    /// individual marker changes. Items that are not <see cref="MlnMapMarker"/> are ignored.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Bindable property for <see cref="ItemsSource"/>.</summary>
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(MlnMapImage),
            new PropertyMetadata(null, static (d, e) =>
                ((MlnMapImage)d).OnItemsSourceChanged(e.OldValue as IEnumerable, e.NewValue as IEnumerable)));

    private void OnItemsSourceChanged(IEnumerable? oldSource, IEnumerable? newSource)
    {
        if (oldSource is INotifyCollectionChanged oldNcc)
            oldNcc.CollectionChanged -= OnItemsCollectionChanged;
        if (newSource is INotifyCollectionChanged newNcc)
            newNcc.CollectionChanged += OnItemsCollectionChanged;

        ResubscribeItems();
        RebuildItemsLayer();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        RebuildItemsLayer();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => RebuildItemsLayer();

    // Track per-marker PropertyChanged subscriptions so a Reset (which carries no OldItems) can't leak.
    private void ResubscribeItems()
    {
        foreach (var p in _subscribedItems)
            p.PropertyChanged -= OnItemPropertyChanged;
        _subscribedItems.Clear();

        if (ItemsSource == null) return;
        foreach (var item in ItemsSource)
            if (item is INotifyPropertyChanged p && _subscribedItems.Add(p))
                p.PropertyChanged += OnItemPropertyChanged;
    }

    private void RebuildItemsLayer()
    {
        // Requires a loaded style; re-invoked from the style-loaded observer when one becomes ready.
        if (_style == null || !_styleReady) return;

        var markers = new List<MlnMapMarker>();
        if (ItemsSource != null)
            foreach (var item in ItemsSource)
                if (item is MlnMapMarker marker)
                    markers.Add(marker);

        if (markers.Count == 0)
        {
            if (_style.HasLayer(ItemsLabelLayerId)) _style.RemoveLayer(ItemsLabelLayerId);
            if (_style.HasLayer(ItemsCircleLayerId)) _style.RemoveLayer(ItemsCircleLayerId);
            if (_style.HasSource(ItemsSourceId)) _style.RemoveSource(ItemsSourceId);
            _renderNeedsUpdate = true;
            return;
        }

        string geojson = BuildFeatureCollection(markers);
        MbglSource src = _style.HasSource(ItemsSourceId)
            ? _style.GetSource(ItemsSourceId)!
            : _style.AddGeoJsonSource(ItemsSourceId);
        src.SetGeoJson(geojson);

        if (!_style.HasLayer(ItemsCircleLayerId))
        {
            var circle = _style.AddCircleLayer(ItemsCircleLayerId, ItemsSourceId);
            circle.SetPaintProperty("circle-radius", "7");
            circle.SetPaintProperty("circle-color", "[\"get\",\"color\"]");
            circle.SetPaintProperty("circle-stroke-width", "2");
            circle.SetPaintProperty("circle-stroke-color", "\"#FFFFFF\"");
        }

        if (!_style.HasLayer(ItemsLabelLayerId))
        {
            var label = _style.AddSymbolLayer(ItemsLabelLayerId, ItemsSourceId);
            label.SetLayoutProperty("text-field", "[\"get\",\"label\"]");
            label.SetLayoutProperty("text-size", "12");
            label.SetLayoutProperty("text-offset", "[0, 1.2]");
            label.SetLayoutProperty("text-anchor", "\"top\"");
            label.SetPaintProperty("text-color", "\"#333333\"");
            label.SetPaintProperty("text-halo-color", "\"#FFFFFF\"");
            label.SetPaintProperty("text-halo-width", "1.5");
        }

        _renderNeedsUpdate = true;
    }

    private static string BuildFeatureCollection(IReadOnlyList<MlnMapMarker> markers)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"FeatureCollection\",\"features\":[");
        for (int i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
              .Append(m.Longitude.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.Latitude.ToString("R", CultureInfo.InvariantCulture))
              .Append("]},\"properties\":{\"label\":")
              .Append(System.Text.Json.JsonSerializer.Serialize(m.Label ?? string.Empty))
              .Append(",\"color\":\"").Append(ToHex(m.Color)).Append("\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ToHex(Color? color)
    {
        var c = color ?? DefaultMarkerColor;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
