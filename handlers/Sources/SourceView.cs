using MapLibreNative.Maui.Handlers.Annotation;

namespace MapLibreNative.Maui.Handlers.Sources;

public class SourceView : StyleView
{
    public static readonly BindableProperty SourceNameProperty =
        BindableProperty.Create(nameof(SourceName), typeof(string), typeof(GeoJsonSource), null);
    public static readonly BindableProperty MinZoomProperty = BindableProperty.Create(nameof(MinZoom), typeof(int), typeof(RasterDemSource), 0);
    public static readonly BindableProperty MaxZoomProperty = BindableProperty.Create(nameof(MaxZoom), typeof(int), typeof(RasterDemSource), 22);
    
    public string SourceName
    {
        get => (string)GetValue(SourceNameProperty);
        set => SetValue(SourceNameProperty, value);
    }
    
    public int MinZoom
    {
        get => (int)GetValue(MinZoomProperty);
        set => SetValue(MinZoomProperty, value);
    }

    public int MaxZoom
    {
        get => (int)GetValue(MaxZoomProperty);
        set => SetValue(MaxZoomProperty, value);
    }
}