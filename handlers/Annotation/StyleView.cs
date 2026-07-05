using MapLibreNative.Maui.Handlers.EventArgs;

namespace MapLibreNative.Maui.Handlers.Annotation;

public class StyleView : ContentView
{
    protected bool IsAdded;
    private MapLibreMap? _parentMap;

    private static readonly string _diagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maplibre_maui_diag.log");
    private void SDiag(string msg)
    {
        try { System.IO.File.AppendAllText(_diagPath,
            $"{DateTime.Now:HH:mm:ss.fff} [sv {GetType().Name}#{GetHashCode():X}] {msg}\r\n"); }
        catch { /* ignore */ }
    }

    protected override void OnParentChanged()
    {
        base.OnParentChanged();
        var parentMap = FindParentMapLibreMap(this);
        SDiag($"OnParentChanged parentMap={(parentMap == null ? "null" : parentMap.GetHashCode().ToString("X"))} prev={(_parentMap == null ? "null" : _parentMap.GetHashCode().ToString("X"))} IsAdded={IsAdded}");
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

    private void OnStyleLoaded(object? sender, StyleLoadedEventArgs e)
    {
        // A StyleLoaded event means the native style was (re)loaded fresh — it contains NONE of our
        // sources/layers. This happens on a style-URL change AND, crucially, when the handler is
        // rebuilt on tab re-entry (AppShell caches the page/overlay elements, so IsAdded is stale
        // true from the previous native map). Reset so we re-add to the new style; otherwise the
        // overlays never reappear and in-place source updates (grow/shrink) find no source.
        SDiag($"OnStyleLoaded IsAdded={IsAdded} -> re-add");
        IsAdded = false;
        Materialise();
    }

    private void Materialise()
    {
        if (IsAdded) return;
        SDiag("Materialise -> AddLayerToParentMap");
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