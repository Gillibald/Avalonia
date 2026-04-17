using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    public void PushLayer(LayerOptions options)
    {
        CheckLease();

        var paint = SKPaintCache.Shared.Get();
        SKImageFilter? imageFilter = null;

        var opacity = options.EffectiveOpacity;
        if (opacity < 1.0)
            paint.Color = new SKColor(255, 255, 255, (byte)(opacity * 255));

        var blendMode = options.EffectiveBlendMode;
        if (blendMode != BitmapBlendingMode.SourceOver && blendMode != BitmapBlendingMode.Unspecified)
            paint.BlendMode = blendMode.ToSKBlendMode();

        if (options.Effect is { } effect)
        {
            imageFilter = CreateEffect(effect);
            paint.ImageFilter = imageFilter;
        }

        if (options.Bounds.HasValue)
            Canvas.SaveLayer(options.Bounds.Value.ToSKRect(), paint);
        else
            Canvas.SaveLayer(paint);

        imageFilter?.Dispose();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    // PopLayer is shared with IDrawingContextImpl's existing PopLayer(); see
    // DrawingContextImpl.cs for the single implementation.
}
