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

        if (effect is IFloodEffect flood)
        {
            var alpha = flood.Color.A * flood.Opacity;
            var color = new SKColor(flood.Color.R, flood.Color.G, flood.Color.B,
                (byte)Math.Max(0, Math.Min(255, alpha)));

            // A Src-blend color filter replaces every pixel, transparent ones
            // included: a flood across the layer region.
            using var colorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.Src);
            return SKImageFilter.CreateColorFilter(colorFilter);
        }

        if (effect is IMergeEffect merge)
        {
            // A null input is the layer source, which is Skia's null-filter
            // convention too.
            var inputs = new SKImageFilter?[merge.Inputs.Count];
            for (var i = 0; i < inputs.Length; i++)
                inputs[i] = merge.Inputs[i] is { } input ? CreateEffect(input) : null;

            var merged = SKImageFilter.CreateMerge(inputs!);
            foreach (var input in inputs)
                input?.Dispose();
            return merged;
        }

        if (effect is IBlendEffect blend)
        {
            using var background = blend.Background is { } bg ? CreateEffect(bg) : null;
            using var foreground = blend.Foreground is { } fg ? CreateEffect(fg) : null;
            return SKImageFilter.CreateBlendMode(blend.Mode.ToSKBlendMode(), background, foreground);
        }

        if (effect is IArithmeticCompositeEffect arithmetic)
        {
            using var background = arithmetic.Background is { } bg ? CreateEffect(bg) : null;
            using var foreground = arithmetic.Foreground is { } fg ? CreateEffect(fg) : null;
            return SKImageFilter.CreateArithmetic(
                (float)arithmetic.K1, (float)arithmetic.K2, (float)arithmetic.K3, (float)arithmetic.K4,
                enforcePMColor: true, background, foreground);
        }

        if (effect is ITileEffect tile)
        {
            using var input = tile.Input is { } tileInput ? CreateEffect(tileInput) : null;
            return SKImageFilter.CreateTile(tile.Source.ToSKRect(), tile.Destination.ToSKRect(), input);
        }

        if (effect is IMorphologyEffect morphology)
        {
            using var input = morphology.Input is { } morphologyInput ? CreateEffect(morphologyInput) : null;
            var radiusX = (float)morphology.RadiusX;
            var radiusY = (float)morphology.RadiusY;
            return morphology.Dilate
                ? SKImageFilter.CreateDilate(radiusX, radiusY, input)
                : SKImageFilter.CreateErode(radiusX, radiusY, input);
        }

        if (effect is ILightingEffect lighting)
        {
            using var lightingInput = lighting.Input is { } li ? CreateEffect(li) : null;
            var lightColor = new SKColor(lighting.LightColor.R, lighting.LightColor.G, lighting.LightColor.B);
            var surfaceScale = (float)lighting.SurfaceScale;
            var constant = (float)lighting.LightingConstant;
            var shininess = (float)lighting.Shininess;

            switch (lighting.Light)
            {
                case LightSourceKind.Distant:
                {
                    // Azimuth/elevation degrees to a direction vector.
                    var azimuth = Matrix.ToRadians(lighting.Azimuth);
                    var elevation = Matrix.ToRadians(lighting.Elevation);
                    var direction = new SKPoint3(
                        (float)(Math.Cos(azimuth) * Math.Cos(elevation)),
                        (float)(Math.Sin(azimuth) * Math.Cos(elevation)),
                        (float)Math.Sin(elevation));
                    return lighting.Specular
                        ? SKImageFilter.CreateDistantLitSpecular(direction, lightColor, surfaceScale, constant, shininess, lightingInput)
                        : SKImageFilter.CreateDistantLitDiffuse(direction, lightColor, surfaceScale, constant, lightingInput);
                }
                case LightSourceKind.Point:
                {
                    var location = new SKPoint3(
                        (float)lighting.LightPosition.X, (float)lighting.LightPosition.Y, (float)lighting.LightZ);
                    return lighting.Specular
                        ? SKImageFilter.CreatePointLitSpecular(location, lightColor, surfaceScale, constant, shininess, lightingInput)
                        : SKImageFilter.CreatePointLitDiffuse(location, lightColor, surfaceScale, constant, lightingInput);
                }
                default:
                {
                    var location = new SKPoint3(
                        (float)lighting.LightPosition.X, (float)lighting.LightPosition.Y, (float)lighting.LightZ);
                    var target = new SKPoint3(
                        (float)lighting.PointsAt.X, (float)lighting.PointsAt.Y, (float)lighting.PointsAtZ);
                    var cutoff = (float)(lighting.LimitingConeAngle ?? 90);
                    return lighting.Specular
                        ? SKImageFilter.CreateSpotLitSpecular(location, target, (float)lighting.SpotExponent, cutoff,
                            lightColor, surfaceScale, constant, shininess, lightingInput)
                        : SKImageFilter.CreateSpotLitDiffuse(location, target, (float)lighting.SpotExponent, cutoff,
                            lightColor, surfaceScale, constant, lightingInput);
                }
            }
        }

        if (effect is IComponentTransferEffect transfer)
        {
            using var transferInput = transfer.Input is { } ti ? CreateEffect(ti) : null;
            using var table = SKColorFilter.CreateTable(
                ToTable(transfer.AlphaTable), ToTable(transfer.RedTable),
                ToTable(transfer.GreenTable), ToTable(transfer.BlueTable));
            return SKImageFilter.CreateColorFilter(table, transferInput);

            static byte[]? ToTable(System.Collections.Generic.IReadOnlyList<byte>? source)
            {
                if (source == null)
                    return null;
                var table = new byte[256];
                for (var i = 0; i < table.Length; i++)
                    table[i] = source[i];
                return table;
            }
        }

        if (effect is IConvolveMatrixEffect convolve)
        {
            using var convolveInput = convolve.Input is { } ci ? CreateEffect(ci) : null;
            var kernel = new float[convolve.Kernel.Count];
            for (var i = 0; i < kernel.Length; i++)
                kernel[i] = (float)convolve.Kernel[i];

            var tileMode = convolve.EdgeMode switch
            {
                ConvolveMatrixEdgeMode.Wrap => SKShaderTileMode.Repeat,
                ConvolveMatrixEdgeMode.None => SKShaderTileMode.Decal,
                _ => SKShaderTileMode.Clamp,
            };

            return SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(convolve.OrderX, convolve.OrderY),
                kernel,
                (float)(1 / convolve.Divisor),
                (float)convolve.Bias,
                new SKPointI(convolve.TargetX, convolve.TargetY),
                tileMode,
                convolve.PreserveAlpha,
                convolveInput);
        }

        if (effect is ICropEffect crop)
        {
            // A tile with identical source and destination passes the region
            // through unchanged — a crop to the primitive subregion.
            using var cropInput = crop.Input is { } cri ? CreateEffect(cri) : null;
            return SKImageFilter.CreateTile(crop.Rect.ToSKRect(), crop.Rect.ToSKRect(), cropInput);
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
