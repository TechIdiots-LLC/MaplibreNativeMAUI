namespace MapLibreNative.Maui.Handlers.Maps;

public class Layer
{
    private object? _platform;
    public string Id { get; set; } = string.Empty;
    public bool IsDetached { get; set; }
    public float MinZoom { get; set; }
    public float MaxZoom { get; set; }
    public bool Visibility { get; set; }

    public Layer(object platform)
    {
        _platform = platform;
    }
}