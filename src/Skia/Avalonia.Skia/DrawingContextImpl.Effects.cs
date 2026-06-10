using System;
using Avalonia.Media;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    
    public void PushEffect(Rect? effectClipRect, IEffect effect)
    {
        CheckLease();
        using var filter = CreateEffect(effect);
        var paint = SKPaintCache.Shared.Get();
        paint.ImageFilter = filter;
        if (effectClipRect.HasValue)
            Canvas.SaveLayer(effectClipRect.Value.ToSKRect(), paint);
        else
            Canvas.SaveLayer(paint);
        SKPaintCache.Shared.ReturnReset(paint);
    }

    public void PopEffect()
    {
        CheckLease();
        RestoreCanvas();
    }

    SKImageFilter? CreateEffect(IEffect effect)
    {
        if (effect is IBlurEffect blur)
        {
            if (blur.Radius <= 0)
                return null;
            var sigma = SkBlurRadiusToSigma(blur.Radius);
            return SKImageFilter.CreateBlur(sigma, sigma);
        }

        if (effect is IDropShadowEffect drop)
        {
            var sigma = drop.BlurRadius > 0 ? SkBlurRadiusToSigma(drop.BlurRadius) : 0;
            var alpha = drop.Color.A * drop.Opacity;
            if (!_useOpacitySaveLayer)
                alpha *= _currentOpacity;
            var color = new SKColor(drop.Color.R, drop.Color.G, drop.Color.B, (byte)Math.Max(0, Math.Min(255, alpha)));

            return SKImageFilter.CreateDropShadow((float)drop.OffsetX, (float)drop.OffsetY, sigma, sigma, color);
        }

        if (effect is IOffsetEffect offset)
            return SKImageFilter.CreateOffset((float)offset.OffsetX, (float)offset.OffsetY);

        if (effect is IColorMatrixEffect colorMatrix
            && colorMatrix.Matrix.Count == ImmutableColorMatrixEffect.MatrixLength)
        {
            var matrix = new float[ImmutableColorMatrixEffect.MatrixLength];
            for (var i = 0; i < matrix.Length; i++)
                matrix[i] = (float)colorMatrix.Matrix[i];

            using var filter = SKColorFilter.CreateColorMatrix(matrix);
            return SKImageFilter.CreateColorFilter(filter);
        }

        if (effect is ICompositeEffect composite)
        {
            // Children apply in sequence: fold so each stage wraps the previous
            // one (CreateCompose applies the inner filter first).
            SKImageFilter? chain = null;
            foreach (var child in composite.Children)
            {
                var stage = CreateEffect(child);
                if (stage == null)
                    continue;

                if (chain == null)
                {
                    chain = stage;
                }
                else
                {
                    var composed = SKImageFilter.CreateCompose(stage, chain);
                    stage.Dispose();
                    chain.Dispose();
                    chain = composed;
                }
            }

            return chain;
        }

        return null;
    }
    
}
