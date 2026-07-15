using System;
using Avalonia.Metadata;

namespace Avalonia.Platform.Surfaces
{
    [Unstable]
    public interface IFramebufferPlatformSurface : IPlatformRenderSurface
    {
        IFramebufferRenderTarget CreateFramebufferRenderTarget();
    }

    /// <summary>
    /// Marks a framebuffer surface whose contents are captured or read back (bitmap targets,
    /// headless windows, test harnesses) rather than presented on a monitor. Subpixel (LCD)
    /// text is not eligible on such surfaces: their output may be composed, resampled or kept
    /// with alpha, where per-channel coverage has no valid interpretation. Framebuffers
    /// without this marker are treated as display-bound.
    /// </summary>
    internal interface IOffscreenFramebufferPlatformSurface : IFramebufferPlatformSurface
    {
    }
    
    
    [PrivateApi]
    public interface IFramebufferRenderTarget : IDisposable, IPlatformRenderSurfaceRenderTarget
    {
        /// <summary>
        /// Provides a framebuffer descriptor for drawing.
        /// </summary>
        /// <remarks>
        /// Contents should be drawn on actual window after disposing
        /// </remarks>
        ILockedFramebuffer Lock(IRenderTarget.RenderTargetSceneInfo sceneInfo, out FramebufferLockProperties properties);

        bool RetainsFrameContents => false;
    }
    
    [PrivateApi]
    public record struct FramebufferLockProperties(bool PreviousFrameIsRetained);

    /// <summary>
    /// For simple cases when framebuffer is always available
    /// </summary>
    public class FuncFramebufferRenderTarget : IFramebufferRenderTarget
    {
        public delegate ILockedFramebuffer LockFramebufferDelegate(IRenderTarget.RenderTargetSceneInfo sceneInfo, out FramebufferLockProperties properties);
        private readonly LockFramebufferDelegate _lockFramebuffer;

        public FuncFramebufferRenderTarget(Func<ILockedFramebuffer> lockFramebuffer) :
            this((_, out properties) =>
            {
                properties = default;
                return lockFramebuffer();
            })
        {

        }
        

        public FuncFramebufferRenderTarget(LockFramebufferDelegate lockFramebuffer, bool retainsFrameContents = false)
        {
            _lockFramebuffer = lockFramebuffer;
            RetainsFrameContents = retainsFrameContents;
        }
        
        public void Dispose()
        {
            // No-op
        }

        public ILockedFramebuffer Lock(IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out FramebufferLockProperties properties) => _lockFramebuffer(sceneInfo, out properties);

        public bool RetainsFrameContents { get; }
    }
    
}
