namespace MapLibreNative.Maui.Handlers.Maps;

public class Source
{
    private object _platform;
    public string Attribution { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;

    public Source(object platform)
    {
        _platform = platform;
    }
}