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
        /// The subpixel apron in final pixels: analytic bleed is under one subpixel and the
        /// 5-tap downfilter spreads coverage two further subpixels, together under 2 pixels.
        /// </summary>
        public const int SubpixelApron = 2;

        // The stripe downfilter (1,2,3,2,1)/9 — the DirectWrite/FreeType default. Sums to the
        // divisor exactly, so solid interiors stay fully covered.
        private const int FilterDivisorRounding = 4;

        public static GlyphMask Build(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key)
        {
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            if (!typeface.TryGetGlyphInkBounds(key.Glyph, out var box) ||
                box.XMax <= box.XMin || box.YMax <= box.YMin)
            {
                return GlyphMask.Empty;
            }

            var apron = key.Mode == GlyphMaskMode.Subpixel ? SubpixelApron : Apron;

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

            // Vertical grid fit: font zones snap onto pixel rows so horizontal features render
            // hard; identical per (typeface, scale), so every glyph and every layer of a color
            // glyph warps consistently. Horizontal geometry is untouched. TextHintingMode.None
            // opts a draw out (outlines scaled only), keyed separately in the cache.
            var warp = key.GridFit ? typeface.GridFit.GetWarp(key.ScaleQ) : VerticalWarp.Identity;

            if (key.Mode == GlyphMaskMode.Subpixel)
            {
                // Three coverage samples per final pixel, one per stripe: rasterize at 3x
                // horizontal (the analytic rasterizer takes the anisotropic transform as-is),
                // then downfilter each stripe channel.
                if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(scale * 3, 0, 0, -scale, 0, 0), scratch))
                {
                    return GlyphMask.Empty;
                }

                scratch.ApplyVerticalWarp(warp);

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

            scratch.ApplyVerticalWarp(warp);

            var alpha = new byte[width * height];

            GlyphRasterizer.Rasterize(scratch, width, height,
                -left + key.PhaseOffset, -top, key.Mode == GlyphMaskMode.Aliased, alpha);

            return new GlyphMask(alpha, width, height, left, top);
        }

        /// <summary>
        /// Applies the (1,2,3,2,1)/9 stripe filter to 3x-wide coverage samples, producing
        /// interleaved RGB channel coverage — each channel reads its own subpixel plus two
        /// neighbors each side, which is what bounds color fringing.
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
                    var acc = 3 * samples[row + s];

                    if (s >= 1)
                    {
                        acc += 2 * samples[row + s - 1];
                    }

                    if (s >= 2)
                    {
                        acc += samples[row + s - 2];
                    }

                    if (s + 1 < subWidth)
                    {
                        acc += 2 * samples[row + s + 1];
                    }

                    if (s + 2 < subWidth)
                    {
                        acc += samples[row + s + 2];
                    }

                    filtered[row + s] = (byte)((acc + FilterDivisorRounding) / 9);
                }
            }

            return filtered;
        }
    }
}
