#if ANDROID
using Android.Views;
using Android.Widget;
using Android.Runtime;
using Android.Text;
using Android.Text.Method;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers.Geometry;
using Map    = MapLibreNative.Maui.Handlers.Maps.Map;
using Style  = MapLibreNative.Maui.Handlers.Maps.Style;
using Location = Microsoft.Maui.Devices.Sensors.Location;

namespace MapLibreNative.Maui.Handlers;

/// <summary>
/// Android IMapLibreMapController backed by mln-cabi.so via EGL + ANativeWindow.
/// The platform view is a SurfaceView; the C++ EGL frontend manages its own
/// EGL display, config, context, and window surface.
/// </summary>
public class MapLibreMapController : IMapLibreMapController
{
    // -- HTTP provider (shared across all map instances) -----------------------

    private static readonly HttpClient s_http = new(new HttpClientHandler
    {
        AllowAutoRedirect       = true,
        MaxAutomaticRedirections = 10,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "MapLibreNative/1.0 (.NET MAUI Android)" } },
    };

    // Keep the delegate alive for the lifetime of the process so the GC
    // never collects it while the native side is still pointing to it.
    private static readonly NativeMethods.HttpProviderDelegate s_httpProvider = OnHttpRequest;

    // Tracks in-flight CancellationTokenSources keyed by request_id so we can
    // abort the HttpClient request when the native side cancels it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource>
        s_pendingRequests = new();

    private static bool s_httpProviderRegistered;

    private static void EnsureHttpProviderRegistered()
    {
        if (s_httpProviderRegistered) return;
        s_httpProviderRegistered = true;
        NativeMethods.SetHttpProvider(s_httpProvider, IntPtr.Zero);
    }

    private static void OnHttpRequest(ulong requestId, IntPtr urlPtr, IntPtr etagPtr,
                                       IntPtr modifiedPtr, long rangeStart, long rangeEnd,
                                       IntPtr userdata)
    {
        string url      = Marshal.PtrToStringUTF8(urlPtr)  ?? string.Empty;
        string? etag    = Marshal.PtrToStringUTF8(etagPtr);
        string? modified = Marshal.PtrToStringUTF8(modifiedPtr);

        var cts = new CancellationTokenSource();
        s_pendingRequests[requestId] = cts;

        // Fire-and-forget; errors are delivered via mbgl_http_respond.
        _ = FetchAsync(requestId, url, etag, modified, rangeStart, rangeEnd, cts.Token);
    }

    private static async Task FetchAsync(ulong requestId, string url,
                                          string? etag, string? modified,
                                          long rangeStart, long rangeEnd,
                                          CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            // Conditional GET headers
            if (!string.IsNullOrEmpty(etag))
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{etag}\"", true));
            else if (!string.IsNullOrEmpty(modified))
                req.Headers.IfModifiedSince = DateTimeOffset.TryParse(modified, out var dt) ? dt : null;

            // Range request (used by PMTiles and other partial-content sources)
            if (rangeStart >= 0 && rangeEnd >= rangeStart)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart, rangeEnd);

            HttpResponseMessage resp;
            try
            {
                resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                   .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Request was cancelled from the native side — no response needed.
                s_pendingRequests.TryRemove(requestId, out _);
                return;
            }
            catch (Exception ex)
            {
                s_pendingRequests.TryRemove(requestId, out _);
                RespondError(requestId, NativeMethods.MbglHttpError.Connection, ex.Message);
                return;
            }

            s_pendingRequests.TryRemove(requestId, out _);

            int status = (int)resp.StatusCode;

            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                RespondNotModified(requestId);
                return;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent ||
                (status == 404 && url.Contains("/tiles/")))
            {
                RespondNoContent(requestId);
                return;
            }

            if (status == 404)
            {
                RespondError(requestId, NativeMethods.MbglHttpError.NotFound, "HTTP 404");
                return;
            }

            if (status == 429)
            {
                RespondError(requestId, NativeMethods.MbglHttpError.RateLimit, "HTTP 429");
                return;
            }

            if (status >= 500 && status < 600)
            {
                RespondError(requestId, NativeMethods.MbglHttpError.Server, $"HTTP {status}");
                return;
            }

            // 200 OK and 206 Partial Content are both treated as success.
            if (status != 200 && status != 206)
            {
                RespondError(requestId, NativeMethods.MbglHttpError.Other, $"HTTP {status}");
                return;
            }

            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            string? respEtag     = resp.Headers.ETag?.Tag?.Trim('"');
            string? respModified = resp.Content.Headers.LastModified?.ToString("R");
            string? respExpires  = resp.Content.Headers.Expires?.ToString("R");
            string? cacheControl = resp.Headers.CacheControl?.ToString();
            int     mustReval    = resp.Headers.CacheControl?.MustRevalidate == true ? 1 : 0;

            RespondSuccess(requestId, body, respEtag, respModified, respExpires,
                           cacheControl, mustReval);
        }
        catch (OperationCanceledException)
        {
            s_pendingRequests.TryRemove(requestId, out _);
        }
        catch (Exception ex)
        {
            s_pendingRequests.TryRemove(requestId, out _);
            RespondError(requestId, NativeMethods.MbglHttpError.Connection, ex.Message);
        }
    }

    private static void RespondSuccess(ulong requestId, byte[] body,
                                       string? etag, string? modified,
                                       string? expires, string? cacheControl,
                                       int mustReval)
    {
        var etagBytes    = ToNullTerminatedUtf8(etag);
        var modBytes     = ToNullTerminatedUtf8(modified);
        var expiresBytes = ToNullTerminatedUtf8(expires);
        var ccBytes      = ToNullTerminatedUtf8(cacheControl);

        // Use a dummy single-byte array when body is empty so fixed() gives a
        // valid (non-null) pointer — the C++ side checks data_len so the byte
        // itself is never read.
        byte[] safeBody = body.Length > 0 ? body : new byte[1];

        unsafe
        {
            fixed (byte* bodyPtr = safeBody)
            fixed (byte* e = etagBytes)
            fixed (byte* m = modBytes)
            fixed (byte* x = expiresBytes)
            fixed (byte* c = ccBytes)
            {
                NativeMethods.HttpRespond(
                    requestId,
                    NativeMethods.MbglHttpError.None,
                    IntPtr.Zero,
                    200,
                    (nint)bodyPtr,
                    body.Length,
                    e is null ? IntPtr.Zero : (IntPtr)e,
                    m is null ? IntPtr.Zero : (IntPtr)m,
                    x is null ? IntPtr.Zero : (IntPtr)x,
                    c is null ? IntPtr.Zero : (IntPtr)c,
                    0, 0, mustReval);
            }
        }
    }

    private static byte[]? ToNullTerminatedUtf8(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int len = System.Text.Encoding.UTF8.GetByteCount(s);
        var buf = new byte[len + 1]; // +1 for null terminator (already zero-initialized)
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, buf, 0);
        return buf;
    }

    private static void RespondNotModified(ulong requestId)
    {
        NativeMethods.HttpRespond(requestId, NativeMethods.MbglHttpError.None,
            IntPtr.Zero, 304, IntPtr.Zero, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            0, 1, 0);
    }

    private static void RespondNoContent(ulong requestId)
    {
        NativeMethods.HttpRespond(requestId, NativeMethods.MbglHttpError.None,
            IntPtr.Zero, 204, IntPtr.Zero, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            1, 0, 0);
    }

    private static void RespondError(ulong requestId, NativeMethods.MbglHttpError error, string message)
    {
        var msgBytes = ToNullTerminatedUtf8(message);
        unsafe
        {
            fixed (byte* msg = msgBytes)
            {
                NativeMethods.HttpRespond(requestId, error,
                    msg is null ? IntPtr.Zero : (IntPtr)msg,
                    0, IntPtr.Zero, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, 0, 0);
            }
        }
    }

    // -- Layout property name set (same as Windows controller) ----------------

    private static readonly HashSet<string> LayoutPropertyNames = new(StringComparer.Ordinal)
    {
        "visibility",
        "symbol-placement","symbol-spacing","symbol-avoid-edges","symbol-sort-key","symbol-z-order",
        "icon-allow-overlap","icon-ignore-placement","icon-optional","icon-rotation-alignment",
        "icon-size","icon-text-fit","icon-text-fit-padding","icon-image","icon-rotate",
        "icon-padding","icon-keep-upright","icon-offset","icon-anchor","icon-pitch-alignment",
        "text-pitch-alignment","text-rotation-alignment","text-field","text-font","text-size",
        "text-max-width","text-line-height","text-letter-spacing","text-justify",
        "text-radial-offset","text-variable-anchor","text-anchor","text-max-angle",
        "text-writing-mode","text-rotate","text-padding","text-keep-upright","text-transform",
        "text-offset","text-allow-overlap","text-ignore-placement","text-optional",
        "line-cap","line-join","line-miter-limit","line-round-limit","line-sort-key",
        "fill-sort-key","circle-sort-key",
    };

    // -- State -----------------------------------------------------------------

    private readonly string? _styleString;
    private readonly float   _pixelRatio;

    private IntPtr        _nativeWindow = IntPtr.Zero;
    private MbglRunLoop?  _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap?      _map;
    private MbglStyle?    _style;
    private bool          _styleReady;

    private readonly SurfaceCallback _surfaceCb;
    private SurfaceView     _surfaceView      = null!;
    private TextView        _attrView         = null!;  // expanded full text
    private TextView        _attrButton       = null!;  // collapsed ⓘ button
    private bool            _showAttrControl  = true;
    private string?         _customAttribution;
    private int             _attrCollapseGen;            // generation counter for auto-collapse timer
    private bool            _attrLoaded;                 // true once attribution content has been fetched

    public FrameLayout View { get; private set; } = null!;

    // -- Events ----------------------------------------------------------------

    public event Action<Map>?                OnMapReadyReceived;
    public event Action?                     OnDidBecomeIdleReceived;
    public event Action<int>?                OnCameraMoveStartedReceived;
    public event Action?                     OnCameraMoveReceived;
    public event Action?                     OnCameraIdleReceived;
    public event Action<int>?                OnCameraTrackingChangedReceived;
    public event Action?                     OnCameraTrackingDismissedReceived;
    public event Func<LatLng, bool>?         OnMapClickReceived;
    public event Func<LatLng, bool>?         OnMapLongClickReceived;
    public event Action<Style>?              OnStyleLoadedReceived;
    public event Action<Location>?           OnUserLocationUpdateReceived;
    public event Action<string>?             OnDidFailLoadingMapReceived;
    public event Action<string>?             OnStyleImageMissingReceived;

    // -- Construction ----------------------------------------------------------

    public MapLibreMapController(float pixelRatio, string? styleString)
    {
        _pixelRatio  = pixelRatio;
        _styleString = styleString;

        EnsureHttpProviderRegistered();

        var ctx = Android.App.Application.Context!;

        _surfaceView = new SurfaceView(ctx);
        _surfaceCb = new SurfaceCallback
        {
            Created   = OnSurfaceCreated,
            Changed   = OnSurfaceChanged,
            Destroyed = _ => DisposeNative(),
        };
        _surfaceView.Holder!.AddCallback(_surfaceCb);

        // Attribution overlay (bottom-right corner, OSM licence compliance)
        _attrView = new TextView(ctx);
        _attrView.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        _attrView.SetTextColor(Android.Graphics.Color.Argb(220, 50, 50, 50));
        _attrView.SetBackgroundColor(Android.Graphics.Color.Argb(180, 255, 255, 255));
        _attrView.SetPadding(8, 4, 8, 4);
        _attrView.MovementMethod = LinkMovementMethod.Instance;
        _attrView.Visibility = ViewStates.Gone;

        var container = new FrameLayout(ctx);
        container.AddView(_surfaceView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        var attrParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom | GravityFlags.End);
        attrParams.SetMargins(0, 0, 8, 8);
        container.AddView(_attrView, attrParams);

        // Collapsed ⓘ button — same corner, shown when full text is hidden
        _attrButton = new TextView(ctx);
        _attrButton.Text = "ⓘ";
        _attrButton.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        _attrButton.SetTextColor(Android.Graphics.Color.Argb(220, 50, 50, 50));
        _attrButton.SetBackgroundColor(Android.Graphics.Color.Argb(180, 255, 255, 255));
        _attrButton.SetPadding(8, 4, 8, 4);
        _attrButton.Visibility = ViewStates.Gone;
        _attrButton.Click += (_, _) => ExpandAttribution();
        var btnParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Bottom | GravityFlags.End);
        btnParams.SetMargins(0, 0, 8, 8);
        container.AddView(_attrButton, btnParams);

        View = container;
    }

    // -- Surface lifecycle -----------------------------------------------------

    private void OnSurfaceCreated(ISurfaceHolder holder)
    {
        var surface = holder.Surface!;
        _nativeWindow = NativeMethods.AndroidAcquireWindow(JNIEnv.Handle, surface.Handle);

        int w = Math.Max(1, _surfaceView.Width);
        int h = Math.Max(1, _surfaceView.Height);
        InitMaplibre(w, h);
    }

    private void OnSurfaceChanged(ISurfaceHolder holder,
        global::Android.Graphics.Format fmt, int w, int h)
    {
        _frontend?.SetSize(w, h);
        _map?.SetSize(w, h);
        _map?.TriggerRepaint();
    }

    // -- MapLibre init ---------------------------------------------------------

    private void InitMaplibre(int w, int h)
    {
        _runLoop  = new MbglRunLoop();
        _frontend = new MbglFrontend(_nativeWindow, IntPtr.Zero, w, h, _pixelRatio, OnRender);
        _map      = new MbglMap(_frontend, _runLoop,
                                pixelRatio: _pixelRatio,
                                observer: OnMapObserverEvent);
        _map.SetSize(w, h);

        if (!string.IsNullOrEmpty(_styleString))
        {
            if (_styleString!.StartsWith('{')) _map.SetStyleJson(_styleString);
            else                               _map.SetStyleUrl(_styleString);
        }

        OnMapReadyReceived?.Invoke(new Map(null));
    }

    private void OnRender()
    {
        _frontend?.Render();
        _runLoop?.RunOnce();
    }

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                _styleReady = true;
                _style = _map?.GetStyle();
                _attrLoaded = false;  // new style — sources may have different attribution
                RefreshAttribution();
                OnStyleLoadedReceived?.Invoke(new Style(null));
                break;
            case "onDidBecomeIdle":
                // TileJSON sources may finish loading after onDidFinishLoadingStyle;
                // only retry while we still have no content.
                if (!_attrLoaded) RefreshAttribution();
                OnDidBecomeIdleReceived?.Invoke();
                break;
            case "onCameraIsChanging":
                CollapseAttribution();
                OnCameraMoveReceived?.Invoke();
                break;
            case "onCameraDidChange":
                OnCameraIdleReceived?.Invoke();
                break;
            case "onDidFailLoadingMap":
                OnDidFailLoadingMapReceived?.Invoke(detail ?? string.Empty);
                break;
            case "onStyleImageMissing":
                OnStyleImageMissingReceived?.Invoke(detail ?? string.Empty);
                break;
        }
    }

    // -- IMapLibreMapOptionsSink -----------------------------------------------

    public void SetStyleString(string styleString)
    {
        if (_map == null) return;
        if (styleString.StartsWith('{')) _map.SetStyleJson(styleString);
        else                             _map.SetStyleUrl(styleString);
    }

    public void SetMinMaxZoomPreference(double? min, double? max)
    {
        if (min.HasValue) _map?.SetMinZoom(min.Value);
        if (max.HasValue) _map?.SetMaxZoom(max.Value);
    }

    public void SetCameraTargetBounds(LatLngBounds bounds,
        double minZoom = double.NaN, double maxZoom = double.NaN,
        double minPitch = double.NaN, double maxPitch = double.NaN)
    {
        _map?.SetBounds(bounds.SouthWest.Latitude, bounds.SouthWest.Longitude,
                        bounds.NorthEast.Latitude, bounds.NorthEast.Longitude,
                        minZoom, maxZoom, minPitch, maxPitch);
    }
    public void SetCompassEnabled(bool v)                     { }
    public void SetRotateGesturesEnabled(bool v)              { }
    public void SetScrollGesturesEnabled(bool v)              { }
    public void SetTiltGesturesEnabled(bool v)                { }
    public void SetTrackCameraPosition(bool v)                { }
    public void SetZoomGesturesEnabled(bool v)                { }
    public void SetMyLocationEnabled(bool v)                  { }
    public void SetMyLocationTrackingMode(int v)              { }
    public void SetMyLocationRenderMode(int v)                { }
    public void SetLogoViewMargins(int x, int y)              { }
    public void SetCompassGravity(int gravity)                { }
    public void SetCompassViewMargins(int x, int y)           { }
    public void SetAttributionButtonGravity(int v)            { }
    public void SetAttributionButtonMargins(int x, int y)     { }
    public void SetShowNavigationControls(bool show)          { }
    public void SetShowAttributionControl(bool show, string? customAttribution)
    {
        _showAttrControl   = show;
        _customAttribution = customAttribution;
        RefreshAttribution();
    }

    // -- Attribution -----------------------------------------------------------

    private void RefreshAttribution()
    {
        if (_style == null)
        {
            _attrView.Visibility   = ViewStates.Gone;
            _attrButton.Visibility = ViewStates.Gone;
            return;
        }

        var parts = new System.Collections.Generic.List<string>(_style.GetSourceAttributions());
        if (!string.IsNullOrWhiteSpace(_customAttribution))
            parts.Add(_customAttribution!);

        if (parts.Count == 0 || !_showAttrControl)
        {
            _attrView.Visibility   = ViewStates.Gone;
            _attrButton.Visibility = ViewStates.Gone;
            return;
        }

        _attrLoaded = true;
        _attrView.TextFormatted = BuildAttributionSpanned(parts);
        ExpandAttribution();
    }

    private void ExpandAttribution()
    {
        if (!_showAttrControl || !_attrLoaded) return;
        _attrView.Visibility   = ViewStates.Visible;
        _attrButton.Visibility = ViewStates.Gone;
        ScheduleAutoCollapse();
    }

    private void CollapseAttribution()
    {
        // If neither view is showing, there is nothing to collapse.
        if (_attrView.Visibility   == ViewStates.Gone &&
            _attrButton.Visibility == ViewStates.Gone) return;
        ++_attrCollapseGen;  // cancel any pending auto-collapse
        _attrView.Visibility   = ViewStates.Gone;
        _attrButton.Visibility = (_attrLoaded && _showAttrControl)
            ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ScheduleAutoCollapse()
    {
        int gen = ++_attrCollapseGen;
        // PostDelayed runs on the view's UI thread; generation counter prevents
        // stale callbacks from firing after Expand was called again.
        _attrView.PostDelayed(() =>
        {
            if (_attrCollapseGen == gen) CollapseAttribution();
        }, 5000);
    }

    private static ISpanned BuildAttributionSpanned(
        System.Collections.Generic.List<string> parts)
    {
        // Parse each part's HTML <a href> tags into URLSpans so links are clickable.
        var sb  = new SpannableStringBuilder();
        var hrefRe = new Regex(
            @"<a\b[^>]*?href=[""']?([^""'\s>]+)[""']?[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        bool first = true;
        foreach (var part in parts)
        {
            if (!first) sb.Append(" | ");
            first = false;

            int pos = 0;
            foreach (Match m in hrefRe.Matches(part))
            {
                // Plain text before this <a>
                if (m.Index > pos)
                    sb.Append(DecodeHtmlEntities(StripHtmlTags(part[pos..m.Index])));

                // Link text with URLSpan
                string href     = m.Groups[1].Value;
                string linkText = DecodeHtmlEntities(StripHtmlTags(m.Groups[2].Value));
                int start = sb.Length();
                sb.Append(linkText);
                if (Uri.TryCreate(href, UriKind.Absolute, out _))
                    sb.SetSpan(new Android.Text.Style.URLSpan(href),
                               start, sb.Length(),
                               SpanTypes.InclusiveInclusive);

                pos = m.Index + m.Length;
            }
            // Remaining text
            if (pos < part.Length)
                sb.Append(DecodeHtmlEntities(StripHtmlTags(part[pos..])));
        }
        return sb;
    }

    private static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var sb2   = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if      (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag)   sb2.Append(c);
        }
        return sb2.ToString().Trim();
    }

    private static string DecodeHtmlEntities(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&')) return text;
        return text
            .Replace("&amp;",   "&")
            .Replace("&lt;",    "<")
            .Replace("&gt;",    ">")
            .Replace("&quot;",  "\"")
            .Replace("&#39;",   "'")
            .Replace("&nbsp;",  "\u00A0")
            .Replace("&copy;",  "\u00A9")
            .Replace("&reg;",   "\u00AE")
            .Replace("&trade;", "\u2122");
    }

    // -- Sources ---------------------------------------------------------------

    public void AddGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        if (_style.HasSource(sourceName)) return;
        var s = _style.AddGeoJsonSource(sourceName);
        s.SetGeoJson(source);
    }

    public void SetGeoJsonSource(string sourceName, string source)
        => AddGeoJsonSource(sourceName, source);

    public void SetGeoJsonFeature(string sourceName, string geojsonFeature)
    {
        if (!_styleReady || _style == null) return;
        if (_style.HasSource(sourceName)) _style.RemoveSource(sourceName);
        AddGeoJsonSource(sourceName, geojsonFeature);
    }

    public void AddRasterSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddRasterSource(sourceName, url, tileSize);
    }

    public void AddRasterDemSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddRasterDemSource(sourceName, url, tileSize);
    }

    public void AddVectorSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddVectorSource(sourceName, url);
    }

    public void AddImageSource(string sourceName, string url, LatLngQuad? coordinates)
    {
        if (!_styleReady || _style == null) return;
        _style.AddRasterSource(sourceName, url);
    }

    public void RemoveSource(string sourceId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveSource(sourceId);
    }

    // -- Layers ----------------------------------------------------------------

    public void AddFillLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddFillLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddLineLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddLineLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddCircleLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddCircleLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddSymbolLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddSymbolLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddRasterLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddRasterLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddHillshadeLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddHillshadeLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddFillExtrusionLayer(string layerName, string sourceName,
        string? belowLayerId, string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddFillExtrusionLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddHeatmapLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddHeatmapLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void RemoveLayer(string layerId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveLayer(layerId);
    }

    // -- Helpers ---------------------------------------------------------------

    private static void ApplyLayerMeta(MbglLayer layer, string? sourceLayer,
        float minZoom, float maxZoom)
    {
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
    }

    private void ApplyProperties(MbglLayer layer, IDictionary<string, object?> props)
    {
        foreach (var (k, v) in props)
        {
            var json = JsonSerializer.Serialize(v);
            if (LayoutPropertyNames.Contains(k))
                layer.SetLayoutProperty(k, json);
            else
                layer.SetPaintProperty(k, json);
        }
    }

    // -- Camera ----------------------------------------------------------------

    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0)
        => _map?.JumpTo(latitude, longitude, zoom, bearing, pitch);

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 300)
        => _map?.EaseTo(latitude, longitude, zoom, bearing, pitch, durationMs);

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 500)
        => _map?.FlyTo(latitude, longitude, zoom, bearing, pitch, durationMs);

    public void CancelTransitions() => _map?.CancelTransitions();

    public double GetZoom()    => _map?.Zoom    ?? 0;
    public double GetBearing() => _map?.Bearing ?? 0;
    public double GetPitch()   => _map?.Pitch   ?? 0;
    public LatLng GetCenter()
    {
        if (_map == null) return new LatLng(0, 0);
        var (lat, lon) = _map.Center;
        return new LatLng(lat, lon);
    }

    public (double X, double Y) LatLngToScreenPoint(double latitude, double longitude)
        => _map?.PixelForLatLng(latitude, longitude) ?? (0, 0);

    public LatLng ScreenPointToLatLng(double x, double y)
    {
        if (_map == null) return new LatLng(0, 0);
        var (lat, lon) = _map.LatLngForPixel(x, y);
        return new LatLng(lat, lon);
    }

    public string? QueryRenderedFeaturesAtPoint(double x, double y, string? layerIds = null)
        => _map?.QueryRenderedFeaturesAtPoint(x, y, layerIds);

    public string? QueryRenderedFeaturesInBox(double x1, double y1, double x2, double y2,
        string? layerIds = null)
        => _map?.QueryRenderedFeaturesInBox(x1, y1, x2, y2, layerIds);

    // -- Tier 1 – gesture / interactive movement ───────────────────────────────
    public void SetGestureInProgress(bool inProgress) => _map?.SetGestureInProgress(inProgress);
    public void MoveBy(double dx, double dy, long durationMs = 0) => _map?.MoveBy(dx, dy, durationMs);
    public void RotateBy(double x0, double y0, double x1, double y1) => _map?.RotateBy(x0, y0, x1, y1);
    public void PitchBy(double deltaDegrees, long durationMs = 0) => _map?.PitchBy(deltaDegrees, durationMs);

    // -- Tier 1 – map option setters ───────────────────────────────────────────
    public void SetNorthOrientation(int orientation) => _map?.SetNorthOrientation(orientation);
    public void SetConstrainMode(int mode) => _map?.SetConstrainMode(mode);
    public void SetViewportMode(int mode) => _map?.SetViewportMode(mode);

    // -- Tier 1 – bounds read-back ─────────────────────────────────────────────
    public BoundOptions GetBounds() => _map?.GetBounds() ?? default;

    // -- Tier 2 – tile LOD / prefetch ─────────────────────────────────────────
    public void SetPrefetchZoomDelta(int delta) => _map?.SetPrefetchZoomDelta(delta);
    public int  GetPrefetchZoomDelta() => _map?.GetPrefetchZoomDelta() ?? 4;
    public void SetTileLodMinRadius(double radius) => _map?.SetTileLodMinRadius(radius);
    public void SetTileLodScale(double scale) => _map?.SetTileLodScale(scale);
    public void SetTileLodPitchThreshold(double thresholdRadians) => _map?.SetTileLodPitchThreshold(thresholdRadians);
    public void SetTileLodZoomShift(double shift) => _map?.SetTileLodZoomShift(shift);
    public void SetTileLodMode(int mode) => _map?.SetTileLodMode(mode);

    // -- Tier 2 – camera / batch projection ───────────────────────────────────
    public CameraResult CameraForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points,
        double padTop = 0, double padLeft = 0, double padBottom = 0, double padRight = 0)
        => _map?.CameraForLatLngs(points, padTop, padLeft, padBottom, padRight) ?? default;

    public (double X, double Y)[] PixelsForLatLngs(IReadOnlyList<(double Lat, double Lon)> points)
        => _map?.PixelsForLatLngs(points) ?? [];

    public (double Lat, double Lon)[] LatLngsForPixels(IReadOnlyList<(double X, double Y)> pixels)
        => _map?.LatLngsForPixels(pixels) ?? [];

    // ── Debug overlays ────────────────────────────────────────────────────────────

    public int  GetDebugOptions() => _map?.GetDebugOptions() ?? 0;
    public void SetDebugOptions(int options) => _map?.SetDebugOptions(options);

    // ── Style inspection ───────────────────────────────────────────────────

    public string   GetStyleUrl()       => _style?.GetUrl()       ?? string.Empty;
    public string[] GetStyleSourceIds() => _style?.GetSourceIds() ?? [];
    public string[] GetStyleLayerIds()  => _style?.GetLayerIds()  ?? [];

    // ── Layer read-back + visibility ──────────────────────────────────────────

    public string? GetLayerPaintProperty(string layerId, string name)
        => _style?.GetLayer(layerId)?.GetPaintProperty(name);

    public string? GetLayerLayoutProperty(string layerId, string name)
        => _style?.GetLayer(layerId)?.GetLayoutProperty(name);

    public bool GetLayerVisibility(string layerId)
        => _style?.GetLayer(layerId)?.GetVisibility() ?? false;

    public void SetLayerVisibility(string layerId, bool visible)
        => _style?.GetLayer(layerId)?.SetVisible(visible);

    // ── Location indicator (no-op on Android — platform uses its own blue-dot) ──
    public bool FollowLocation { get; set; } = true;
    public bool ShowBearing    { get; set; } = true;
    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10) { }
    public void ClearLocationIndicator() { }

    // -- Cleanup ---------------------------------------------------------------

    private void DisposeNative()
    {
        _style    = null;
        _map?.Dispose();      _map      = null;
        // Drain pending libuv tasks scheduled by Map destruction.
        for (int i = 0; i < 8 && _runLoop != null; i++) _runLoop.RunOnce();
        // mbgl_map_create transfers ownership of the frontend pointer to the
        // native CabiMap; mbgl_map_destroy already destroyed it. Do not call
        // Dispose() on _frontend — it is a no-op after TransferOwnership() but
        // we null it here explicitly to avoid confusion.
        _frontend = null;
        _runLoop?.Dispose();  _runLoop  = null;

        if (_nativeWindow != IntPtr.Zero)
        {
            NativeMethods.AndroidReleaseWindow(_nativeWindow);
            _nativeWindow = IntPtr.Zero;
        }
        _styleReady = false;
    }
}

// -- Surface callback helper ---------------------------------------------------

internal sealed class SurfaceCallback : Java.Lang.Object, ISurfaceHolderCallback
{
    public Action<ISurfaceHolder>? Created;
    public Action<ISurfaceHolder, global::Android.Graphics.Format, int, int>? Changed;
    public Action<ISurfaceHolder>? Destroyed;

    public void SurfaceCreated(ISurfaceHolder holder)
        => Created?.Invoke(holder);

    public void SurfaceChanged(ISurfaceHolder holder,
        global::Android.Graphics.Format format, int width, int height)
        => Changed?.Invoke(holder, format, width, height);

    public void SurfaceDestroyed(ISurfaceHolder holder)
        => Destroyed?.Invoke(holder);
}
#endif
