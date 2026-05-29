namespace MapLibreNative.Maui.Handlers.Maps;

public class Style
{
    private object _platform;
    public bool IsFullyLoaded { get; set; }
    public string Uri { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public IList<Source> Sources { get; set; } = new List<Source>();
    public IList<Layer> Layers { get; set; } = new List<Layer>();
    
    public Style(object platform)
    {
        _platform = platform;
    }
}