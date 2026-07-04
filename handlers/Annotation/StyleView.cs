using MapLibreNative.Maui.Handlers.EventArgs;

namespace MapLibreNative.Maui.Handlers.Annotation;

public class StyleView : ContentView
{
    protected bool IsAdded;
    private MapLibreMap? _parentMap;

    protected override void OnParentChanged()
    {
        base.OnParentChanged();
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == _parentMap) return;

        // Detached from (or re-parented away from) the previous map: tear down.
        if (_parentMap != null)
        {
            _parentMap.StyleLoaded -= OnStyleLoaded;
            if (IsAdded) RemoveLayerFromParentMap();
            IsAdded = false;
        }

        _parentMap = parentMap;
        if (parentMap == null) return;

        parentMap.StyleLoaded += OnStyleLoaded;
        // If the style is already loaded (element added after the map became ready), materialise
        // now — StyleLoaded will not fire again for this element.
        if (parentMap.IsStyleLoaded) Materialise();
    }

    private void OnStyleLoaded(object? sender, StyleLoadedEventArgs e) => Materialise();

    private void Materialise()
    {
        if (IsAdded) return;
        AddLayerToParentMap();
        IsAdded = true;
    }

    protected virtual void AddLayerToParentMap()
    {

    }

    /// <summary>
    /// Removes this element's contribution (sources/layers) from the map. Called when the element
    /// is removed from the map's visual tree. The default implementation does nothing.
    /// </summary>
    protected virtual void RemoveLayerFromParentMap()
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