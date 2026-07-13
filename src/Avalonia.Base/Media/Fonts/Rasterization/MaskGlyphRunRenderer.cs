using System;
using System.Buffers;
using Avalonia.Media.Fonts.Tables.Colr;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Draws a <see cref="ManagedGlyphRunImpl"/> through the mask pipeline: per-glyph masks from
    /// the typeface's cache are composed once into an immutable run bitmap (cached per run) and
    /// every subsequent frame is a single bitmap blit. Backend-independent — it uses only
    /// mandatory <see cref="IDrawingContextImpl"/> capabilities plus writeable-bitmap creation.
    /// </summary>
    internal static class MaskGlyphRunRenderer
    {
        /// <summary>Above this device size the D4 triage sends the run to the caller's fallback.</summary>
        internal const double MaxPixelsPerEm = 160;

        /// <summary>Run masks wider than this fall back (chunking is deferred until profiling asks).</summary>
        internal const int MaxRunMaskWidth = 2048;

        [ThreadStatic]
        private static GlyphPathBuilder? t_scratch;

        private static readonly Func<GlyphMaskKey, (GlyphTypeface, GlyphPathBuilder), GlyphMask> s_buildMask =
            static (key, state) => GlyphMasks.Build(state.Item1, state.Item2, key);

        /// <summary>
        /// Attempts to draw the run through the mask path. Returns <c>false</c> when this draw
        /// cannot take it — non-axis-aligned or non-uniform transform, oversized glyphs or run,
        /// or a non-solid foreground — and the caller falls back to its native path. Returns
        /// <c>true</c> when handled, including the nothing-to-draw cases.
        /// </summary>
        public static bool TryDraw(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, bool aliased)
        {
            var transform = context.Transform;

            if (transform.M12 != 0 || transform.M21 != 0)
            {
                return false;
            }

            var scaleX = transform.M11;
            var scaleY = transform.M22;

            if (scaleX <= 0 || scaleY <= 0 || Math.Abs(scaleX - scaleY) > scaleX * 0.001)
            {
                return false;
            }

            var pixelsPerEm = run.FontRenderingEmSize * scaleX;

            if (pixelsPerEm <= 0 || pixelsPerEm > MaxPixelsPerEm)
            {
                return false;
            }

            // The composed union cannot exceed the scaled ink bounds by more than the apron and
            // phase margins, so gating on Bounds here keeps Compose from ever producing an
            // oversized mask (which would otherwise be indistinguishable from "no ink").
            if (run.Bounds.Width * scaleX > MaxRunMaskWidth - 8)
            {
                return false;
            }

            if (foreground is not ISolidColorBrush solid)
            {
                // Non-solid foregrounds stay on the caller's native path for now; the
                // opacity-mask floor and backend shader tinting widen this later.
                return false;
            }

            if (run.GlyphTypeface.ColorTable is { HasV1Data: true } v1Colr)
            {
                // COLR v1 rendering goes through the record-time drawing split, which is not
                // wired yet: a glyph with only a v1 paint graph (no v0 layer fallback) must keep
                // the backend's COLR rendering rather than draw as a monochrome outline.
                var glyphs = run.GlyphIndices;

                for (var i = 0; i < glyphs.Length; i++)
                {
                    if (v1Colr.TryGetBaseGlyphV1Record(glyphs[i], out _) &&
                        !v1Colr.TryGetBaseGlyphRecord(glyphs[i], out _))
                    {
                        return false;
                    }
                }
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            var tint = RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B);

            var deviceX = (float)(run.BaselineOrigin.X * scaleX + transform.M31);
            var deviceY = run.BaselineOrigin.Y * scaleY + transform.M32;

            GlyphMaskKey.SnapPen(deviceX, out var originX, out var originPhase);
            var originY = (int)Math.Round(deviceY);

            var mode = aliased ? GlyphMaskMode.Aliased : GlyphMaskMode.Antialiased;
            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhase, mode, tint);

            var cache = run.RunMasks;

            if (!cache.TryGet(key, out var runMask))
            {
                var composed = Compose(run, key, (float)scaleX, (float)scaleY);

                if (composed is null)
                {
                    return true;   // whitespace-only run
                }

                cache.Add(key, composed);
                runMask = composed;
            }

            // The mask is already in device pixels; draw it under an identity transform so the
            // canvas transform is not applied twice.
            var oldTransform = context.Transform;
            context.Transform = Matrix.Identity;

            var sourceRect = new Rect(0, 0, runMask.Width, runMask.Height);
            var destRect = sourceRect.Translate(new Vector(originX + runMask.OffsetX, originY + runMask.OffsetY));

            context.DrawBitmap(runMask.Bitmap, 1, sourceRect, destRect);
            context.Transform = oldTransform;

            return true;
        }

        private static unsafe RunMask? Compose(ManagedGlyphRunImpl run, RunMaskKey key, float scaleX, float scaleY)
        {
            var typeface = run.GlyphTypeface;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var colr = typeface.ColorTable;
            var cpal = typeface.ColorPaletteTable;
            var state = (typeface, scratch);

            GlyphMask GetMask(ushort glyph, byte phase)
                => maskCache.GetOrBuild(new GlyphMaskKey(glyph, key.ScaleQ, phase, key.Mode), state, s_buildMask);

            // Two passes over the same (glyph → v0 layers) expansion: the first unions the
            // placements, the second composes. The second pass refetches every mask through the
            // cache — a warm hit costs nanoseconds and avoids buffering (mask, pen, tint)
            // triples whose count is unknown until layers are expanded.
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                // Each glyph's pen snaps individually: the run origin's fractional phase shifts
                // every pen, and each pen's own fraction picks that glyph's mask phase bucket.
                // v0 layer glyphs share their base glyph's pen and phase.
                var relativeX = originFraction + positions[i * 2] * scaleX;
                GlyphMaskKey.SnapPen(relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                {
                    for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                    {
                        if (colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                        {
                            UnionMask(GetMask(layerRecord.GlyphIndex, glyphPhase), penX, penY,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                }
                else
                {
                    UnionMask(GetMask(indices[i], glyphPhase), penX, penY, ref minX, ref minY, ref maxX, ref maxY);
                }
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;

            // Resolved per compose (a cache miss), not captured statically — the same
            // locator-scope reasoning as the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
            var bitmap = renderInterface.CreateWriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var framebuffer = bitmap.Lock())
            {
                // Compose straight into the locked framebuffer — no staging buffer (D7). The
                // bitmap is never locked again, so its backend image identity stays stable.
                var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
                span.Clear();

                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    GlyphMaskKey.SnapPen(relativeX, out var penX, out var glyphPhase);
                    var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                    {
                        // COLR v0: flat-color layers composed bottom-to-top in record order. The
                        // 0xFFFF palette sentinel means "use the text foreground" — the run's
                        // tint, available right here on the mask path.
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

                            RunMaskComposer.ComposeTinted(GetMask(layerRecord.GlyphIndex, glyphPhase),
                                penX - minX, penY - minY, layerTint, span, width, height, framebuffer.RowBytes);
                        }
                    }
                    else
                    {
                        RunMaskComposer.ComposeTinted(GetMask(indices[i], glyphPhase),
                            penX - minX, penY - minY, key.Tint, span, width, height, framebuffer.RowBytes);
                    }
                }
            }

            return new RunMask(bitmap, minX, minY, width, height);
        }

        private static void UnionMask(GlyphMask mask, int penX, int penY,
            ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            minX = Math.Min(minX, penX + mask.Left);
            minY = Math.Min(minY, penY + mask.Top);
            maxX = Math.Max(maxX, penX + mask.Left + mask.Width);
            maxY = Math.Max(maxY, penY + mask.Top + mask.Height);
        }
    }
}
