using MapLibreNative.Maui.Handlers.Geometry;

namespace MapLibreNative.Maui.Handlers.Sources;

public class ImageSource : SourceView
{
    public static readonly BindableProperty UrlProperty =
        BindableProperty.Create(nameof(Url), typeof(string), typeof(ImageSource), null);
    public static readonly BindableProperty CoordinatesProperty = BindableProperty.Create(nameof(Coordinates), typeof(LatLngQuad), typeof(ImageSource), null);

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public LatLngQuad? Coordinates
    {
        get => (LatLngQuad?)GetValue(CoordinatesProperty);
        set => SetValue(CoordinatesProperty, value);
    }
    
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        if (string.IsNullOrEmpty(Url)) return;
        parentMap.AddImageSource(SourceName, Url, Coordinates);
    }
}