using Avalonia.Platform.Surfaces;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using SkiaSharp;

namespace Avalonia.Skia;

// A render-target bitmap is a readback surface by definition: its output is captured,
// composed or saved with alpha, never presented on a panel, so the offscreen marker keeps
// subpixel text from engaging (the compositor path's test surfaces already carry it).
internal class RenderTargetBitmapImpl : WriteableBitmapImpl,
    IRenderTargetBitmapImpl,
    IOffscreenFramebufferPlatformSurface
{
    private readonly FramebufferRenderTarget _renderTarget;
    
    public RenderTargetBitmapImpl(PixelSize size, Vector dpi) : base(size, dpi, 
        SKImageInfo.PlatformColorType == SKColorType.Rgba8888 ? PixelFormats.Rgba8888 : PixelFormat.Bgra8888,
        Platform.AlphaFormat.Premul)
    {
        _renderTarget = new FramebufferRenderTarget(this, true);
    }
    
    public IDrawingContextImpl CreateDrawingContext()
    {
        return _renderTarget.CreateDrawingContext(new IRenderTarget.RenderTargetSceneInfo(
            PixelSize, Dpi.X / 96.0, CompositionTransparencyLevel.None), out _);
    }


    public bool IsCorrupted => false;
    
    public override void Dispose()
    {
        _renderTarget.Dispose();
        base.Dispose();
    }
    
    public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new FuncFramebufferRenderTarget(Lock);
}
