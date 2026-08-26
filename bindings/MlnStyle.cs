/**
 * MlnStyle.cs — Typed wrapper around mln_style_t (non-owning, valid for the
 * lifetime of its parent MlnMap).
 */
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MapLibreNative.Maui;

/// <summary>
/// Provides access to sources and layers.  This is a <em>non-owning</em> handle
/// — do not dispose it; it is invalidated when the parent <see cref="MlnMap"/> is disposed.
/// </summary>
public sealed class MlnStyle
{
    internal IntPtr Handle { get; }

    internal MlnStyle(IntPtr handle) => Handle = handle;

    // ── Sources ───────────────────────────────────────────────────────────────

    public bool HasSource(string sourceId)
        => NativeMethods.StyleHasSource(Handle, sourceId) != 0;

    public MlnSource AddGeoJsonSource(string sourceId)
        => new(NativeMethods.StyleAddGeoJsonSource(Handle, sourceId));

    public MlnSource AddGeoJsonSourceUrl(string sourceId, string url)
        => new(NativeMethods.StyleAddGeoJsonSourceUrl(Handle, sourceId, url));

    /// <summary>
    /// Add a GeoJSON source with style-spec options (clustering etc.).
    /// <paramref name="optionsJson"/> is a JSON object of GeoJSON source options —
    /// the style-spec keys minus <c>type</c>/<c>data</c>: <c>cluster</c>,
    /// <c>clusterRadius</c>, <c>clusterMaxZoom</c>, <c>clusterMinPoints</c>,
    /// <c>clusterProperties</c>, <c>maxzoom</c>, <c>buffer</c>, <c>tolerance</c>,
    /// <c>lineMetrics</c>. Set data afterwards with <see cref="MlnSource.SetGeoJson"/>.
    /// </summary>
    public MlnSource AddGeoJsonSourceOptions(string sourceId, string? optionsJson)
        => new(NativeMethods.StyleAddGeoJsonSourceOptions(Handle, sourceId, optionsJson));

    public MlnSource AddVectorSource(string sourceId, string url)
        => new(NativeMethods.StyleAddVectorSource(Handle, sourceId, url));

    public MlnSource AddRasterSource(string sourceId, string url, int tileSize = 512)
        => new(NativeMethods.StyleAddRasterSource(Handle, sourceId, url, tileSize));

    public MlnSource AddRasterDemSource(string sourceId, string url, int tileSize = 512)
        => new(NativeMethods.StyleAddRasterDemSource(Handle, sourceId, url, tileSize));

    /// <summary>
    /// Add a vector source from explicit <c>{z}/{x}/{y}</c> tile URL templates rather than
    /// a TileJSON URL, optionally with an <paramref name="attribution"/>. A TileJSON source
    /// carries its own attribution; templates have nowhere else to declare one.
    /// </summary>
    public void AddVectorTilesSource(string sourceId, IEnumerable<string> tileUrlTemplates,
        int minZoom = 0, int maxZoom = 22, string? attribution = null)
        => AddTileSourceJson("vector", sourceId, tileUrlTemplates, tileSize: null,
                             minZoom, maxZoom, encoding: null, attribution);

    /// <summary>
    /// Add a raster source from explicit <c>{z}/{x}/{y}</c> tile URL templates rather than
    /// a TileJSON URL, optionally with an <paramref name="attribution"/>. A TileJSON source
    /// carries its own attribution; templates have nowhere else to declare one.
    /// </summary>
    public void AddRasterTilesSource(string sourceId, IEnumerable<string> tileUrlTemplates,
        int tileSize = 512, int minZoom = 0, int maxZoom = 22, string? attribution = null)
        => AddTileSourceJson("raster", sourceId, tileUrlTemplates, tileSize,
                             minZoom, maxZoom, encoding: null, attribution);

    /// <summary>
    /// Add a raster-dem source from explicit <c>{z}/{x}/{y}</c> tile URL templates rather
    /// than a TileJSON URL. Use this when the DEM has no TileJSON, or when the elevation
    /// <paramref name="encoding"/> must be declared — <c>"terrarium"</c> (AWS/Mapzen
    /// terrain-tiles) decodes differently from the default <c>"mapbox"</c> encoding, and
    /// mis-declaring it renders garbage elevation.
    /// </summary>
    /// <param name="attribution">Attribution HTML for the source, shown by the attribution control.</param>
    public void AddRasterDemTilesSource(string sourceId, IEnumerable<string> tileUrlTemplates,
        int tileSize = 512, int minZoom = 0, int maxZoom = 15,
        string? encoding = null, string? attribution = null)
        => AddTileSourceJson("raster-dem", sourceId, tileUrlTemplates, tileSize,
                             minZoom, maxZoom, encoding, attribution);

    // Tile-template sources go in as source-spec JSON: the typed C ABI entry points take a
    // TileJSON URL only, which is a different thing — and `attribution`/`encoding` live in
    // the TileJSON a template source does not have.
    private void AddTileSourceJson(string type, string sourceId, IEnumerable<string> tileUrlTemplates,
        int? tileSize, int minZoom, int maxZoom, string? encoding, string? attribution)
    {
        var spec = new Dictionary<string, object?>
        {
            ["type"]    = type,
            ["tiles"]   = tileUrlTemplates.ToArray(),
            ["minzoom"] = minZoom,
            ["maxzoom"] = maxZoom,
        };
        if (tileSize is int size)                    spec["tileSize"]    = size;
        if (!string.IsNullOrWhiteSpace(encoding))    spec["encoding"]    = encoding;
        if (!string.IsNullOrWhiteSpace(attribution)) spec["attribution"] = attribution;
        AddSourceJson(sourceId, JsonSerializer.Serialize(spec));
    }

    /// <summary>
    /// Add an image source with an explicit lat/lng quad defining the four corners.
    /// Corner order: top-right, top-left, bottom-right, bottom-left (matches MapLibre style spec).
    /// </summary>
    public MlnSource AddImageSource(string sourceId, string url,
        double lat0, double lon0, double lat1, double lon1,
        double lat2, double lon2, double lat3, double lon3)
        => new(NativeMethods.StyleAddImageSource(Handle, sourceId, url,
               lat0, lon0, lat1, lon1, lat2, lon2, lat3, lon3));

    public void RemoveSource(string sourceId)
        => NativeMethods.StyleRemoveSource(Handle, sourceId);

    // ── Layers ────────────────────────────────────────────────────────────────

    public bool HasLayer(string layerId)
        => NativeMethods.StyleHasLayer(Handle, layerId) != 0;

    public MlnLayer AddFillLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddFillLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddLineLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddLineLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddCircleLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddCircleLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddSymbolLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddSymbolLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddRasterLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddRasterLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddHeatmapLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddHeatmapLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddHillshadeLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddHillshadeLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddFillExtrusionLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddFillExtrusionLayer(Handle, layerId, sourceId, beforeLayerId));

    public MlnLayer AddBackgroundLayer(string layerId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddBackgroundLayer(Handle, layerId, beforeLayerId));

    public MlnLayer AddLocationIndicatorLayer(string layerId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddLocationIndicatorLayer(Handle, layerId, beforeLayerId));

    public MlnLayer AddColorReliefLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddColorReliefLayer(Handle, layerId, sourceId, beforeLayerId));

    public void RemoveLayer(string layerId)
        => NativeMethods.StyleRemoveLayer(Handle, layerId);

    // ── 3D terrain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables 3D terrain, draping the map over elevation from an existing
    /// raster-dem source (add it first with <see cref="AddRasterDemSource"/> or
    /// include it in the style JSON; it may be the same source a hillshade layer
    /// uses).
    /// </summary>
    /// <param name="sourceId">ID of a raster-dem source already in the style.</param>
    /// <param name="exaggeration">Vertical exaggeration multiplier (1.0 = true scale).</param>
    public void SetTerrain(string sourceId, float exaggeration = 1.0f)
        => NativeMethods.StyleSetTerrain(Handle, sourceId, exaggeration);

    /// <summary>Disables 3D terrain; the map renders flat again.</summary>
    public void RemoveTerrain()
        => NativeMethods.StyleRemoveTerrain(Handle);

    /// <summary>Whether 3D terrain is currently enabled.</summary>
    public bool IsTerrainEnabled
        => NativeMethods.StyleIsTerrainEnabled(Handle) != 0;

    // ── Attribution ──────────────────────────────────────────────────────

    /// <summary>
    /// Iterates every source in the loaded style and collects unique, non-empty
    /// attribution strings (from TileJSON metadata), in the order they appear.
    /// The result is suitable for building an OSM-compliant attribution overlay.
    /// Returns an empty array before a style is loaded.
    /// </summary>
    public IReadOnlyList<string> GetSourceAttributions()
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in GetSourceIds())
        {
            var srcPtr  = NativeMethods.StyleGetSource(Handle, id);
            if (srcPtr == IntPtr.Zero) continue;
            var attrPtr = NativeMethods.SourceGetAttribution(srcPtr);
            if (attrPtr == IntPtr.Zero) continue;
            var attr = Marshal.PtrToStringUTF8(attrPtr) ?? string.Empty;
            NativeMethods.FreeString(attrPtr);
            if (!string.IsNullOrWhiteSpace(attr) && seen.Add(attr))
                result.Add(attr);
        }
        return result;
    }

    /// <summary>
    /// HTML fragment linking to the MapLibre project. This library is built on
    /// MapLibre Native, so the project should always be credited — matching the
    /// behaviour of maplibre-gl-js, which always shows a "MapLibre" link.
    /// </summary>
    public const string MapLibreAttributionHtml =
        "<a href=\"https://maplibre.org/\" target=\"_blank\" rel=\"noopener nofollow\">MapLibre</a>";

    /// <summary>
    /// Returns <paramref name="parts"/> with a MapLibre attribution link prepended
    /// when none of the existing parts already reference MapLibre (by link or text).
    /// Used by every platform's attribution overlay so MapLibre is always credited.
    /// </summary>
    public static IReadOnlyList<string> EnsureMapLibreAttribution(IEnumerable<string> parts)
    {
        var list = new List<string>(parts);
        bool hasMapLibre = false;
        foreach (var p in list)
        {
            if (p is null) continue;
            if (p.IndexOf("maplibre", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasMapLibre = true;
                break;
            }
        }
        if (!hasMapLibre)
            list.Insert(0, MapLibreAttributionHtml);
        return list;
    }

    // ── Images ────────────────────────────────────────────────────────────────

    /// <summary>Add a sprite image. <paramref name="rgbaPremultiplied"/> must be
    /// width × height × 4 bytes of premultiplied RGBA.</summary>
    public unsafe void AddImage(string imageId, int width, int height,
                                 float pixelRatio, bool sdf,
                                 byte[] rgbaPremultiplied)
    {
        fixed (byte* p = rgbaPremultiplied)
            NativeMethods.StyleAddImage(Handle, imageId, width, height, pixelRatio, sdf ? 1 : 0, p);
    }

    public void RemoveImage(string imageId)
        => NativeMethods.StyleRemoveImage(Handle, imageId);

    // ── Style-level properties ──────────────────────────────────────────────────────────────

    /// <summary>Returns the currently loaded style as a JSON string.</summary>
    public string GetJson()
    {
        var ptr = NativeMethods.StyleGetJson(Handle);
        if (ptr == IntPtr.Zero) return string.Empty;
        var result = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Set the global transition duration and optional delay for all animated
    /// style property changes.</summary>
    public void SetTransition(long durationMs, long delayMs = 0)
        => NativeMethods.StyleSetTransition(Handle, durationMs, delayMs);

    /// <summary>Set a Light property by name using a JSON-encoded value.
    /// Valid names: <c>"anchor"</c> (<c>"map"</c>|<c>"viewport"</c>),
    /// <c>"color"</c> (hex string), <c>"intensity"</c> (0–1),
    /// <c>"position"</c> ([radial, azimuthal, polar]).</summary>
    public void SetLightProperty(string name, string valueJson)
        => NativeMethods.StyleSetLightProperty(Handle, name, valueJson);

    /// <summary>Set a Light property, serializing the value from a C# object.</summary>
    public void SetLightProperty(string name, object? value)
        => SetLightProperty(name, JsonSerializer.Serialize(value));

    // ── Style enumeration (Tier 1) ────────────────────────────────────────────

    /// <summary>Returns the URL from which the style was loaded, or empty string.</summary>
    public string GetUrl()
    {
        var ptr = NativeMethods.StyleGetUrl(Handle);
        if (ptr == IntPtr.Zero) return string.Empty;
        var result = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Returns the human-readable name of the loaded style, or empty string.</summary>
    public string GetName()
    {
        var ptr = NativeMethods.StyleGetName(Handle);
        if (ptr == IntPtr.Zero) return string.Empty;
        var result = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return result;
    }

    // The native side returns ID lists as JSON arrays: IDs may contain any
    // character (including newlines), so a delimiter-joined string would be
    // ambiguous.
    private static string[] ParseIdArray(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return [];
        var raw = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        if (raw.Length == 0) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Returns an array of all source IDs currently in the style.</summary>
    public string[] GetSourceIds()
        => ParseIdArray(NativeMethods.StyleGetSourceIds(Handle));

    /// <summary>Returns an array of all layer IDs in draw order.</summary>
    public string[] GetLayerIds()
        => ParseIdArray(NativeMethods.StyleGetLayerIds(Handle));

    /// <summary>Gets a layer handle by ID, or <c>null</c> if not found.</summary>
    public MlnLayer? GetLayer(string layerId)
    {
        var ptr = NativeMethods.StyleGetLayer(Handle, layerId);
        return ptr == IntPtr.Zero ? null : new MlnLayer(ptr);
    }

    /// <summary>Gets a source handle by ID, or <c>null</c> if not found.</summary>
    public MlnSource? GetSource(string sourceId)
    {
        var ptr = NativeMethods.StyleGetSource(Handle, sourceId);
        return ptr == IntPtr.Zero ? null : new MlnSource(ptr);
    }

    // ── Generic JSON add ───────────────────────────────────────────────────────

    /// <summary>
    /// Add a source from a raw MapLibre source-spec JSON object
    /// (the object value — not including the source ID key).
    /// Example: <c>AddSourceJson("my-source", "{\"type\":\"geojson\",\"data\":\"...\"}")</c>
    /// </summary>
    public void AddSourceJson(string sourceId, string sourceJson)
        => NativeMethods.StyleAddSourceJson(Handle, sourceId, sourceJson);

    /// <summary>
    /// Add a layer from a complete MapLibre layer-spec JSON object
    /// (must include "id" and "type" fields).
    /// Returns a non-owning <see cref="MlnLayer"/> handle, or <c>null</c> on error.
    /// </summary>
    public MlnLayer? AddLayerJson(string layerJson, string? beforeLayerId = null)
    {
        var ptr = NativeMethods.StyleAddLayerJson(Handle, layerJson, beforeLayerId);
        return ptr == IntPtr.Zero ? null : new MlnLayer(ptr);
    }
}

// ── Source handle ─────────────────────────────────────────────────────────────

/// <summary>Non-owning handle to a source inside a loaded style.</summary>
public sealed class MlnSource
{
    internal IntPtr Handle { get; }
    internal MlnSource(IntPtr handle) => Handle = handle;

    public void SetGeoJson(string geojson)
        => NativeMethods.GeoJsonSourceSetData(Handle, geojson);

    public void SetUrl(string url)
        => NativeMethods.GeoJsonSourceSetUrl(Handle, url);

    /// <summary>Returns the TileJSON attribution string for this source, or empty string if unavailable.</summary>
    public string GetAttribution()
    {
        var ptr = NativeMethods.SourceGetAttribution(Handle);
        if (ptr == IntPtr.Zero) return string.Empty;
        var result = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return result;
    }
}

// ── Layer handle ──────────────────────────────────────────────────────────────

/// <summary>Non-owning handle to a layer inside a loaded style.</summary>
public sealed class MlnLayer
{
    internal IntPtr Handle { get; }
    internal MlnLayer(IntPtr handle) => Handle = handle;

    public void SetSourceLayer(string sourceLayer)
        => NativeMethods.LayerSetSourceLayer(Handle, sourceLayer);

    public void SetFilter(string filterJson)
        => NativeMethods.LayerSetFilter(Handle, filterJson);

    public void SetMinZoom(float zoom) => NativeMethods.LayerSetMinZoom(Handle, zoom);
    public void SetMaxZoom(float zoom) => NativeMethods.LayerSetMaxZoom(Handle, zoom);

    public void SetVisible(bool visible)
        => NativeMethods.LayerSetVisibility(Handle, visible ? 1 : 0);

    /// <param name="valueJson">JSON-encoded value, e.g. <c>"\"#ff0000\""</c> or <c>"[\"get\",\"class\"]"</c></param>
    public void SetPaintProperty(string name, string valueJson)
        => NativeMethods.LayerSetPaintProperty(Handle, name, valueJson);

    /// <param name="valueJson">JSON-encoded value</param>
    public void SetLayoutProperty(string name, string valueJson)
        => NativeMethods.LayerSetLayoutProperty(Handle, name, valueJson);

    // Convenience: accept a C# object and serialize to JSON
    public void SetPaintProperty(string name, object? value)
        => SetPaintProperty(name, JsonSerializer.Serialize(value));

    public void SetLayoutProperty(string name, object? value)
        => SetLayoutProperty(name, JsonSerializer.Serialize(value));

    // ── Layer read-back (Tier 1) ──────────────────────────────────────────────

    /// <summary>Returns the JSON-encoded value of a paint property, or <c>null</c> if not set.</summary>
    public string? GetPaintProperty(string name)
    {
        var ptr = NativeMethods.LayerGetPaintProperty(Handle, name);
        if (ptr == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Returns the JSON-encoded value of a layout property, or <c>null</c> if not set.</summary>
    public string? GetLayoutProperty(string name)
    {
        var ptr = NativeMethods.LayerGetLayoutProperty(Handle, name);
        if (ptr == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Returns <c>true</c> if the layer is visible.</summary>
    public bool GetVisibility()
        => NativeMethods.LayerGetVisibility(Handle) != 0;
}
