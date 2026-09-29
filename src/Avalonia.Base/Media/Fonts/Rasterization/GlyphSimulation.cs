using System;
using System.Buffers;
using Avalonia.Media.Fonts.Tables.Glyf;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Synthesizes <see cref="FontSimulations"/> on captured glyph outlines for the managed
    /// rasterization paths, matching what the Skia backend produces for the same typeface
    /// (<c>SKFont.SkewX</c> of <see cref="FontSimulationConstants.ObliqueSlant"/> and
    /// <c>SKFont.Embolden</c>) so both engines agree.
    /// </summary>
    /// <remarks>
    /// Simulations run on the final outline: after bytecode hinting and after the auto-hinter's
    /// grid-fit warps. Those fit the font's real stems and zones, so running them over a
    /// synthesized outline would snap the added weight away again or fit a slanted stem as if
    /// it were upright. Emboldening runs before the slant, the order Skia strokes and then
    /// applies its skew. Neither simulation changes advances.
    /// </remarks>
    internal static class GlyphSimulation
    {
        /// <summary>The em size at and below which fake bold uses <see cref="SmallBoldStrokeRatio"/>.</summary>
        public const float SmallBoldEmSize = 9f;

        /// <summary>The em size at and above which fake bold uses <see cref="LargeBoldStrokeRatio"/>.</summary>
        public const float LargeBoldEmSize = 36f;

        /// <summary>Stroke width per unit of em size for fake bold at small sizes.</summary>
        public const float SmallBoldStrokeRatio = 1f / 24;

        /// <summary>Stroke width per unit of em size for fake bold at large sizes.</summary>
        public const float LargeBoldStrokeRatio = 1f / 32;

        /// <summary>Whether <paramref name="simulations"/> changes glyph outlines at all.</summary>
        public static bool AffectsOutline(FontSimulations simulations)
            => (simulations & (FontSimulations.Bold | FontSimulations.Oblique)) != 0;

        /// <summary>
        /// The fake bold stroke width per unit of em size. Small text gets a proportionally
        /// heavier stroke so the added weight stays visible at a handful of pixels, large text a
        /// lighter one so it does not turn blobby: linear between the two anchor sizes, clamped
        /// outside them (Skia's <c>kStdFakeBoldInterpKeys</c> / <c>Values</c>).
        /// </summary>
        public static float GetBoldStrokeRatio(float emSize)
        {
            if (!(emSize > SmallBoldEmSize))
            {
                return SmallBoldStrokeRatio;
            }

            if (emSize >= LargeBoldEmSize)
            {
                return LargeBoldStrokeRatio;
            }

            var t = (emSize - SmallBoldEmSize) / (LargeBoldEmSize - SmallBoldEmSize);

            return SmallBoldStrokeRatio + t * (LargeBoldStrokeRatio - SmallBoldStrokeRatio);
        }

        /// <summary>
        /// How far the bold simulation moves each edge outwards, in the units of
        /// <paramref name="emSize"/>, or zero without <see cref="FontSimulations.Bold"/>. The
        /// fake bold is a stroke-and-fill of the outline, so half the stroke lands outside.
        /// The strength follows the em size the text was laid out at, not the device size.
        /// </summary>
        public static float GetEmboldenOutset(FontSimulations simulations, float emSize)
            => (simulations & FontSimulations.Bold) != 0 && emSize > 0
                ? emSize * GetBoldStrokeRatio(emSize) * 0.5f
                : 0f;

        /// <summary>
        /// The embolden outset for a mask rasterized at the quantized scale
        /// <paramref name="scaleQ"/> of a run laid out at <paramref name="emSize"/>, in
        /// 1/64 device pixels, as it enters the mask cache identity.
        /// </summary>
        public static ushort QuantizeEmboldenOutset(FontSimulations simulations, double emSize, ushort scaleQ)
        {
            if ((simulations & FontSimulations.Bold) == 0 || !(emSize > 0))
            {
                return 0;
            }

            var pixelsPerEm = scaleQ / GlyphMaskKey.ScaleQuantum;
            var outset = pixelsPerEm * GetBoldStrokeRatio((float)emSize) * 0.5f;

            return (ushort)Math.Clamp((int)MathF.Round(outset * 64f), 0, ushort.MaxValue);
        }

        /// <summary>
        /// Applies <paramref name="simulations"/> in place to a captured outline whose points
        /// are relative to the glyph origin. <paramref name="emboldenOutset"/> is in path units;
        /// <paramref name="yDown"/> states the path's vertical orientation, which decides the
        /// slant's sign. A path stretched horizontally by <paramref name="xScale"/> (the 3x
        /// subpixel rasterization) is unstretched around the simulation, so the synthesized
        /// weight and slant are those of the square-pixel glyph.
        /// </summary>
        public static void Apply(GlyphPathBuilder path, FontSimulations simulations, float emboldenOutset,
            bool yDown, float xScale = 1f)
        {
            if (!AffectsOutline(simulations))
            {
                return;
            }

            var points = path.WritablePoints;

            if (xScale != 1f)
            {
                ScaleX(points, 1f / xScale);
            }

            if ((simulations & FontSimulations.Bold) != 0 && emboldenOutset > 0)
            {
                Embolden(path.Verbs, points, emboldenOutset);
            }

            if ((simulations & FontSimulations.Oblique) != 0)
            {
                Slant(points, yDown);
            }

            if (xScale != 1f)
            {
                ScaleX(points, xScale);
            }
        }

        /// <summary>
        /// Shears interleaved x,y points about the origin: <c>x += slant * y</c> in y-up space,
        /// <c>x -= slant * y</c> in y-down space, so ink above the baseline leans forward either way.
        /// </summary>
        public static void Slant(Span<float> points, bool yDown)
        {
            var slant = yDown ? -FontSimulationConstants.ObliqueSlant : FontSimulationConstants.ObliqueSlant;

            for (var i = 0; i + 1 < points.Length; i += 2)
            {
                points[i] += slant * points[i + 1];
            }
        }

        /// <summary>
        /// Moves every contour edge of the captured outline outwards by <paramref name="outset"/>
        /// through <see cref="OutlineEmbolden"/>, with ink contours growing and counters
        /// shrinking; the growth is symmetric about the original edges, like a stroke of twice
        /// the outset.
        /// </summary>
        public static void Embolden(ReadOnlySpan<byte> verbs, Span<float> points, float outset)
        {
            if (!(outset > 0))
            {
                return;
            }

            var contourCount = 0;

            foreach (var verb in verbs)
            {
                if ((GlyphPathVerb)verb == GlyphPathVerb.MoveTo)
                {
                    contourCount++;
                }
            }

            var pointCount = points.Length / 2;

            if (contourCount == 0 || pointCount == 0)
            {
                return;
            }

            var outline = ArrayPool<Point>.Shared.Rent(pointCount);
            var contourEnds = ArrayPool<int>.Shared.Rent(contourCount);

            try
            {
                var p = 0;
                var contour = 0;

                foreach (var verb in verbs)
                {
                    switch ((GlyphPathVerb)verb)
                    {
                        case GlyphPathVerb.MoveTo:
                            if (p > 0)
                            {
                                contourEnds[contour++] = p - 1;
                            }

                            p += 1;
                            break;
                        case GlyphPathVerb.LineTo:
                            p += 1;
                            break;
                        case GlyphPathVerb.QuadTo:
                            p += 2;
                            break;
                        case GlyphPathVerb.CubicTo:
                            p += 3;
                            break;
                    }
                }

                contourEnds[contour] = p - 1;

                for (var i = 0; i < pointCount; i++)
                {
                    outline[i] = new Point(points[i * 2], points[i * 2 + 1]);
                }

                OutlineEmbolden.Embolden(outline.AsSpan(0, pointCount), contourEnds.AsSpan(0, contourCount),
                    outset * 2, outset * 2);

                for (var i = 0; i < pointCount; i++)
                {
                    points[i * 2] = (float)outline[i].X;
                    points[i * 2 + 1] = (float)outline[i].Y;
                }
            }
            finally
            {
                ArrayPool<Point>.Shared.Return(outline);
                ArrayPool<int>.Shared.Return(contourEnds);
            }
        }

        private static void ScaleX(Span<float> points, float factor)
        {
            for (var i = 0; i < points.Length; i += 2)
            {
                points[i] *= factor;
            }
        }
    }
}
