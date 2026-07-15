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

            // Font units are y-up, masks are y-down: the top of the mask comes from YMax.
            var left = (int)Math.Floor(box.XMin * scale) - apron;
            var top = (int)Math.Floor(-box.YMax * scale) - Apron;
            var width = (int)Math.Ceiling(box.XMax * scale) + apron - left;
            var height = (int)Math.Ceiling(-box.YMin * scale) + Apron - top;

            if (width <= 0 || height <= 0 || width > MaxMaskSize || height > MaxMaskSize)
            {
                return GlyphMask.Empty;
            }

            scratch.Reset();

            if (key.Mode == GlyphMaskMode.Subpixel)
            {
                // Three coverage samples per final pixel, one per stripe: rasterize at 3x
                // horizontal (the analytic rasterizer takes the anisotropic transform as-is),
                // then downfilter each stripe channel.
                if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(scale * 3, 0, 0, -scale, 0, 0), scratch))
                {
                    return GlyphMask.Empty;
                }

                // Vertical grid fit: zone knots plus this glyph's own stroke pairs, so
                // crossbars stay thickness-true instead of washing out. TextHintingMode.None
                // opts a draw out (outlines scaled only), keyed separately in the cache.
                if (key.GridFit)
                {
                    scratch.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(scratch, key.ScaleQ,
                        typeface.StemWidths.HorizontalStrokeWidths));
                }

                if (key.StemSnap)
                {
                    scratch.ApplyHorizontalWarp(StemFit.BuildWarp(scratch, subpixelFactor: 3,
                        typeface.StemWidths.VerticalStemWidths, scale));
                }

                var subWidth = width * 3;
                var samples = new byte[subWidth * height];

                GlyphRasterizer.Rasterize(scratch, subWidth, height,
                    (-left + key.PhaseOffset) * 3, -top, aliased: false, samples);

                return new GlyphMask(FilterStripes(samples, width, height), width, height, left, top, channels: 3);
            }

            if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(scale, 0, 0, -scale, 0, 0), scratch))
            {
                return GlyphMask.Empty;
            }

            if (key.GridFit)
            {
                scratch.ApplyVerticalWarp(typeface.GridFit.GetGlyphWarp(scratch, key.ScaleQ,
                    typeface.StemWidths.HorizontalStrokeWidths));
            }

            if (key.StemSnap)
            {
                scratch.ApplyHorizontalWarp(StemFit.BuildWarp(scratch, subpixelFactor: 1,
                    typeface.StemWidths.VerticalStemWidths, scale));
            }

            var alpha = new byte[width * height];

            GlyphRasterizer.Rasterize(scratch, width, height,
                -left + key.PhaseOffset, -top, key.Mode == GlyphMaskMode.Aliased, alpha);

            return new GlyphMask(alpha, width, height, left, top);
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
