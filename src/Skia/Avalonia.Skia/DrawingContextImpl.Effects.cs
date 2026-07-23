using System;
using Avalonia.Media;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    
    public void PushEffect(Rect? effectClipRect, IEffect effect)
    {
        CheckLease();
        using var filter = CreateEffect(effect, effectClipRect);
        var paint = SKPaintCache.Shared.Get();
        paint.ImageFilter = filter;
        if (effectClipRect.HasValue)
            Canvas.SaveLayer(effectClipRect.Value.ToSKRect(), paint);
        else
            Canvas.SaveLayer(paint);
        _saveLayerDepth++;
        SKPaintCache.Shared.ReturnReset(paint);
    }

    public void PopEffect()
    {
        CheckLease();
        _saveLayerDepth--;
        RestoreCanvas();
    }

    /// <param name="region">
    /// The area the graph filters, if known. Only the matrix convolution uses it:
    /// Skia's tile mode is relative to the filter's crop rect, and with no crop
    /// rect there is no boundary to tile against, so every edge mode degenerates
    /// to sampling transparent black.
    /// </param>
    /// <remarks>
    /// The convolution is the only filter here that needs the region. Supplying a
    /// crop rect at natural bounds to blur, morphology, lighting, offset, drop
    /// shadow, colour filters, the shader source and the compositing filters
    /// changes nothing; a tighter one would only clip them, which is why they are
    /// left unbounded. Blur does take a tile mode, and it is not inert either, but
    /// its default is the decal behaviour SVG wants - a blur that fades out at the
    /// edge - so there is nothing to pass.
    /// </remarks>
    SKImageFilter? CreateEffect(IEffect effect, Rect? region)
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
                inputs[i] = merge.Inputs[i] is { } input ? CreateEffect(input, region) : null;

            var merged = SKImageFilter.CreateMerge(inputs!);
            foreach (var input in inputs)
                input?.Dispose();
            return merged;
        }

        if (effect is IBlendEffect blend)
        {
            using var background = blend.Background is { } bg ? CreateEffect(bg, region) : null;
            using var foreground = blend.Foreground is { } fg ? CreateEffect(fg, region) : null;
            return SKImageFilter.CreateBlendMode(blend.Mode.ToSKBlendMode(), background, foreground);
        }

        if (effect is IArithmeticCompositeEffect arithmetic)
        {
            using var background = arithmetic.Background is { } bg ? CreateEffect(bg, region) : null;
            using var foreground = arithmetic.Foreground is { } fg ? CreateEffect(fg, region) : null;
            return SKImageFilter.CreateArithmetic(
                (float)arithmetic.K1, (float)arithmetic.K2, (float)arithmetic.K3, (float)arithmetic.K4,
                enforcePMColor: true, background, foreground);
        }

        if (effect is ITileEffect tile)
        {
            using var input = tile.Input is { } tileInput ? CreateEffect(tileInput, region) : null;
            return SKImageFilter.CreateTile(tile.Source.ToSKRect(), tile.Destination.ToSKRect(), input);
        }

        if (effect is IMorphologyEffect morphology)
        {
            using var input = morphology.Input is { } morphologyInput ? CreateEffect(morphologyInput, region) : null;
            var radiusX = (float)morphology.RadiusX;
            var radiusY = (float)morphology.RadiusY;
            return morphology.Dilate
                ? SKImageFilter.CreateDilate(radiusX, radiusY, input)
                : SKImageFilter.CreateErode(radiusX, radiusY, input);
        }

        if (effect is ILightingEffect lighting)
        {
            using var lightingInput = lighting.Input is { } li ? CreateEffect(li, region) : null;
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
            using var transferInput = transfer.Input is { } ti ? CreateEffect(ti, region) : null;
            using var table = SKColorFilter.CreateTable(
                ToTable(transfer.AlphaTable), ToTable(transfer.RedTable),
                ToTable(transfer.GreenTable), ToTable(transfer.BlueTable));
            return SKImageFilter.CreateColorFilter(table, transferInput);

            // SKColorFilter.CreateTable rejects null channel tables, so an
            // unset (identity) channel materializes as an identity table.
            static byte[] ToTable(System.Collections.Generic.IReadOnlyList<byte>? source)
            {
                var table = new byte[256];
                for (var i = 0; i < table.Length; i++)
                    table[i] = source?[i] ?? (byte)i;
                return table;
            }
        }

        if (effect is IConvolveMatrixEffect convolve)
        {
            using var convolveInput = convolve.Input is { } ci ? CreateEffect(ci, region) : null;
            var kernel = new float[convolve.Kernel.Count];
            for (var i = 0; i < kernel.Length; i++)
                kernel[i] = (float)convolve.Kernel[i];

            var tileMode = convolve.EdgeMode switch
            {
                ConvolveMatrixEdgeMode.Wrap => SKShaderTileMode.Repeat,
                ConvolveMatrixEdgeMode.None => SKShaderTileMode.Decal,
                _ => SKShaderTileMode.Clamp,
            };

            var kernelSize = new SKSizeI(convolve.OrderX, convolve.OrderY);
            var gain = (float)(1 / convolve.Divisor);
            // Skia's bias is in [0, 255] color units; the effect's is the
            // SVG [0, 1] fraction.
            var bias = (float)(convolve.Bias * 255);
            var kernelOffset = new SKPointI(convolve.TargetX, convolve.TargetY);
            // Skia's flag is convolveAlpha — the inverse of preserveAlpha:
            // the SVG default convolves premultiplied data incl. alpha.
            var convolveAlpha = !convolve.PreserveAlpha;

            // The tile mode is relative to the crop rect. Without one the filter
            // is unbounded, there is no edge to tile against, and every mode
            // silently degrades to sampling transparent black.
            return region is { } cropRect
                ? SKImageFilter.CreateMatrixConvolution(
                    kernelSize, kernel, gain, bias, kernelOffset, tileMode, convolveAlpha,
                    convolveInput, cropRect.ToSKRect())
                : SKImageFilter.CreateMatrixConvolution(
                    kernelSize, kernel, gain, bias, kernelOffset, tileMode, convolveAlpha,
                    convolveInput);
        }

        if (effect is IRecordingEffect recordingEffect)
        {
            // The recording rasterizes into a picture replayed by the filter:
            // the layer content is replaced by the recorded drawing.
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(recordingEffect.Bounds.ToSKRect());
            var createInfo = new CreateInfo
            {
                Canvas = canvas,
                Dpi = new Vector(96, 96),
                DisableSubpixelTextRendering = true,
                GrContext = _grContext,
                Gpu = _gpu,
            };
            using (var nested = new DrawingContextImpl(createInfo))
                recordingEffect.Recording.Render(nested);
            using var picture = recorder.EndRecording();
            return SKImageFilter.CreatePicture(picture, recordingEffect.Bounds.ToSKRect());
        }

        if (effect is ITurbulenceEffect turbulence)
        {
            var frequencyX = (float)turbulence.BaseFrequencyX;
            var frequencyY = (float)turbulence.BaseFrequencyY;
            var seed = (float)turbulence.Seed;
            var stitchTile = new SKPointI(
                (int)Math.Round(turbulence.StitchTile.Width),
                (int)Math.Round(turbulence.StitchTile.Height));

            using var noise = turbulence.FractalNoise
                ? turbulence.Stitch
                    ? SKShader.CreatePerlinNoiseFractalNoise(frequencyX, frequencyY, turbulence.Octaves, seed, stitchTile)
                    : SKShader.CreatePerlinNoiseFractalNoise(frequencyX, frequencyY, turbulence.Octaves, seed)
                : turbulence.Stitch
                    ? SKShader.CreatePerlinNoiseTurbulence(frequencyX, frequencyY, turbulence.Octaves, seed, stitchTile)
                    : SKShader.CreatePerlinNoiseTurbulence(frequencyX, frequencyY, turbulence.Octaves, seed);

            return noise == null ? null : SKImageFilter.CreateShader(noise, dither: false);
        }

        if (effect is IAnisotropicBlurEffect anisotropicBlur)
        {
            using var blurInput = anisotropicBlur.Input is { } abi ? CreateEffect(abi, region) : null;
            var sigmaX = anisotropicBlur.RadiusX > 0 ? SkBlurRadiusToSigma(anisotropicBlur.RadiusX) : 0;
            var sigmaY = anisotropicBlur.RadiusY > 0 ? SkBlurRadiusToSigma(anisotropicBlur.RadiusY) : 0;
            return SKImageFilter.CreateBlur(sigmaX, sigmaY, blurInput);
        }

        if (effect is ICropEffect crop)
        {
            // A tile with identical source and destination passes the region
            // through unchanged — a crop to the primitive subregion.
            using var cropInput = crop.Input is { } cri ? CreateEffect(cri, region) : null;
            return SKImageFilter.CreateTile(crop.Rect.ToSKRect(), crop.Rect.ToSKRect(), cropInput);
        }

        if (effect is ICompositeEffect composite)
        {
            // Children apply in sequence: fold so each stage wraps the previous
            // one (CreateCompose applies the inner filter first).
            SKImageFilter? chain = null;
            foreach (var child in composite.Children)
            {
                var stage = CreateEffect(child, region);
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
