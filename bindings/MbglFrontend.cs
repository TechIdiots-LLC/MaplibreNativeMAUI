/**
 * MbglFrontend.cs — Typed wrapper around mbgl_frontend_t.
 */
using System.Runtime.InteropServices;

namespace MapLibreNative.Maui;

/// <summary>The renderer the loaded native library was built against.</summary>
public enum MbglRenderBackend { OpenGL, Vulkan, Metal }

/// <summary>
/// Wraps <c>mbgl_frontend_t*</c>.
/// <para>
/// <b>Ownership note:</b> once passed to <see cref="MbglMap"/>, the map takes
/// ownership of the underlying native pointer and will destroy it via
/// <c>mbgl_map_destroy</c>. <see cref="Dispose"/> becomes a no-op after
/// <see cref="TransferOwnership"/> is called. Do <em>not</em> call
/// <see cref="Dispose"/> after <see cref="MbglMap.Dispose"/>.
/// </para>
/// </summary>
public sealed class MbglFrontend : IDisposable
{
    internal IntPtr Handle { get; private set; }

    /// <summary>The renderer this native build uses (queried once from the native library).
    /// Lets the shared managed layer pick the right surface handshake — the GL and Vulkan
    /// packages ship identical C# but different native libraries under the same name.</summary>
    public static MbglRenderBackend RenderBackend { get; } =
        NativeMethods.GetRenderBackend() switch
        {
            "vulkan" => MbglRenderBackend.Vulkan,
            "metal"  => MbglRenderBackend.Metal,
            _         => MbglRenderBackend.OpenGL,
        };

    // Set to true after MbglMap takes ownership. Dispose() becomes a no-op
    // but Handle intentionally stays valid so Render/SetSize calls continue
    // to work normally through the frontend's lifetime.
    private bool _ownershipTransferred;

    /// <summary>
    /// Marks the native pointer as owned by the <see cref="MbglMap"/>.
    /// Called automatically by the <see cref="MbglMap"/> constructor.
    /// After this, <see cref="Dispose"/> will not call <c>mbgl_frontend_destroy</c>
    /// (since <c>mbgl_map_destroy</c> already does so), but <see cref="Handle"/>
    /// remains valid for <see cref="Render"/> / <see cref="SetSize"/> calls.
    /// </summary>
    internal void TransferOwnership() => _ownershipTransferred = true;

    // Prevent the delegate from being collected
    private readonly NativeMethods.RenderFn _renderDelegate;
    private readonly Action _renderCallback;

    /// <param name="surfaceHandle">Platform-specific surface: HDC (Windows), ANativeWindow* (Android), CAMetalLayer* (Apple)</param>
    /// <param name="glContext">WGL context (Windows) or null (Android/Apple)</param>
    /// <param name="widthPx">Initial width in physical pixels</param>
    /// <param name="heightPx">Initial height in physical pixels</param>
    /// <param name="pixelRatio">Device pixel ratio</param>
    /// <param name="onRender">Called by the native layer when a new frame is ready; call <see cref="Render"/> inside it.</param>
    public MbglFrontend(
        IntPtr surfaceHandle,
        IntPtr glContext,
        int    widthPx,
        int    heightPx,
        float  pixelRatio,
        Action onRender)
    {
        _renderCallback = onRender;
        _renderDelegate = _ => _renderCallback();

        Handle = NativeMethods.FrontendCreate(
            surfaceHandle, glContext,
            widthPx, heightPx, pixelRatio,
            _renderDelegate, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("mbgl_frontend_create returned null.");
    }

    /// <summary>
    /// Copies the most recently rendered frame as tightly-packed premultiplied RGBA
    /// (<paramref name="byteLength"/> must be ≥ width*height*4) into <paramref name="buffer"/>.
    /// Only the offscreen (Vulkan Windows) frontend supports this; returns false otherwise.
    /// </summary>
    public bool ReadPixels(IntPtr buffer, nuint byteLength)
        => NativeMethods.FrontendReadPixels(Handle, buffer, byteLength) == MbglStatus.Ok;

    /// <summary>
    /// Execute the pending render pass. Call from the render thread when
    /// <see cref="onRender"/> fires.
    /// </summary>
    public void Render() => NativeMethods.FrontendRender(Handle);

    public void SetSize(int widthPx, int heightPx)
        => NativeMethods.FrontendSetSize(Handle, widthPx, heightPx);

    /// <summary>
    /// Returns the platform-native view created by the frontend, or <see cref="IntPtr.Zero"/>.
    /// On Apple platforms this is the <c>MTKView*</c>; cast to <c>UIView</c> and add as subview.
    /// </summary>
    public IntPtr GetNativeView() => NativeMethods.FrontendGetNativeView(Handle);

    public void Dispose()
    {
        // If MbglMap took ownership, mbgl_map_destroy already freed this pointer.
        // Do not call mbgl_frontend_destroy — that would be a double-free.
        if (!_ownershipTransferred && Handle != IntPtr.Zero)
        {
            NativeMethods.FrontendDestroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
