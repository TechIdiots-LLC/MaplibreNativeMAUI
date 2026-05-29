using MapLibreNative.Maui.Handlers.Annotation;

namespace MapLibreNative.Maui.Handlers.Layers;

public class LayerView<T>: StyleView
{
     // Bindable properties for LineLayer parameters
    public static readonly BindableProperty LayerNameProperty = BindableProperty.Create(nameof(LayerName), typeof(string), typeof(LineLayer), null, propertyChanged: OnPropertiesChanged);
    public string LayerName
    {
        get => (string)GetValue(LayerNameProperty);
        set => SetValue(LayerNameProperty, value);
    }

    public static readonly BindableProperty SourceNameProperty = BindableProperty.Create(nameof(SourceName), typeof(string), typeof(LineLayer), null, propertyChanged: OnPropertiesChanged);
    public string SourceName
    {
        get => (string)GetValue(SourceNameProperty);
        set => SetValue(SourceNameProperty, value);
    }

    public static readonly BindableProperty BelowLayerIdProperty = BindableProperty.Create(nameof(BelowLayerId), typeof(string), typeof(LineLayer), null, propertyChanged: OnPropertiesChanged);
    public string BelowLayerId
    {
        get => (string)GetValue(BelowLayerIdProperty);
        set => SetValue(BelowLayerIdProperty, value);
    }

    public static readonly BindableProperty SourceLayerProperty = BindableProperty.Create(nameof(SourceLayer), typeof(string), typeof(LineLayer), null, propertyChanged: OnPropertiesChanged);
    public string SourceLayer
    {
        get => (string)GetValue(SourceLayerProperty);
        set => SetValue(SourceLayerProperty, value);
    }

    public static readonly BindableProperty PropertiesProperty = BindableProperty.Create(nameof(Properties), typeof(T), typeof(LineLayer), null, propertyChanged: OnPropertiesChanged);
    public T? Properties
    {
        get => (T?)GetValue(PropertiesProperty);
        set => SetValue(PropertiesProperty, value);
    }

    public static readonly BindableProperty MinZoomProperty = BindableProperty.Create(nameof(MinZoom), typeof(float), typeof(LineLayer), 0f, propertyChanged: OnPropertiesChanged);
    public float MinZoom
    {
        get => (float)GetValue(MinZoomProperty);
        set => SetValue(MinZoomProperty, value);
    }

    public static readonly BindableProperty MaxZoomProperty = BindableProperty.Create(nameof(MaxZoom), typeof(float), typeof(LineLayer), 0f, propertyChanged: OnPropertiesChanged);
    public float MaxZoom
    {
        get => (float)GetValue(MaxZoomProperty);
        set => SetValue(MaxZoomProperty, value);
    }

    public static readonly BindableProperty EnableInteractionProperty = BindableProperty.Create(nameof(EnableInteraction), typeof(bool), typeof(LineLayer), false, propertyChanged: OnPropertiesChanged);
    public bool EnableInteraction
    {
        get => (bool)GetValue(EnableInteractionProperty);
        set => SetValue(EnableInteractionProperty, value);
    }

    private static void OnPropertiesChanged(BindableObject bindable, object oldValue, object newValue)
    {
        // You could trigger adding here
    }
}