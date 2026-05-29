using MapLibreNative.Maui.Handlers.EventArgs;

namespace MapLibreNative.Maui.Handlers.Annotation;

public class StyleView : ContentView
{
    protected bool IsAdded;
    
    protected override void OnParentChanged()
    {
        base.OnParentChanged();
        if (IsAdded) return;
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;

        parentMap.StyleLoaded += OnStyleLoaded;
    }

    private void OnStyleLoaded(object? sender, StyleLoadedEventArgs e)
    {
        AddLayerToParentMap();
        IsAdded = true;
    }

    protected virtual void AddLayerToParentMap()
    {
        
    }

    protected static MapLibreMap? FindParentMapLibreMap(Element element)
    {
        var parent = element.Parent;
        while (parent != null)
        {
            if (parent is MapLibreMap map)
            {
                return map;
            }
            parent = parent.Parent;
        }
        return null;
    }
}