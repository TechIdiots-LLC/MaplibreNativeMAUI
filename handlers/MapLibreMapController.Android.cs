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
/// The platform view is a TextureView; the C++ EGL frontend manages its own
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

    private TextureView            _textureView    = null!;
    private Android.Views.Surface? _textureSurface;
    private TextView        _attrView         = null!;  // expanded full text
    private TextView        _attrButton       = null!;  // collapsed ⓘ button
    private bool            _showAttrControl  = true;
    private string?         _customAttribution;
    private int             _attrCollapseGen;            // generation counter for auto-collapse timer
    private bool            _attrLoaded;                 // true once attribution content has been fetched

    // -- Navigation + GPS overlay controls -------------------------------------

    // dp sizing (converted to px via display density at layout time)
    private const int OverlayBtnDp   = 40;
    private const int OverlayMarginDp = 8;
    private const int OverlayGapDp    = 8;

    private LinearLayout _navPanel        = null!;  // d-pad / zoom-in / zoom-out
    private LinearLayout _gpsPanel        = null!;  // tracking / bearing-reset
    private Android.Views.View _navNorthTick = null!;  // rotates with map bearing
    private TextView     _gpsTrackingIcon = null!;  // reflects tracking mode
    private TextView     _gpsBearingIcon  = null!;

    private bool _showNavControls = true;
    private bool _showGpsControl  = true;

    private MapControlCorner _navCorner  = MapControlCorner.TopRight;
    private MapControlCorner _gpsCorner  = MapControlCorner.TopRight;
    private MapControlCorner _attrCorner = MapControlCorner.BottomLeft;

    // GPS tracking state (fixes are fed externally via UpdateGpsLocation)
    private enum GpsTrackingMode { Off, Show, Follow, FollowBearing }
    private GpsTrackingMode _gpsMode = GpsTrackingMode.Off;
    private double _lastGpsLat, _lastGpsLon;
    private float  _lastGpsBearing;
    private float  _lastGpsAccuracy = 10f;
    private bool   _hasGpsFix;

    // Location indicator puck (portable style layer, shared with Windows impl)
    private const string LocIndLayerId = "__mln_location_indicator";
    private MbglLayer? _locIndLayer;
    private readonly record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    // -- Gesture state ---------------------------------------------------------

    private GestureDetector      _gestureDetector = null!;
    private ScaleGestureDetector _scaleDetector   = null!;
    private bool _scrollGesturesEnabled = true;
    private bool _zoomGesturesEnabled   = true;
    private bool _rotateGesturesEnabled = true;
    private bool _tiltGesturesEnabled   = true;
    // Two-pointer tracking (rotation + tilt)
    private float _tpPrevAngle;
    private float _tpPrevMidY;
    private bool  _tpActive;

    public FrameLayout View { get; private set; } = null!;

    // -- Events ----------------------------------------------------------------

    public event Action<Map>?                OnMapReadyReceived;
    public event Action?                     OnDidBecomeIdleReceived;
    public event Action<int>?                OnCameraMoveStartedReceived;
    public event Action?                     OnCameraMoveReceived;
    public event Action?                     OnCameraIdleReceived;
    public event Action<int>?                OnCameraTrackingChangedReceived;
    public event Action?                     OnCameraTrackingDismissedReceived;
    public event Func<LatLng, double, double, bool>?         OnMapClickReceived;
    public event Func<LatLng, double, double, bool>?         OnMapLongClickReceived;
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

        _textureView = new TextureView(ctx);
        _textureView.SurfaceTextureListener = new TextureSurfaceListener
        {
            Available   = OnTextureAvailable,
            SizeChanged = (_, w, h) => OnTextureSizeChanged(w, h),
            Destroyed   = _ => DisposeNative(),
        };
        SetupGestureDetectors();

        // Attribution overlay (bottom-right corner, OSM licence compliance)
        _attrView = new TextView(ctx);
        _attrView.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        _attrView.SetTextColor(Android.Graphics.Color.Argb(220, 50, 50, 50));
        _attrView.SetBackgroundColor(Android.Graphics.Color.Argb(180, 255, 255, 255));
        _attrView.SetPadding(8, 4, 8, 4);
        _attrView.MovementMethod = LinkMovementMethod.Instance;
        _attrView.Visibility = ViewStates.Gone;

        var container = new FrameLayout(ctx);
        container.AddView(_textureView, new FrameLayout.LayoutParams(
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

        // Navigation + GPS overlay panels (custom native views — mln-cabi does
        // not expose the platform SDK's built-in controls).
        BuildNavigationPanel(ctx, container);
        BuildGpsPanel(ctx, container);

        View = container;
        RepositionOverlays();
    }

    // -- Navigation + GPS panel construction -----------------------------------

    private void BuildNavigationPanel(global::Android.Content.Context ctx, FrameLayout container)
    {
        float d = ctx.Resources!.DisplayMetrics!.Density;
        int btn = (int)Math.Round(OverlayBtnDp * d);

        _navPanel = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        _navPanel.SetBackgroundColor(Android.Graphics.Color.Argb(230, 255, 255, 255));

        // Round rotate/pitch d-pad on top.
        var dpad = BuildDpad(ctx, btn, d);

        var zoomIn   = MakeOverlayButton(ctx, "\uFF0B", btn);   // ＋
        zoomIn.Click += (_, _) => ZoomBy(1);
        var divider1 = MakeDivider(ctx, btn, d);
        var zoomOut  = MakeOverlayButton(ctx, "\uFF0D", btn);   // －
        zoomOut.Click += (_, _) => ZoomBy(-1);
        var divider2 = MakeDivider(ctx, btn, d);

        _navPanel.AddView(dpad);
        _navPanel.AddView(divider2);
        _navPanel.AddView(zoomIn);
        _navPanel.AddView(divider1);
        _navPanel.AddView(zoomOut);

        _navPanel.Visibility = _showNavControls ? ViewStates.Visible : ViewStates.Gone;
        container.AddView(_navPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
    }

    /// <summary>
    /// Builds the round rotate/pitch d-pad: up/down tilt the map (±10°, clamped
    /// 0–60°), left/right rotate it (±15°), the centre resets north, and a hollow
    /// ring around the arrows carries a blue north tick that tracks the bearing.
    /// </summary>
    private FrameLayout BuildDpad(global::Android.Content.Context ctx, int btn, float density)
    {
        var host = new FrameLayout(ctx)
        {
            LayoutParameters = new LinearLayout.LayoutParams(btn, btn),
        };

        int cell = btn / 3;

        TextView Cell(string? text, Action? onClick)
        {
            var tv = new TextView(ctx)
            {
                Gravity          = GravityFlags.Center,
                LayoutParameters = new LinearLayout.LayoutParams(cell, cell),
            };
            if (text != null)
            {
                tv.Text = text;
                tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 8f);
                tv.SetTextColor(Android.Graphics.Color.Argb(230, 40, 40, 40));
            }
            if (onClick != null) { tv.Clickable = true; tv.Click += (_, _) => onClick(); }
            return tv;
        }

        LinearLayout Row(params Android.Views.View[] cells)
        {
            var r = new LinearLayout(ctx)
            {
                Orientation      = Orientation.Horizontal,
                LayoutParameters = new LinearLayout.LayoutParams(btn, cell),
            };
            foreach (var c in cells) r.AddView(c);
            return r;
        }

        var grid = new LinearLayout(ctx)
        {
            Orientation      = Orientation.Vertical,
            LayoutParameters = new FrameLayout.LayoutParams(btn, btn),
        };
        grid.AddView(Row(Cell(null, null),               Cell("\u25B2", () => PitchBy(10)),  Cell(null, null)));                // ▲ up
        grid.AddView(Row(Cell("\u25C0", () => RotateBy(-15)), Cell(null, ResetNorth),         Cell("\u25B6", () => RotateBy(15))));  // ◀ reset ▶
        grid.AddView(Row(Cell(null, null),               Cell("\u25BC", () => PitchBy(-10)), Cell(null, null)));                // ▼ down
        host.AddView(grid);

        // Hollow compass ring around the arrows (non-interactive).
        int ringSize = (int)(btn * 0.80);
        var ring = new Android.Views.View(ctx) { Clickable = false };
        var ringDrawable = new Android.Graphics.Drawables.GradientDrawable();
        ringDrawable.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
        ringDrawable.SetStroke(Math.Max(1, (int)Math.Round(density)), Android.Graphics.Color.Argb(255, 204, 204, 204));
        ring.Background = ringDrawable;
        host.AddView(ring, new FrameLayout.LayoutParams(ringSize, ringSize) { Gravity = GravityFlags.Center });

        // North tick — a short blue mark at the top of the ring that rotates with bearing.
        _navNorthTick = new FrameLayout(ctx) { Clickable = false };
        var tick = new Android.Views.View(ctx);
        tick.SetBackgroundColor(Android.Graphics.Color.Argb(255, 10, 102, 204));
        var tickLp = new FrameLayout.LayoutParams(Math.Max(2, (int)Math.Round(density * 2)), (int)(btn * 0.14))
        {
            Gravity   = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = (btn - ringSize) / 2,
        };
        ((FrameLayout)_navNorthTick).AddView(tick, tickLp);
        host.AddView(_navNorthTick, new FrameLayout.LayoutParams(btn, btn));

        return host;
    }

    private void BuildGpsPanel(global::Android.Content.Context ctx, FrameLayout container)
    {
        float d = ctx.Resources!.DisplayMetrics!.Density;
        int btn = (int)Math.Round(OverlayBtnDp * d);

        _gpsPanel = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        _gpsPanel.SetBackgroundColor(Android.Graphics.Color.Argb(230, 255, 255, 255));

        _gpsTrackingIcon = MakeOverlayButton(ctx, "\u25CB", btn); // ○
        _gpsTrackingIcon.Click += (_, _) => CycleGpsMode();
        var divider = MakeDivider(ctx, btn, d);
        _gpsBearingIcon  = MakeOverlayButton(ctx, "\u21BA", btn); // ↺
        _gpsBearingIcon.Click += (_, _) => ResetNorth();

        _gpsPanel.AddView(_gpsTrackingIcon);
        _gpsPanel.AddView(divider);
        _gpsPanel.AddView(_gpsBearingIcon);

        _gpsPanel.Visibility = _showGpsControl ? ViewStates.Visible : ViewStates.Gone;
        container.AddView(_gpsPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        RefreshGpsIcons();
    }

    private static TextView MakeOverlayButton(global::Android.Content.Context ctx, string text, int sizePx)
    {
        var tv = new TextView(ctx)
        {
            Text        = text,
            Gravity     = GravityFlags.Center,
            LayoutParameters = new LinearLayout.LayoutParams(sizePx, sizePx),
        };
        tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 18f);
        tv.SetTextColor(Android.Graphics.Color.Argb(230, 40, 40, 40));
        tv.Clickable = true;
        return tv;
    }

    private static Android.Views.View MakeDivider(global::Android.Content.Context ctx, int widthPx, float density)
    {
        var v = new Android.Views.View(ctx);
        v.SetBackgroundColor(Android.Graphics.Color.Argb(40, 0, 0, 0));
        v.LayoutParameters = new LinearLayout.LayoutParams(widthPx, Math.Max(1, (int)Math.Round(density)));
        return v;
    }

    /// <summary>Anchors nav / GPS / attribution overlays to their corners and
    /// applies vertical stacking when several share a corner.</summary>
    private void RepositionOverlays()
    {
        if (View?.Context is not { } ctx) return;
        float d = ctx.Resources!.DisplayMetrics!.Density;
        int Px(double dp) => (int)Math.Round(dp * d);

        int margin = Px(OverlayMarginDp);
        int gap    = Px(OverlayGapDp);
        int navH   = Px(OverlayBtnDp * 3 + 2);
        int gpsH   = Px(OverlayBtnDp * 2 + 1);

        bool navVis = _showNavControls;
        bool gpsVis = _showGpsControl;

        int StackOffset(MapControlCorner c, int idx)
        {
            int off = 0;
            if (idx > 0 && navVis && _navCorner == c) off += navH + gap;
            if (idx > 1 && gpsVis && _gpsCorner == c) off += gpsH + gap;
            return off;
        }

        void Apply(Android.Views.View? v, MapControlCorner c, int off)
        {
            if (v?.LayoutParameters is not FrameLayout.LayoutParams lp) return;
            bool left = c is MapControlCorner.TopLeft or MapControlCorner.BottomLeft;
            bool top  = c is MapControlCorner.TopLeft or MapControlCorner.TopRight;
            lp.Gravity = (left ? GravityFlags.Left : GravityFlags.Right)
                       | (top  ? GravityFlags.Top  : GravityFlags.Bottom);
            lp.LeftMargin   = left ? margin : 0;
            lp.RightMargin  = left ? 0 : margin;
            lp.TopMargin    = top  ? margin + off : 0;
            lp.BottomMargin = top  ? 0 : margin + off;
            v.LayoutParameters = lp;
        }

        Apply(_navPanel,   _navCorner,  StackOffset(_navCorner, 0));
        Apply(_gpsPanel,   _gpsCorner,  StackOffset(_gpsCorner, 1));
        Apply(_attrView,   _attrCorner, StackOffset(_attrCorner, 2));
        Apply(_attrButton, _attrCorner, StackOffset(_attrCorner, 2));
    }

    // -- Surface / texture lifecycle ------------------------------------------

    private void OnTextureAvailable(Android.Graphics.SurfaceTexture surfaceTexture, int width, int height)
    {
        _textureSurface = new Android.Views.Surface(surfaceTexture);
        _nativeWindow = NativeMethods.AndroidAcquireWindow(JNIEnv.Handle, _textureSurface.Handle);
        InitMaplibre(Math.Max(1, width), Math.Max(1, height));
    }

    private void OnTextureSizeChanged(int width, int height)
    {
        _frontend?.SetSize(width, height);
        _map?.SetSize(width, height);
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
                _locIndLayer = null;               // layer belongs to the old style
                if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
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
                UpdateCompassRotation();
                OnCameraMoveReceived?.Invoke();
                break;
            case "onCameraDidChange":
                UpdateCompassRotation();
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
    public void SetRotateGesturesEnabled(bool v)  => _rotateGesturesEnabled = v;
    public void SetScrollGesturesEnabled(bool v)  => _scrollGesturesEnabled = v;
    public void SetTiltGesturesEnabled(bool v)    => _tiltGesturesEnabled   = v;
    public void SetTrackCameraPosition(bool v)                { }
    public void SetZoomGesturesEnabled(bool v)    => _zoomGesturesEnabled   = v;
    public void SetMyLocationEnabled(bool v)                  { }
    public void SetMyLocationTrackingMode(int v)              { }
    public void SetMyLocationRenderMode(int v)                { }
    public void SetLogoViewMargins(int x, int y)              { }
    public void SetCompassGravity(int gravity)                { }
    public void SetCompassViewMargins(int x, int y)           { }
    public void SetAttributionButtonGravity(int v)            { }
    public void SetAttributionButtonMargins(int x, int y)     { }
    public void SetShowNavigationControls(bool show)
    {
        _showNavControls = show;
        if (_navPanel != null)
            _navPanel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        RepositionOverlays();
    }

    public void SetShowGpsControl(bool show)
    {
        _showGpsControl = show;
        if (_gpsPanel != null)
            _gpsPanel.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        RepositionOverlays();
    }

    public void SetNavigationControlPosition(MapControlCorner corner)
    {
        if (_navCorner == corner) return;
        _navCorner = corner;
        RepositionOverlays();
    }

    public void SetGpsControlPosition(MapControlCorner corner)
    {
        if (_gpsCorner == corner) return;
        _gpsCorner = corner;
        RepositionOverlays();
    }

    public void SetAttributionControlPosition(MapControlCorner corner)
    {
        if (_attrCorner == corner) return;
        _attrCorner = corner;
        RepositionOverlays();
    }

    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat      = lat;
        _lastGpsLon      = lon;
        _lastGpsBearing  = bearing;
        _lastGpsAccuracy = Math.Max(5f, accuracyMeters);
        bool isFirstFix  = !_hasGpsFix;
        _hasGpsFix       = true;

        _pendingLocInd = new LocIndParams(lat, lon, bearing, _lastGpsAccuracy);

        if (_gpsMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing && _map != null)
        {
            bool useBearing  = _gpsMode == GpsTrackingMode.FollowBearing;
            double zoom      = _map.Zoom < 8 ? 14 : _map.Zoom;
            double camBearing = useBearing ? bearing : _map.Bearing;
            if (isFirstFix) _map.JumpTo(lat, lon, zoom, camBearing, _map.Pitch);
            else            _map.EaseTo(lat, lon, _map.Zoom, camBearing, _map.Pitch, durationMs: 200);
        }

        if (_styleReady && _style != null)
            ApplyPendingLocationIndicator();
        _map?.TriggerRepaint();
    }

    // -- Navigation + GPS behaviour --------------------------------------------

    private void ZoomBy(double delta)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom + delta, _map.Bearing, _map.Pitch, durationMs: 200);
    }

    private void ResetNorth()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, 0, _map.Pitch, durationMs: 200);
    }

    /// <summary>Rotate the map by <paramref name="deltaDeg"/> (positive = clockwise).</summary>
    private void RotateBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing + deltaDeg, _map.Pitch, durationMs: 200);
    }

    /// <summary>Tilt the map by <paramref name="deltaDeg"/>, clamped to 0–60°.</summary>
    private void PitchBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        double newPitch = Math.Max(0, Math.Min(60, _map.Pitch + deltaDeg));
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, newPitch, durationMs: 200);
    }

    private void CycleGpsMode()
    {
        _gpsMode = _gpsMode switch
        {
            GpsTrackingMode.Off           => GpsTrackingMode.Show,
            GpsTrackingMode.Show          => GpsTrackingMode.Follow,
            GpsTrackingMode.Follow        => GpsTrackingMode.FollowBearing,
            _                             => GpsTrackingMode.Off,
        };
        RefreshGpsIcons();

        if (_gpsMode == GpsTrackingMode.Off)
        {
            ClearLocationIndicator();
            return;
        }

        // Re-apply the tracking behaviour to the last known fix.
        if (_hasGpsFix)
            UpdateGpsLocation(_lastGpsLat, _lastGpsLon, _lastGpsBearing, _lastGpsAccuracy);
    }

    private void RefreshGpsIcons()
    {
        if (_gpsTrackingIcon == null) return;
        (string icon, int r, int g, int b) = _gpsMode switch
        {
            GpsTrackingMode.Show          => ("\u2299", 30, 136, 229),  // ⊙ blue
            GpsTrackingMode.Follow        => ("\u25CE", 21, 101, 192),  // ◎ deep blue
            GpsTrackingMode.FollowBearing => ("\u27A4", 245, 124, 0),   // ➤ orange
            _                             => ("\u25CB", 120, 120, 120), // ○ gray (Off)
        };
        _gpsTrackingIcon.Text = icon;
        _gpsTrackingIcon.SetTextColor(Android.Graphics.Color.Argb(255, r, g, b));
    }

    /// <summary>Rotates the compass north tick to reflect the current map bearing.</summary>
    private void UpdateCompassRotation()
    {
        if (_navNorthTick == null || _map == null) return;
        _navNorthTick.Rotation = -(float)_map.Bearing;
    }

    private void ApplyPendingLocationIndicator()
    {
        if (_pendingLocInd == null || _style == null || _gpsMode == GpsTrackingMode.Off) return;
        var p  = _pendingLocInd.Value;
        var ic = System.Globalization.CultureInfo.InvariantCulture;

        if (_locIndLayer == null)
        {
            if (_style.HasLayer(LocIndLayerId)) _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            _locIndLayer.SetPaintProperty("accuracy-radius-color", "\"rgba(30,136,229,0.3)\"");
            _locIndLayer.SetPaintProperty("accuracy-radius-border-color", "\"rgba(30,136,229,0.85)\"");
        }

        bool showBearing = _gpsMode == GpsTrackingMode.FollowBearing;
        _locIndLayer.SetPaintProperty("location",
            $"[{p.Lat.ToString(ic)},{p.Lon.ToString(ic)},0]");
        _locIndLayer.SetPaintProperty("bearing",
            (showBearing ? p.Bearing : 0f).ToString(ic));
        _locIndLayer.SetPaintProperty("accuracy-radius", p.AccuracyM.ToString(ic));
    }

    private void ClearLocationIndicatorInternal()
    {
        _pendingLocInd = null;
        _locIndLayer   = null;
        if (_styleReady && _style?.HasLayer(LocIndLayerId) == true)
            _style.RemoveLayer(LocIndLayerId);
        _map?.TriggerRepaint();
    }
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
        var attributions = MbglStyle.EnsureMapLibreAttribution(parts);

        if (attributions.Count == 0 || !_showAttrControl)
        {
            _attrView.Visibility   = ViewStates.Gone;
            _attrButton.Visibility = ViewStates.Gone;
            return;
        }

        _attrLoaded = true;
        _attrView.TextFormatted = BuildAttributionSpanned(attributions);
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
        System.Collections.Generic.IReadOnlyList<string> parts)
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
        // Reuse the existing source if present so a re-add updates it in place
        // instead of no-op'ing (which left overlay geometry stale).
        var s = _style.HasSource(sourceName) ? _style.GetSource(sourceName)! : _style.AddGeoJsonSource(sourceName);
        s.SetGeoJson(source);
    }

    public void AddGeoJsonSource(string sourceName, string source, string? optionsJson)
    {
        if (!_styleReady || _style == null) return;
        // Options (clustering etc.) only apply at creation; an existing source
        // keeps its original options and just gets new data.
        var s = _style.HasSource(sourceName)
            ? _style.GetSource(sourceName)!
            : _style.AddGeoJsonSourceOptions(sourceName, optionsJson);
        s.SetGeoJson(source);
    }

    public void SetGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        // Update the existing source's data in place (no layer churn).
        _style.GetSource(sourceName)?.SetGeoJson(source);
    }

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

    public void AddSpriteImage(string imageId, int width, int height, byte[] rgba, float pixelRatio = 1f, bool sdf = false)
    {
        if (!_styleReady || _style == null) return;
        _style.AddImage(imageId, width, height, pixelRatio, sdf, rgba);
    }

    public void RemoveSpriteImage(string imageId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveImage(imageId);
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

    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight)
        => _map?.JumpTo(latitude, longitude, zoom, bearing, pitch,
                        padTop, padLeft, padBottom, padRight);

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs = 300)
        => _map?.EaseTo(latitude, longitude, zoom, bearing, pitch,
                        padTop, padLeft, padBottom, padRight, durationMs);

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs = 500)
        => _map?.FlyTo(latitude, longitude, zoom, bearing, pitch,
                       padTop, padLeft, padBottom, padRight, durationMs);

    public void ScaleBy(double scale, double anchorX = double.NaN, double anchorY = double.NaN,
        long durationMs = 0)
        => _map?.ScaleBy(scale, anchorX, anchorY, durationMs);

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

    public string? QuerySourceFeatures(string sourceId, string? sourceLayerIds = null,
        string? filterJson = null)
        => _map?.QuerySourceFeatures(sourceId, sourceLayerIds, filterJson);

    public double? GetClusterExpansionZoom(string sourceId, string clusterFeatureJson)
        => _map?.GetClusterExpansionZoom(sourceId, clusterFeatureJson);

    public string? GetClusterChildren(string sourceId, string clusterFeatureJson)
        => _map?.GetClusterChildren(sourceId, clusterFeatureJson);

    public string? GetClusterLeaves(string sourceId, string clusterFeatureJson,
        uint limit = 10, uint offset = 0)
        => _map?.GetClusterLeaves(sourceId, clusterFeatureJson, limit, offset);

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

    public (double LatSW, double LonSW, double LatNE, double LonNE) GetVisibleBounds()
        => _map?.LatLngBoundsForCamera() ?? default;

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
    public void ClearLocationIndicator() => ClearLocationIndicatorInternal();

    // -- Gesture detection -----------------------------------------------------

    private void SetupGestureDetectors()
    {
        var ctx = Android.App.Application.Context!;
        var gl  = new MapGestureListener(this);
        _gestureDetector = new GestureDetector(ctx, gl);
        _scaleDetector   = new ScaleGestureDetector(ctx, new MapScaleListener(this));
        _textureView.SetOnTouchListener(new MapTouchListener(this));
    }

    private class MapTouchListener : Java.Lang.Object, Android.Views.View.IOnTouchListener
    {
        private readonly MapLibreMapController _ctrl;
        public MapTouchListener(MapLibreMapController ctrl) => _ctrl = ctrl;

        public bool OnTouch(Android.Views.View? v, MotionEvent? e)
        {
            if (e == null) return false;

            // Feed every event to both detectors.
            _ctrl._gestureDetector.OnTouchEvent(e);
            _ctrl._scaleDetector.OnTouchEvent(e);

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _ctrl._map?.SetGestureInProgress(true);
                    _ctrl._map?.CancelTransitions();
                    _ctrl._tpActive = false;
                    break;

                case MotionEventActions.PointerDown:
                    if (e.PointerCount == 2 &&
                        (_ctrl._rotateGesturesEnabled || _ctrl._tiltGesturesEnabled))
                    {
                        _ctrl._tpActive    = true;
                        _ctrl._tpPrevAngle = TwoFingerAngle(e);
                        _ctrl._tpPrevMidY  = TwoFingerMidY(e);
                    }
                    break;

                case MotionEventActions.Move:
                    if (_ctrl._tpActive && e.PointerCount >= 2)
                    {
                        float pr = _ctrl._pixelRatio;

                        if (_ctrl._rotateGesturesEnabled)
                        {
                            float newAngle = TwoFingerAngle(e);
                            float delta    = newAngle - _ctrl._tpPrevAngle;
                            while (delta >  180f) delta -= 360f;
                            while (delta < -180f) delta += 360f;
                            if (System.Math.Abs(delta) > 0.15f)
                            {
                                double rad = delta * System.Math.PI / 180.0;
                                double cx  = _ctrl._textureView.Width  / (2.0 * pr);
                                double cy  = _ctrl._textureView.Height / (2.0 * pr);
                                const double r = 100.0;
                                _ctrl._map?.RotateBy(
                                    cx + r, cy,
                                    cx + r * System.Math.Cos(rad),
                                    cy + r * System.Math.Sin(rad));
                                _ctrl._tpPrevAngle = newAngle;
                            }
                        }

                        if (_ctrl._tiltGesturesEnabled)
                        {
                            float newMidY = TwoFingerMidY(e);
                            float deltaY  = newMidY - _ctrl._tpPrevMidY;
                            if (System.Math.Abs(deltaY) > 0.5f)
                            {
                                // Fingers moving up (negative deltaY) increases pitch.
                                _ctrl._map?.PitchBy(-deltaY / _ctrl._pixelRatio * 0.4);
                                _ctrl._tpPrevMidY = newMidY;
                            }
                        }
                    }
                    break;

                case MotionEventActions.PointerUp:
                    if (e.PointerCount <= 2)
                        _ctrl._tpActive = false;
                    break;

                case MotionEventActions.Up:
                    _ctrl._map?.SetGestureInProgress(false);
                    _ctrl._tpActive = false;
                    break;

                case MotionEventActions.Cancel:
                    _ctrl._map?.SetGestureInProgress(false);
                    _ctrl._tpActive = false;
                    break;
            }
            return true;
        }

        private static float TwoFingerAngle(MotionEvent e) =>
            (float)(System.Math.Atan2(e.GetY(1) - e.GetY(0), e.GetX(1) - e.GetX(0))
                    * 180.0 / System.Math.PI);

        private static float TwoFingerMidY(MotionEvent e) =>
            (e.GetY(0) + e.GetY(1)) * 0.5f;
    }

    private class MapGestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly MapLibreMapController _ctrl;
        public MapGestureListener(MapLibreMapController ctrl) => _ctrl = ctrl;

        public override bool OnScroll(MotionEvent? e1, MotionEvent? e2,
                                      float distanceX, float distanceY)
        {
            // Suppress single-finger scroll while two pointers are active
            // (scale/rotate/tilt are handled by MapTouchListener directly).
            if (!_ctrl._scrollGesturesEnabled || _ctrl._tpActive) return false;
            float pr = _ctrl._pixelRatio;
            _ctrl._map?.MoveBy(-distanceX / pr, -distanceY / pr);
            return true;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent? e2,
                                     float velocityX, float velocityY)
        {
            if (!_ctrl._scrollGesturesEnabled) return false;
            double pr    = _ctrl._pixelRatio;
            double velXY = System.Math.Sqrt(velocityX * velocityX + velocityY * velocityY) / pr;
            if (velXY < 200) return false;   // too slow to fling
            long animTime  = (long)System.Math.Min(velXY / 7.0 + 400, 1500);
            double offsetX = velocityX * animTime * 0.00028 / pr;
            double offsetY = velocityY * animTime * 0.00028 / pr;
            _ctrl._map?.MoveBy(offsetX, offsetY, animTime);
            return true;
        }

        public override void OnLongPress(MotionEvent e)
        {
            if (_ctrl._tpActive || _ctrl._map == null) return;
            float pr       = _ctrl._pixelRatio;
            double sx      = e.GetX() / pr;
            double sy      = e.GetY() / pr;
            var (lat, lon) = _ctrl._map.LatLngForPixel(sx, sy);
            _ctrl.OnMapLongClickReceived?.Invoke(new LatLng(lat, lon), sx, sy);
        }

        public override bool OnSingleTapConfirmed(MotionEvent e)
        {
            if (_ctrl._map == null) return false;
            float pr       = _ctrl._pixelRatio;
            double sx      = e.GetX() / pr;
            double sy      = e.GetY() / pr;
            var (lat, lon) = _ctrl._map.LatLngForPixel(sx, sy);
            _ctrl.OnMapClickReceived?.Invoke(new LatLng(lat, lon), sx, sy);
            return true;
        }

        public override bool OnDoubleTap(MotionEvent e)
        {
            if (!_ctrl._zoomGesturesEnabled) return false;
            float pr = _ctrl._pixelRatio;
            _ctrl._map?.OnDoubleTap(e.GetX() / pr, e.GetY() / pr);
            return true;
        }
    }

    private class MapScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly MapLibreMapController _ctrl;
        public MapScaleListener(MapLibreMapController ctrl) => _ctrl = ctrl;

        public override bool OnScale(ScaleGestureDetector detector)
        {
            if (!_ctrl._zoomGesturesEnabled) return false;
            float pr     = _ctrl._pixelRatio;
            float focusX = detector.FocusX / pr;
            float focusY = detector.FocusY / pr;
            _ctrl._map?.OnPinch(detector.ScaleFactor, focusX, focusY);
            return true;
        }
    }

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
        _textureSurface?.Dispose();
        _textureSurface = null;
        _styleReady = false;
    }
}

// -- TextureView surface listener helper ---------------------------------------

internal sealed class TextureSurfaceListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
{
    public Action<Android.Graphics.SurfaceTexture, int, int>? Available;
    public Action<Android.Graphics.SurfaceTexture, int, int>? SizeChanged;
    public Action<Android.Graphics.SurfaceTexture>? Destroyed;

    public void OnSurfaceTextureAvailable(Android.Graphics.SurfaceTexture surface, int width, int height)
        => Available?.Invoke(surface, width, height);

    public void OnSurfaceTextureSizeChanged(Android.Graphics.SurfaceTexture surface, int width, int height)
        => SizeChanged?.Invoke(surface, width, height);

    public bool OnSurfaceTextureDestroyed(Android.Graphics.SurfaceTexture surface)
    {
        Destroyed?.Invoke(surface);
        return true; // release the SurfaceTexture
    }

    public void OnSurfaceTextureUpdated(Android.Graphics.SurfaceTexture surface) { }
}
#endif
