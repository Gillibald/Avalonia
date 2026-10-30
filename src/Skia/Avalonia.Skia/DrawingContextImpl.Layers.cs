using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    // The folded opacity each pushed layer replaced, restored when it pops.
    private readonly Stack<double> _layerOpacityStack = new();

    public void PushLayer(LayerOptions options)
    {
        CheckLease();

        // A layer is composited as a whole, so it takes the folded opacity as
        // part of its composite alpha and everything inside it - draws and
        // effect filters - works at full opacity; applying it again inside
        // would fade twice.
        var opacity = options.EffectiveOpacity * _currentOpacity;
        _layerOpacityStack.Push(_currentOpacity);
        _currentOpacity = 1;

        var paint = SKPaintCache.Shared.Get();
        SKImageFilter? imageFilter = null;

        if (opacity < 1.0)
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255));

        var blendMode = options.EffectiveBlendMode;
        if (blendMode != BitmapBlendingMode.SourceOver && blendMode != BitmapBlendingMode.Unspecified)
            paint.BlendMode = blendMode.ToSKBlendMode();

        if (options.Effect is { } effect)
        {
            imageFilter = CreateEffect(effect, options.Bounds);
            paint.ImageFilter = imageFilter;
        }

        if (options.Bounds.HasValue)
            Canvas.SaveLayer(options.Bounds.Value.ToSKRect(), paint);
        else
            Canvas.SaveLayer(paint);

        // PopLayer counts it back down, so backdrop sampling knows a layer is open.
        _intermediateLayerDepth++;

        imageFilter?.Dispose();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    // PopLayer is shared with IDrawingContextImpl's existing PopLayer(); see
    // DrawingContextImpl.cs for the single implementation.
}
