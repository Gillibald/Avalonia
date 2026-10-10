using System;
using System.Buffers;

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

        /// <summary>
        /// Builds the mask <paramref name="key"/> describes. The key's simulation is applied, not
        /// the typeface's, so a face and its simulated variants build interchangeable masks.
        /// </summary>
        public static GlyphMask Build(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key)
        {
            if (key.IsTransformed)
            {
                return BuildTransformed(typeface, scratch, key);
            }

            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

            if (!typeface.TryGetUnsimulatedGlyphInkBounds(key.Glyph, out var box) ||
                box.XMax <= box.XMin || box.YMax <= box.YMin)
            {
                return GlyphMask.Empty;
            }

            GlyphRasterDiagnostics.CountGlyphRasterization();

            // Stem snapping can move the right edge outward by up to a pixel, so it shares
            // the wider apron.
            var apron = key.Mode == GlyphMaskMode.Subpixel || key.Strong ? SubpixelApron : Apron;
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
                    // to the auto-hinter below, never to a partial result. The hinted zone
                    // belongs to the rented hinter until its contours are emitted.
                    var rented = hinter.Rent();

                    try
                    {
                        hinted = TryBuildHintedContours(rented, scratch, key, subpixelFactor, apron,
                            out left, out top, out width, out height);
                    }
                    finally
                    {
                        hinter.Return(rented);
                    }
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

                if (key.Strong && applyAutoWarps)
                {
                    scratch.ApplyHorizontalWarp(StemFit.BuildWarp(scratch, subpixelFactor,
                        typeface.StemWidths.VerticalStemWidths, scale));
                }
            }

            var simulations = key.Simulations;

            if (GlyphSimulation.AffectsOutline(simulations))
            {
                // Synthesized weight and slant go onto the fitted outline, so the fit of the
                // font's own stems survives; the mask box then comes from the result, since
                // both simulations push ink past the table and hinted boxes.
                GlyphSimulation.Apply(scratch, simulations, key.EmboldenOutset, yDown: true, subpixelFactor);

                if (!scratch.TryGetPointBounds(out var minX, out var minY, out var maxX, out var maxY))
                {
                    return GlyphMask.Empty;
                }

                left = (int)MathF.Floor(minX / subpixelFactor) - apron;
                top = (int)MathF.Floor(minY) - Apron;
                width = (int)MathF.Ceiling(maxX / subpixelFactor) + apron - left;
                height = (int)MathF.Ceiling(maxY) + Apron - top;
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
        /// Builds a glyph mask under the key's quantized linear transform and x/y phase, with
        /// the key's simulation applied through <see cref="GlyphSimulation"/> like the upright
        /// builder does. Unhinted: grid fitting assumes an upright pixel grid.
        /// </summary>
        internal static GlyphMask BuildTransformed(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key)
        {
            if (!TryGetTransformedPlacement(typeface, key, out var left, out var top, out var width, out var height) ||
                width > MaxMaskSize || height > MaxMaskSize)
            {
                return GlyphMask.Empty;
            }

            var alpha = new byte[width * height];

            return RasterizeTransformed(typeface, scratch, key, left, top, width, height, alpha, width)
                ? new GlyphMask(alpha, width, height, left, top)
                : GlyphMask.Empty;
        }

        /// <summary>
        /// Builds a transformed glyph mask into a buffer rented from the shared pool instead of
        /// an exact-fit allocation, for masks that are composed once and not cached. The caller
        /// returns <paramref name="rented"/> to <see cref="ArrayPool{T}.Shared"/> after use; it
        /// is <c>null</c> when the mask is empty.
        /// </summary>
        internal static GlyphMask BuildTransient(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key,
            out byte[]? rented)
        {
            rented = null;

            if (!TryGetTransformedPlacement(typeface, key, out var left, out var top, out var width, out var height) ||
                width > MaxMaskSize || height > MaxMaskSize)
            {
                return GlyphMask.Empty;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(width * height);

            if (!RasterizeTransformed(typeface, scratch, key, left, top, width, height, buffer, width))
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return GlyphMask.Empty;
            }

            rented = buffer;

            return GlyphMask.CreateOverBuffer(buffer, width, height, left, top);
        }

        /// <summary>
        /// The placement of a transformed glyph mask relative to its snapped pen pixel, derived
        /// without rasterizing: the ink box's corners through the design-to-device transform,
        /// rounded out, plus the apron that absorbs the analytic bleed and the sub-pixel phase.
        /// The box contains the ink, so its transformed corners bound the transformed ink for
        /// any linear map; under the identity this is exactly the upright builder's placement.
        /// A simulated key takes the simulated face's design ink box, whose bold strength is
        /// never below the device strength the key carries.
        /// Returns <c>false</c> for a glyph without ink. The size is not clamped to
        /// <see cref="MaxMaskSize"/>, so callers can decline a draw before any work.
        /// </summary>
        internal static bool TryGetTransformedPlacement(GlyphTypeface typeface, in GlyphMaskKey key,
            out int left, out int top, out int width, out int height)
        {
            left = top = width = height = 0;

            if (!typeface.TryGetSimulatedGlyphInkBounds(key.Glyph, key.Simulations, out var box) ||
                box.XMax <= box.XMin || box.YMax <= box.YMin)
            {
                return false;
            }

            GetDesignToDevice(typeface, key, out var a, out var b, out var c, out var d);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            Corner(box.XMin, box.YMin);
            Corner(box.XMin, box.YMax);
            Corner(box.XMax, box.YMin);
            Corner(box.XMax, box.YMax);

            left = (int)Math.Floor(minX) - Apron;
            top = (int)Math.Floor(minY) - Apron;
            width = (int)Math.Ceiling(maxX) + Apron - left;
            height = (int)Math.Ceiling(maxY) + Apron - top;

            return width > 0 && height > 0;

            void Corner(short x, short y)
            {
                var deviceX = x * a + y * c;
                var deviceY = x * b + y * d;

                minX = Math.Min(minX, deviceX);
                minY = Math.Min(minY, deviceY);
                maxX = Math.Max(maxX, deviceX);
                maxY = Math.Max(maxY, deviceY);
            }
        }

        /// <summary>
        /// The design-unit to device-pixel linear map of a transformed key, as the row-vector
        /// matrix (a, b; c, d): the em scale with the y flip, then the key's linear part.
        /// </summary>
        private static void GetDesignToDevice(GlyphTypeface typeface, in GlyphMaskKey key,
            out float a, out float b, out float c, out float d)
        {
            var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;
            var transform = key.Transform;

            a = scale * transform.Scale11;
            b = scale * transform.Skew12;
            c = -scale * transform.Skew21;
            d = -scale * transform.Scale22;
        }

        /// <summary>
        /// Rasterizes a transformed glyph mask at the placement
        /// <see cref="TryGetTransformedPlacement"/> reported into rows <paramref name="stride"/>
        /// bytes apart, producing exactly the coverage <see cref="BuildTransformed"/> holds.
        /// Returns <c>false</c>, leaving the destination untouched, when the glyph has no outline.
        /// </summary>
        internal static bool RasterizeTransformed(GlyphTypeface typeface, GlyphPathBuilder scratch, in GlyphMaskKey key,
            int left, int top, int width, int height, Span<byte> destination, int stride)
        {
            GlyphRasterDiagnostics.CountGlyphRasterization();
            scratch.Reset();

            var simulations = key.Simulations;

            if (GlyphSimulation.AffectsOutline(simulations))
            {
                // The simulation is the upright builder's, applied to the glyph at its em scale,
                // and the key's linear part then maps the simulated outline to the device.
                var scale = key.PixelsPerEm / typeface.Metrics.DesignEmHeight;

                if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(scale, 0, 0, -scale, 0, 0), scratch))
                {
                    return false;
                }

                GlyphSimulation.Apply(scratch, simulations, key.EmboldenOutset, yDown: true);

                var linear = key.Transform;

                scratch.ApplyLinear(linear.Scale11, linear.Skew12, linear.Skew21, linear.Scale22);
            }
            else
            {
                GetDesignToDevice(typeface, key, out var a, out var b, out var c, out var d);

                if (!typeface.TryBuildGlyphContours(key.Glyph, new Matrix(a, b, c, d, 0, 0), scratch))
                {
                    return false;
                }
            }

            GlyphRasterizer.Rasterize(scratch, width, height, -left + key.PhaseOffset, -top + key.PhaseOffsetY,
                key.Mode == GlyphMaskMode.Aliased, destination, stride);

            return true;
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
            var backwardCompatibility = key.Strong || key.Mode == GlyphMaskMode.Aliased ? 0 : 4;

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
