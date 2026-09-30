using System;
using System.Buffers;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    internal static partial class MaskGlyphRunRenderer
    {
        /// <summary>
        /// Attempts to draw a run the upright triage rejected (rotation, skew, anisotropic scale,
        /// sizes above <see cref="MaxPixelsPerEm"/>, a run too tall for one upright mask) through
        /// transformed glyph masks. Glyph masks are rasterized unhinted under the device
        /// transform's quantized linear part and a quarter-pixel phase in both axes, composed
        /// into a device-aligned run mask and cached on the run, so a static rotated paragraph
        /// costs one blit per run per frame. Returns <c>false</c> when this draw cannot take
        /// the run (non-solid foreground, bitmap strikes, a COLR v1-only glyph, a degenerate or
        /// extreme transform, a glyph or run past the mask bounds) and the caller falls back.
        /// </summary>
        public static bool TryDrawTransformed(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, TextRenderingMode textRenderingMode)
        {
            var transform = context.Transform;
            var determinant = transform.M11 * transform.M22 - transform.M12 * transform.M21;

            if (determinant == 0 || !double.IsFinite(determinant))
            {
                return false;
            }

            if (foreground is not ISolidColorBrush solid)
            {
                return false;
            }

            var typeface = run.GlyphTypeface;

            // A strike bitmap under a free transform would need resampling that masks do not
            // do; the native path keeps those runs.
            if (typeface.OutlineType == GlyphOutlineType.None || typeface.BitmapSource is not null ||
                HasColrV1OnlyGlyph(run))
            {
                return false;
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            // The em scale is the square root of the determinant's magnitude, which leaves a
            // pure rotation's linear part a rotation and keeps its entries on the quantization
            // grid's range.
            var norm = Math.Sqrt(Math.Abs(determinant));
            var pixelsPerEm = run.FontRenderingEmSize * norm;

            if (!(pixelsPerEm > 0) || pixelsPerEm * GlyphMaskKey.ScaleQuantum > ushort.MaxValue ||
                !GlyphMaskTransform.TryQuantize(transform.M11 / norm, transform.M12 / norm,
                    transform.M21 / norm, transform.M22 / norm, out var linear))
            {
                return false;
            }

            // Stripes only make sense on an upright pixel grid, so subpixel requests render
            // grayscale here.
            var mode = textRenderingMode == TextRenderingMode.Alias ? GlyphMaskMode.Aliased : GlyphMaskMode.Antialiased;

            var alphaContext = typeface.ColorTable is null &&
                context is IAlphaGlyphMaskContext { PrefersAlphaMasks: true } preferring
                    ? preferring
                    : null;

            var tint = alphaContext is null
                ? RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B)
                : 0u;

            var origin = run.BaselineOrigin;

            GlyphMaskKey.SnapPen((float)(origin.X * transform.M11 + origin.Y * transform.M21 + transform.M31),
                out var originX, out var originPhaseX);
            GlyphMaskKey.SnapPen((float)(origin.X * transform.M12 + origin.Y * transform.M22 + transform.M32),
                out var originY, out var originPhaseY);

            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhaseX, mode, tint,
                GridFit: false, PenSnap: false, Transform: linear, OriginPhaseY: originPhaseY);

            var cache = run.TransformedRunMasks;

            if (!cache.TryGet(key, out var runMask))
            {
                var maxSize = context is IAlphaGlyphMaskContext bounded
                    ? bounded.MaxRunMaskSize
                    : DefaultMaxRunMaskSize;

                if (!TryComposeTransformed(run, key, transform, maxSize, alphaContext, out var composed))
                {
                    return false;
                }

                if (composed is null)
                {
                    return true;   // no ink
                }

                cache.Add(key, composed);
                runMask = composed;
            }

            DrawTransformedRunMask(context, runMask, originX, originY, alphaContext, alpha, solid.Color);

            return true;
        }

        private static void DrawTransformedRunMask(IDrawingContextImpl context, RunMask runMask, int originX,
            int originY, IAlphaGlyphMaskContext? alphaContext, byte alpha, Color color)
        {
            // The mask is already in device pixels; draw it under an identity transform so the
            // canvas transform is not applied twice.
            var oldTransform = context.Transform;
            context.Transform = Matrix.Identity;

            // Parts cover disjoint tiles of the composed union, so every destination pixel is
            // blended exactly once, with the value a single mask would hold there.
            foreach (var part in runMask.Parts)
            {
                GetPartRects(part, originX, originY, out var sourceRect, out var destRect);

                if (alphaContext is not null)
                {
                    var straightTint = ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

                    alphaContext.DrawAlphaMask(part.Handle, sourceRect, destRect, straightTint);
                }
                else
                {
                    context.DrawBitmap((IBitmapImpl)part.Handle, 1, sourceRect, destRect);
                }
            }

            context.Transform = oldTransform;
        }

        /// <summary>
        /// One glyph mask of a transformed run: a glyph or a COLR v0 layer glyph at its snapped
        /// pen, with the mask placement known before rasterizing and the tint it composes with.
        /// </summary>
        private struct TransformedGlyphItem
        {
            public GlyphMaskKey Key;
            public int PenX;
            public int PenY;
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public uint Tint;
            public bool CorrectCoverage;
        }

        /// <summary>
        /// Composes the transformed run into device-aligned parts. Returns <c>false</c> when the
        /// run cannot be composed within the mask bounds, and <c>true</c> with a <c>null</c>
        /// mask when the run has no ink.
        /// </summary>
        private static bool TryComposeTransformed(ManagedGlyphRunImpl run, in RunMaskKey key, in Matrix transform,
            int maxSize, IAlphaGlyphMaskContext? alphaContext, out RunMask? runMask)
        {
            runMask = null;

            var typeface = run.GlyphTypeface;
            var items = ArrayPool<TransformedGlyphItem>.Shared.Rent(Math.Max(1, run.GlyphCount));
            var itemCount = 0;

            try
            {
                if (!TryCollectTransformedItems(run, key, transform, alphaContext is null, ref items, ref itemCount,
                        out var minX, out var minY, out var maxX, out var maxY))
                {
                    return false;
                }

                if (itemCount == 0)
                {
                    return true;
                }

                var width = maxX - minX;
                var height = maxY - minY;

                if (height > maxSize ||
                    (long)width * height * (alphaContext is null ? 4 : 1) > MaxRunMaskBytes)
                {
                    return false;
                }

                var columns = GetChunkCount(width, maxSize, out var tileWidth);
                var parts = new RunMaskPart[columns];
                var created = 0;
                var scratch = t_scratch ??= new GlyphPathBuilder();
                var state = (typeface, scratch);

                try
                {
                    for (var column = 0; column < columns; column++)
                    {
                        var tileX = minX + column * tileWidth;
                        var tileY = minY;
                        var w = Math.Min(tileWidth, maxX - tileX);
                        var h = height;

                        parts[created++] = alphaContext is null
                            ? ComposeTransformedTintedTile(typeface, state, items, itemCount, tileX, tileY, w, h)
                            : ComposeTransformedAlphaTile(typeface, state, alphaContext, items, itemCount,
                                tileX, tileY, w, h);
                    }

                    runMask = new RunMask(parts);
                    return true;
                }
                catch
                {
                    DisposeParts(parts, created);
                    throw;
                }
            }
            finally
            {
                ArrayPool<TransformedGlyphItem>.Shared.Return(items);
            }
        }

        /// <summary>
        /// Expands the run into glyph mask items (COLR v0 glyphs into their layers) with snapped
        /// pens and placements, and unions their extents. Returns <c>false</c> when a glyph mask
        /// would exceed <see cref="GlyphMasks.MaxMaskSize"/>.
        /// </summary>
        private static bool TryCollectTransformedItems(ManagedGlyphRunImpl run, in RunMaskKey key,
            in Matrix transform, bool expandColorLayers, ref TransformedGlyphItem[] items, ref int count,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = minY = int.MaxValue;
            maxX = maxY = int.MinValue;

            var typeface = run.GlyphTypeface;
            var colr = expandColorLayers ? typeface.ColorTable : null;
            var cpal = typeface.ColorPaletteTable;
            var simulated = typeface.FontSimulations != FontSimulations.None;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;

            var m11 = (float)transform.M11;
            var m12 = (float)transform.M12;
            var m21 = (float)transform.M21;
            var m22 = (float)transform.M22;

            // Each pen snaps individually relative to the run's snapped origin pixel: the
            // origin's quarter-pixel phase shifts every pen, and each pen's own fraction picks
            // that glyph's phase bucket in both axes.
            var originFractionX = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var originFractionY = key.OriginPhaseY * (1f / GlyphMaskKey.PhaseCount);

            for (var i = 0; i < indices.Length; i++)
            {
                var x = positions[i * 2];
                var y = positions[i * 2 + 1];

                GlyphMaskKey.SnapPen(originFractionX + (x * m11 + y * m21), out var penX, out var phaseX);
                GlyphMaskKey.SnapPen(originFractionY + (x * m12 + y * m22), out var penY, out var phaseY);

                var glyphKey = new GlyphMaskKey(indices[i], key.ScaleQ, phaseX, key.Mode, GridFit: false,
                    StemSnap: false, Transform: key.Transform, PhaseY: phaseY, ApplySimulations: simulated);

                if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                {
                    // COLR v0: flat-color layers composed bottom-to-top in record order. The
                    // 0xFFFF palette sentinel means "use the text foreground". Color layers skip
                    // the coverage correction: it is non-linear, so abutting layers whose
                    // coverages sum to full would show seams.
                    for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                    {
                        if (!colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                        {
                            continue;
                        }

                        uint layerTint;

                        if (layerRecord.PaletteIndex == 0xFFFF)
                        {
                            layerTint = key.Tint;
                        }
                        else if (cpal.TryGetColor(layerRecord.PaletteIndex, out var color))
                        {
                            layerTint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                        }
                        else
                        {
                            continue;
                        }

                        if (!TryAddTransformedItem(typeface, glyphKey with { Glyph = layerRecord.GlyphIndex },
                                penX, penY, layerTint, false, ref items, ref count,
                                ref minX, ref minY, ref maxX, ref maxY))
                        {
                            return false;
                        }
                    }
                }
                else if (!TryAddTransformedItem(typeface, glyphKey, penX, penY, key.Tint, true, ref items, ref count,
                             ref minX, ref minY, ref maxX, ref maxY))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddTransformedItem(GlyphTypeface typeface, in GlyphMaskKey glyphKey, int penX, int penY,
            uint tint, bool correctCoverage, ref TransformedGlyphItem[] items, ref int count,
            ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            if (!GlyphMasks.TryGetTransformedPlacement(typeface, glyphKey,
                    out var left, out var top, out var width, out var height))
            {
                return true;   // no ink
            }

            if (width > GlyphMasks.MaxMaskSize || height > GlyphMasks.MaxMaskSize)
            {
                return false;
            }

            if (count == items.Length)
            {
                var grown = ArrayPool<TransformedGlyphItem>.Shared.Rent(items.Length * 2);

                items.AsSpan(0, count).CopyTo(grown);
                ArrayPool<TransformedGlyphItem>.Shared.Return(items);
                items = grown;
            }

            items[count++] = new TransformedGlyphItem
            {
                Key = glyphKey,
                PenX = penX,
                PenY = penY,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Tint = tint,
                CorrectCoverage = correctCoverage,
            };

            minX = Math.Min(minX, penX + left);
            minY = Math.Min(minY, penY + top);
            maxX = Math.Max(maxX, penX + left + width);
            maxY = Math.Max(maxY, penY + top + height);

            return true;
        }

        private static bool IntersectsTile(in TransformedGlyphItem item, int tileX, int tileY, int width, int height)
            => item.PenX + item.Left < tileX + width && item.PenX + item.Left + item.Width > tileX &&
               item.PenY + item.Top < tileY + height && item.PenY + item.Top + item.Height > tileY;

        private static RunMaskPart ComposeTransformedAlphaTile(GlyphTypeface typeface,
            (GlyphTypeface, GlyphPathBuilder) state, IAlphaGlyphMaskContext alphaContext,
            TransformedGlyphItem[] items, int count, int tileX, int tileY, int width, int height)
        {
            var maskCache = typeface.MaskCache;
            var staging = ArrayPool<byte>.Shared.Rent(width * height);

            try
            {
                var span = staging.AsSpan(0, width * height);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    ref readonly var item = ref items[i];

                    if (!IntersectsTile(item, tileX, tileY, width, height))
                    {
                        continue;
                    }

                    var mask = maskCache.GetOrBuild(item.Key, state, s_buildMask);

                    RunMaskComposer.ComposeAlpha(mask, item.PenX - tileX, item.PenY - tileY, span, width, height);
                }

                return new RunMaskPart(alphaContext.CreateAlphaMask(span, width, height), tileX, tileY, width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        private static unsafe RunMaskPart ComposeTransformedTintedTile(GlyphTypeface typeface,
            (GlyphTypeface, GlyphPathBuilder) state, TransformedGlyphItem[] items, int count,
            int tileX, int tileY, int width, int height)
        {
            var maskCache = typeface.MaskCache;

            // Resolved per compose (a cache miss), not captured statically — the same
            // locator-scope reasoning as the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var bitmap = renderInterface.CreateWriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            try
            {
                using var framebuffer = bitmap.Lock();

                // Compose straight into the locked framebuffer. The bitmap is never locked
                // again, so its backend image identity stays stable.
                var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    ref readonly var item = ref items[i];

                    if (!IntersectsTile(item, tileX, tileY, width, height))
                    {
                        continue;
                    }

                    var mask = maskCache.GetOrBuild(item.Key, state, s_buildMask);

                    RunMaskComposer.ComposeTinted(mask, item.PenX - tileX, item.PenY - tileY, item.Tint,
                        span, width, height, framebuffer.RowBytes,
                        item.CorrectCoverage ? MaskGamma.GetTableForPremulBgra(item.Tint) : null);
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }

            return new RunMaskPart(bitmap, tileX, tileY, width, height);
        }
    }
}
