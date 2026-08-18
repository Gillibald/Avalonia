using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Builds <see cref="GlyphMask"/> payloads for <see cref="GlyphMaskCache"/>: one table walk
    /// into a caller-provided (thread-reused) <see cref="GlyphPathBuilder"/>, one coverage fill
    /// into an exact-fit buffer — the cached array is the single allocation of the cold path.
    /// </summary>
    internal static class GlyphMasks
    {
        /// <summary>
        /// One extra pixel around the scaled ink box: analytic AA bleeds less than a pixel past
        /// the control-point box, which itself contains the ink.
        /// </summary>
        public const int Apron = 1;

        /// <summary>
        /// Defensive ceiling on mask dimensions; the transform triage sends larger glyphs to the
        /// geometry path long before this, so hitting it means a hostile or broken input.
        /// </summary>
        public const int MaxMaskSize = 4096;

        /// <summary>
        /// The subpixel apron in final pixels: analytic bleed plus the box downfilter stay
        /// under one pixel, but stem snapping (which shares this apron) can move the right
        /// edge outward by up to a pixel, so two pixels cover both consumers.
        /// </summary>
        public const int SubpixelApron = 2;

        // The stripe downfilter (1,1,1)/3 — matches the DirectWrite host's fringe character
        // (see FilterStripes). Sums to the divisor exactly, so solid interiors stay fully
        // covered.
        private const int FilterDivisorRounding = 1;

        public static GlyphMask Build(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key)
        {
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            if (!typeface.TryGetGlyphInkBounds(key.Glyph, out var box) ||
                box.XMax <= box.XMin || box.YMax <= box.YMin)
            {
                return GlyphMask.Empty;
            }

            // Stem snapping can move the right edge outward by up to a pixel, so it shares
            // the wider apron.
            var apron = key.Mode == GlyphMaskMode.Subpixel || key.StemSnap ? SubpixelApron : Apron;
            var subpixelFactor = key.Mode == GlyphMaskMode.Subpixel ? 3 : 1;

            scratch.Reset();

            int left = 0, top = 0, width = 0, height = 0;
            var applyAutoWarps = true;
            var hinted = false;

            if (key.GridFit && typeface.GetTrueTypeHinter(key.ScaleQ, key.Mode) is { } hinter)
            {
                if (hinter.State.GlyphHintingDisabled)
                {
                    // The font's control program disabled glyph fitting at this size;
                    // honoring it means unhinted outlines, not the auto-hinter's fit.
                    applyAutoWarps = false;
                }
                else
                {
                    // The font's own programs grid-fit the outline. Any veto falls through
                    // to the auto-hinter below, never to a partial result.
                    hinted = TryBuildHintedContours(hinter, scratch, key, subpixelFactor, apron,
                        out left, out top, out width, out height);
                }
            }

            if (!hinted)
            {
                // Font units are y-up, masks are y-down: the top comes from YMax.
                left = (int)Math.Floor(box.XMin * scale) - apron;
                top = (int)Math.Floor(-box.YMax * scale) - Apron;
                width = (int)Math.Ceiling(box.XMax * scale) + apron - left;
                height = (int)Math.Ceiling(-box.YMin * scale) + Apron - top;

                if (!typeface.TryBuildGlyphContours(key.Glyph,
                        new Matrix(scale * subpixelFactor, 0, 0, -scale, 0, 0), scratch))
                {
                    return GlyphMask.Empty;
                }

                // Vertical grid fit: zone knots plus this glyph's own stroke pairs, so
                // crossbars stay thickness-true instead of washing out. TextHintingMode.None
                // opts a draw out (outlines scaled only), keyed separately in the cache.
                if (key.GridFit && applyAutoWarps)
                {
                    scratch.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(scratch, key.ScaleQ,
                        typeface.StemWidths.HorizontalStrokeWidths));
                }

                if (key.StemSnap && applyAutoWarps)
                {
                    scratch.ApplyHorizontalWarp(StemFit.BuildWarp(scratch, subpixelFactor,
                        typeface.StemWidths.VerticalStemWidths, scale));
                }
            }

            if (width <= 0 || height <= 0 || width > MaxMaskSize || height > MaxMaskSize)
            {
                return GlyphMask.Empty;
            }

            if (key.Mode == GlyphMaskMode.Subpixel)
            {
                // Three coverage samples per final pixel, one per stripe: rasterize at 3x
                // horizontal (the analytic rasterizer takes the anisotropic transform as-is),
                // then downfilter each stripe channel.
                var subWidth = width * 3;
                var samples = new byte[subWidth * height];

                GlyphRasterizer.Rasterize(scratch, subWidth, height,
                    (-left + key.PhaseOffset) * 3, -top, aliased: false, samples);

                return new GlyphMask(FilterStripes(samples, width, height), width, height, left, top, channels: 3);
            }

            var alpha = new byte[width * height];

            GlyphRasterizer.Rasterize(scratch, width, height,
                -left + key.PhaseOffset, -top, key.Mode == GlyphMaskMode.Aliased, alpha);

            return new GlyphMask(alpha, width, height, left, top);
        }

        /// <summary>
        /// Runs the glyph's instructions and emits the hinted outline into the scratch
        /// builder. Bounds come from the hinted points themselves rather than the table ink
        /// box, since instructions move edges by design. The interpreter hints at logical
        /// 1x; the emission transform applies the y-flip and any subpixel stretch after.
        /// </summary>
        private static bool TryBuildHintedContours(
            Fonts.Rasterization.TrueType.TrueTypeGlyphHinter hinter,
            GlyphPathBuilder scratch,
            in GlyphMaskKey key,
            int subpixelFactor,
            int apron,
            out int left,
            out int top,
            out int width,
            out int height)
        {
            left = top = width = height = 0;

            // Strong hinting and bi-level rendering interpret the full program; the natural
            // modes run the v40 compatibility class, where x never moves and quarter-pixel
            // phases stay valid.
            var backwardCompatibility = key.StemSnap || key.Mode == GlyphMaskMode.Aliased ? 0 : 4;

            if (!hinter.TryHint(key.Glyph, backwardCompatibility))
            {
                return false;
            }

            var zone = hinter.Zone!;
            var outline = zone.PointCount - 4;

            if (outline <= 0)
            {
                return false;
            }

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < outline; i++)
            {
                minX = Math.Min(minX, zone.CurX[i]);
                minY = Math.Min(minY, zone.CurY[i]);
                maxX = Math.Max(maxX, zone.CurX[i]);
                maxY = Math.Max(maxY, zone.CurY[i]);
            }

            if (minX > maxX || minY > maxY)
            {
                return false;
            }

            // 26.6 y-up to y-down pixel rows; arithmetic shifts floor correctly for
            // negatives, and ceil(v/64) is floor((v + 63)/64).
            left = (minX >> 6) - apron;
            top = (-maxY >> 6) - Apron;
            width = ((maxX + 63) >> 6) + apron - left;
            height = ((-minY + 63) >> 6) + Apron - top;

            if (width <= 0 || height <= 0 || width > MaxMaskSize || height > MaxMaskSize)
            {
                return false;
            }

            Fonts.Rasterization.TrueType.TrueTypeGlyphEmitter.Emit(
                zone, new Matrix(subpixelFactor, 0, 0, -1, 0, 0), scratch);

            return true;
        }

        /// <summary>
        /// Applies the (1,1,1)/3 stripe filter to 3x-wide coverage samples, producing
        /// interleaved RGB channel coverage — each channel reads its own subpixel plus one
        /// neighbor each side. This matches the DirectWrite host's fringe character: the GDI
        /// ClearType 5-tap (1,2,3,2,1)/9 filters roughly a third of the fringe saturation
        /// away, which reads as a temperature cast against DW-rendered text, while dropping
        /// the filter entirely overshoots into harsh color (measured in LcdTemperatureProbe;
        /// the ratio gate lives in LcdFringeSaturationTests).
        /// </summary>
        private static byte[] FilterStripes(byte[] samples, int width, int height)
        {
            var subWidth = width * 3;
            var filtered = new byte[subWidth * height];

            for (var y = 0; y < height; y++)
            {
                var row = y * subWidth;

                for (var s = 0; s < subWidth; s++)
                {
                    var acc = (int)samples[row + s];

                    if (s >= 1)
                    {
                        acc += samples[row + s - 1];
                    }

                    if (s + 1 < subWidth)
                    {
                        acc += samples[row + s + 1];
                    }

                    filtered[row + s] = (byte)((acc + FilterDivisorRounding) / 3);
                }
            }

            return filtered;
        }
    }
}
