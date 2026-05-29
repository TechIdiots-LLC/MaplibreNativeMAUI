/**
 * MbglRunLoop.cs — Typed wrapper around mbgl_runloop_t.
 */
namespace MapLibreNative.Maui;

/// <summary>Wraps <c>mbgl_runloop_t*</c>. Must be created and disposed on the map thread.</summary>
public sealed class MbglRunLoop : IDisposable
{
    internal IntPtr Handle { get; private set; }

    public MbglRunLoop()
    {
        Handle = NativeMethods.RunLoopCreate();
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("mbgl_runloop_create returned null.");
    }

    /// <summary>Drains pending scheduled callbacks without blocking.</summary>
    public void RunOnce() => NativeMethods.RunLoopRunOnce(Handle);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.RunLoopDestroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
