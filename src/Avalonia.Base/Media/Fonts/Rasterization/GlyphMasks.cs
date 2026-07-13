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

        public static GlyphMask Build(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key)
        {
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            if (!typeface.TryGetGlyphInkBounds(key.Glyph, out var box) ||
                box.XMax <= box.XMin || box.YMax <= box.YMin)
            {
                return GlyphMask.Empty;
            }

            // Font units are y-up, masks are y-down: the top of the mask comes from YMax.
            var left = (int)Math.Floor(box.XMin * scale) - Apron;
            var top = (int)Math.Floor(-box.YMax * scale) - Apron;
            var width = (int)Math.Ceiling(box.XMax * scale) + Apron - left;
            var height = (int)Math.Ceiling(-box.YMin * scale) + Apron - top;

            if (width <= 0 || height <= 0 || width > MaxMaskSize || height > MaxMaskSize)
            {
                return GlyphMask.Empty;
            }

            scratch.Reset();

            if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(scale, 0, 0, -scale, 0, 0), scratch))
            {
                return GlyphMask.Empty;
            }

            var alpha = new byte[width * height];

            GlyphRasterizer.Rasterize(scratch, width, height,
                -left + key.PhaseOffset, -top, key.Mode == GlyphMaskMode.Aliased, alpha);

            return new GlyphMask(alpha, width, height, left, top);
        }
    }
}
