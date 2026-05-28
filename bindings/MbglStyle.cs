/**
 * MbglStyle.cs — Typed wrapper around mbgl_style_t (non-owning, valid for the
 * lifetime of its parent MbglMap).
 */
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Maui.MapLibre.Native;

/// <summary>
/// Provides access to sources and layers.  This is a <em>non-owning</em> handle
/// — do not dispose it; it is invalidated when the parent <see cref="MbglMap"/> is disposed.
/// </summary>
public sealed class MbglStyle
{
    internal IntPtr Handle { get; }

    internal MbglStyle(IntPtr handle) => Handle = handle;

    // ── Sources ───────────────────────────────────────────────────────────────

    public bool HasSource(string sourceId)
        => NativeMethods.StyleHasSource(Handle, sourceId) != 0;

    public MbglSource AddGeoJsonSource(string sourceId)
        => new(NativeMethods.StyleAddGeoJsonSource(Handle, sourceId));

    public MbglSource AddGeoJsonSourceUrl(string sourceId, string url)
        => new(NativeMethods.StyleAddGeoJsonSourceUrl(Handle, sourceId, url));

    public MbglSource AddVectorSource(string sourceId, string url)
        => new(NativeMethods.StyleAddVectorSource(Handle, sourceId, url));

    public MbglSource AddRasterSource(string sourceId, string url, int tileSize = 512)
        => new(NativeMethods.StyleAddRasterSource(Handle, sourceId, url, tileSize));

    public MbglSource AddRasterDemSource(string sourceId, string url, int tileSize = 512)
        => new(NativeMethods.StyleAddRasterDemSource(Handle, sourceId, url, tileSize));

    public void RemoveSource(string sourceId)
        => NativeMethods.StyleRemoveSource(Handle, sourceId);

    // ── Layers ────────────────────────────────────────────────────────────────

    public bool HasLayer(string layerId)
        => NativeMethods.StyleHasLayer(Handle, layerId) != 0;

    public MbglLayer AddFillLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddFillLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddLineLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddLineLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddCircleLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddCircleLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddSymbolLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddSymbolLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddRasterLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddRasterLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddHeatmapLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddHeatmapLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddHillshadeLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddHillshadeLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddFillExtrusionLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddFillExtrusionLayer(Handle, layerId, sourceId, beforeLayerId));

    public MbglLayer AddBackgroundLayer(string layerId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddBackgroundLayer(Handle, layerId, beforeLayerId));

    public MbglLayer AddLocationIndicatorLayer(string layerId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddLocationIndicatorLayer(Handle, layerId, beforeLayerId));

    public MbglLayer AddColorReliefLayer(string layerId, string sourceId, string? beforeLayerId = null)
        => new(NativeMethods.StyleAddColorReliefLayer(Handle, layerId, sourceId, beforeLayerId));

    public void RemoveLayer(string layerId)
        => NativeMethods.StyleRemoveLayer(Handle, layerId);

    // ── Attribution ──────────────────────────────────────────────────────

    /// <summary>
    /// Iterates every source in the loaded style and collects unique, non-empty
    /// attribution strings (from TileJSON metadata), in the order they appear.
    /// The result is suitable for building an OSM-compliant attribution overlay.
    /// Returns an empty array before a style is loaded.
    /// </summary>
    public IReadOnlyList<string> GetSourceAttributions()
    {
        var idsPtr = NativeMethods.StyleGetSourceIds(Handle);
        if (idsPtr == IntPtr.Zero) return [];
        var raw = Marshal.PtrToStringUTF8(idsPtr) ?? string.Empty;
        NativeMethods.FreeString(idsPtr);

        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

    /// <summary>Returns an array of all source IDs currently in the style.</summary>
    public string[] GetSourceIds()
    {
        var ptr = NativeMethods.StyleGetSourceIds(Handle);
        if (ptr == IntPtr.Zero) return [];
        var raw = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return raw.Length == 0 ? [] : raw.Split('\n');
    }

    /// <summary>Returns an array of all layer IDs in draw order.</summary>
    public string[] GetLayerIds()
    {
        var ptr = NativeMethods.StyleGetLayerIds(Handle);
        if (ptr == IntPtr.Zero) return [];
        var raw = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        NativeMethods.FreeString(ptr);
        return raw.Length == 0 ? [] : raw.Split('\n');
    }

    /// <summary>Gets a layer handle by ID, or <c>null</c> if not found.</summary>
    public MbglLayer? GetLayer(string layerId)
    {
        var ptr = NativeMethods.StyleGetLayer(Handle, layerId);
        return ptr == IntPtr.Zero ? null : new MbglLayer(ptr);
    }

    /// <summary>Gets a source handle by ID, or <c>null</c> if not found.</summary>
    public MbglSource? GetSource(string sourceId)
    {
        var ptr = NativeMethods.StyleGetSource(Handle, sourceId);
        return ptr == IntPtr.Zero ? null : new MbglSource(ptr);
    }
}

// ── Source handle ─────────────────────────────────────────────────────────────

/// <summary>Non-owning handle to a source inside a loaded style.</summary>
public sealed class MbglSource
{
    internal IntPtr Handle { get; }
    internal MbglSource(IntPtr handle) => Handle = handle;

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
public sealed class MbglLayer
{
    internal IntPtr Handle { get; }
    internal MbglLayer(IntPtr handle) => Handle = handle;

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
