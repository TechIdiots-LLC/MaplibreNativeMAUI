/**
 * MlnRuntime.cs — Typed wrapper for mln_runtime* (maplibre-native-ffi).
 *
 * Sketch for the ffi-upstream branch. Replaces MbglRunLoop.cs.
 *
 * Key design differences from MbglRunLoop:
 *  - mln_runtime replaces the RunLoop + Frontend pair. The runtime is the sole
 *    top-level context; render sessions are attached to the map, not the runtime.
 *  - Events are polled via mln_runtime_poll_event() / mln_runtime_pump() instead
 *    of being delivered through push callbacks.
 *  - The owner thread runs a PumpAndPoll() tick (driven by a platform timer or
 *    a dedicated thread) to deliver events and advance animations.
 */
using System.Runtime.InteropServices;

namespace MapLibreNative.Maui.Vulkan;

/// <summary>
/// Wraps <c>mln_runtime*</c>. One instance per owner thread.
/// Call <see cref="PumpAndPoll"/> on the owner thread to drive the event loop.
/// Dispose on the owner thread after destroying all maps.
/// </summary>
public sealed class MlnRuntime : IDisposable
{
    internal IntPtr Handle { get; private set; }
    private bool _disposed;

    // ── Events fired from PumpAndPoll ─────────────────────────────────────────
    public event Action? RenderUpdateAvailable;
    public event Action? StyleLoaded;
    public event Action? MapIdle;
    public event Action<string>? LoadingFailed;  // error message
    public event Action<string>? StyleImageMissing; // image ID

    public MlnRuntime(string? assetPath = null, string? cachePath = null)
    {
        var opts = MlnMethods.RuntimeOptionsDefault();
        // String lifetimes: pin during the Create call
        unsafe
        {
            // Marshal strings to native UTF-8 if provided
            IntPtr assetNative = assetPath != null
                ? Marshal.StringToCoTaskMemUTF8(assetPath) : IntPtr.Zero;
            IntPtr cacheNative = cachePath != null
                ? Marshal.StringToCoTaskMemUTF8(cachePath) : IntPtr.Zero;
            try
            {
                opts.AssetPath = assetNative;
                opts.CachePath = cacheNative;
                MlnMethods.ThrowIfFailed(
                    MlnMethods.RuntimeCreate(&opts, out var handle),
                    nameof(MlnRuntime));
                Handle = handle;
            }
            finally
            {
                if (assetNative != IntPtr.Zero) Marshal.FreeCoTaskMem(assetNative);
                if (cacheNative != IntPtr.Zero) Marshal.FreeCoTaskMem(cacheNative);
            }
        }
    }

    /// <summary>
    /// Pumps the runtime and drains all pending events on the caller thread.
    /// Call this in your render loop or on a timer on the owner thread.
    /// </summary>
    public void PumpAndPoll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pump advances animations and fires pending callbacks internally.
        MlnMethods.RuntimePump(Handle);

        // Drain all queued events.
        unsafe
        {
            MlnRuntimeEvent ev;
            while (MlnMethods.RuntimePollEvent(Handle, &ev) == MlnStatus.Ok)
                DispatchEvent(in ev);
        }
    }

    private unsafe void DispatchEvent(in MlnRuntimeEvent ev)
    {
        switch ((MlnRuntimeEventType)ev.Type)
        {
            case MlnRuntimeEventType.MapRenderUpdateAvailable:
                RenderUpdateAvailable?.Invoke();
                break;

            case MlnRuntimeEventType.MapStyleLoaded:
                StyleLoaded?.Invoke();
                break;

            case MlnRuntimeEventType.MapIdle:
                MapIdle?.Invoke();
                break;

            case MlnRuntimeEventType.MapLoadingFailed:
                // Read the borrowed message from the event envelope
                var msg = ev.Message != IntPtr.Zero
                    ? Marshal.PtrToStringUTF8(ev.Message, (int)ev.MessageSize)
                    : "map loading failed";
                LoadingFailed?.Invoke(msg ?? "map loading failed");
                break;

            case MlnRuntimeEventType.MapStyleImageMissing:
                // Read the borrowed image ID from the typed payload
                if (ev.Payload != IntPtr.Zero
                    && ev.PayloadSize >= Marshal.SizeOf<MlnRuntimeEventStyleImageMissing>())
                {
                    var p = Marshal.PtrToStructure<MlnRuntimeEventStyleImageMissing>(ev.Payload);
                    var imageId = p.ImageId != IntPtr.Zero
                        ? Marshal.PtrToStringUTF8(p.ImageId, (int)p.ImageIdSize)
                        : string.Empty;
                    StyleImageMissing?.Invoke(imageId ?? string.Empty);
                }
                else
                {
                    StyleImageMissing?.Invoke(string.Empty);
                }
                break;

            // Ignore event types not yet wired up.
            default:
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero)
        {
            MlnMethods.RuntimeDestroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
