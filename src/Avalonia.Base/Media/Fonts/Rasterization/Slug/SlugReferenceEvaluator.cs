using System;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// A managed port of the Slug reference pixel shader: evaluates coverage for one em-space
    /// sample directly from serialized texels — band selection, root eligibility, quadratic
    /// root solving, and the weighted two-ray blend, all with the shader's exact decisions.
    /// This is the oracle side of the SkSL spike and the debugging companion afterwards: the
    /// GPU port must match this evaluator on identical texels, and payload bugs reproduce here
    /// under a debugger instead of inside a fragment shader.
    /// </summary>
    /// <remarks>
    /// Ported from the reference implementation by Eric Lengyel — github.com/EricLengyel/Slug,
    /// licensed MIT OR Apache-2.0, patent dedicated to the public domain; credit is required
    /// and given here and in the eventual shader source.
    /// </remarks>
    internal static class SlugReferenceEvaluator
    {
        /// <summary>
        /// Computes coverage in [0, 1] for the sample at (<paramref name="emX"/>,
        /// <paramref name="emY"/>). <paramref name="emsPerPixelX"/>/<paramref name="emsPerPixelY"/>
        /// are the em-space footprint of one device pixel per axis — what the HLSL derives with
        /// fwidth, constant under an affine transform.
        /// </summary>
        public static float Evaluate(
            ReadOnlySpan<Half> curveTexels, ReadOnlySpan<Half> bandTexels,
            in SlugGlyphPlacement placement, float emX, float emY,
            float emsPerPixelX, float emsPerPixelY)
        {
            var pixelsPerEmX = 1f / emsPerPixelX;
            var pixelsPerEmY = 1f / emsPerPixelY;

            // Band selection: scale and offset, truncate, clamp — int() truncates in HLSL and
            // the clamp absorbs out-of-range samples, so points outside the bounds still pick
            // the nearest band and correctly accumulate zero winding.
            var bandX = Math.Clamp(
                (int)(emX * placement.BandScaleX + placement.BandOffsetX), 0, placement.VerticalBandCount - 1);
            var bandY = Math.Clamp(
                (int)(emY * placement.BandScaleY + placement.BandOffsetY), 0, placement.HorizontalBandCount - 1);

            float xcov = 0f, xwgt = 0f;

            var (hCount, hListX, hListY) = SlugTexelDecoder.ReadBandHeader(
                bandTexels, placement.GlyphLocX, placement.GlyphLocY, bandY);

            for (var i = 0; i < hCount; i++)
            {
                var (cx, cy) = SlugTexelDecoder.ReadListEntry(bandTexels, hListX, hListY, i);
                var curve = SlugTexelDecoder.ReadCurve(curveTexels, cx, cy);

                var x1 = curve.X1 - emX;
                var y1 = curve.Y1 - emY;
                var x2 = curve.X2 - emX;
                var y2 = curve.Y2 - emY;
                var x3 = curve.X3 - emX;
                var y3 = curve.Y3 - emY;

                // Sorted descending by max x: once a curve is fully left of the pixel, the rest
                // of the band is too.
                if (MathF.Max(x1, MathF.Max(x2, x3)) * pixelsPerEmX < -0.5f)
                {
                    break;
                }

                var code = CalcRootCode(y1, y2, y3);

                if (code != 0)
                {
                    SolveQuad(y1, y2, y3, x1, x2, x3, out var r1, out var r2);

                    r1 *= pixelsPerEmX;
                    r2 *= pixelsPerEmX;

                    if ((code & 1u) != 0)
                    {
                        xcov += Saturate(r1 + 0.5f);
                        xwgt = MathF.Max(xwgt, Saturate(1f - MathF.Abs(r1) * 2f));
                    }

                    if (code > 1u)
                    {
                        xcov -= Saturate(r2 + 0.5f);
                        xwgt = MathF.Max(xwgt, Saturate(1f - MathF.Abs(r2) * 2f));
                    }
                }
            }

            float ycov = 0f, ywgt = 0f;

            // Vertical band headers follow all horizontal ones in the header block.
            var (vCount, vListX, vListY) = SlugTexelDecoder.ReadBandHeader(
                bandTexels, placement.GlyphLocX, placement.GlyphLocY, placement.HorizontalBandCount + bandX);

            for (var i = 0; i < vCount; i++)
            {
                var (cx, cy) = SlugTexelDecoder.ReadListEntry(bandTexels, vListX, vListY, i);
                var curve = SlugTexelDecoder.ReadCurve(curveTexels, cx, cy);

                var x1 = curve.X1 - emX;
                var y1 = curve.Y1 - emY;
                var x2 = curve.X2 - emX;
                var y2 = curve.Y2 - emY;
                var x3 = curve.X3 - emX;
                var y3 = curve.Y3 - emY;

                if (MathF.Max(y1, MathF.Max(y2, y3)) * pixelsPerEmY < -0.5f)
                {
                    break;
                }

                var code = CalcRootCode(x1, x2, x3);

                if (code != 0)
                {
                    SolveQuad(x1, x2, x3, y1, y2, y3, out var r1, out var r2);

                    r1 *= pixelsPerEmY;
                    r2 *= pixelsPerEmY;

                    // The vertical ray sees the opposite crossing orientation, so the
                    // contribution signs flip relative to the horizontal loop.
                    if ((code & 1u) != 0)
                    {
                        ycov -= Saturate(r1 + 0.5f);
                        ywgt = MathF.Max(ywgt, Saturate(1f - MathF.Abs(r1) * 2f));
                    }

                    if (code > 1u)
                    {
                        ycov += Saturate(r2 + 0.5f);
                        ywgt = MathF.Max(ywgt, Saturate(1f - MathF.Abs(r2) * 2f));
                    }
                }
            }

            // Blend the two ray estimates by their edge-proximity weights; the absolute values
            // make either winding-direction convention work.
            var coverage = MathF.Max(
                MathF.Abs(xcov * xwgt + ycov * ywgt) / MathF.Max(xwgt + ywgt, 1f / 65536f),
                MathF.Min(MathF.Abs(xcov), MathF.Abs(ycov)));

            if (placement.EvenOdd)
            {
                var wrapped = coverage * 0.5f;

                return 1f - MathF.Abs(1f - (wrapped - MathF.Floor(wrapped)) * 2f);
            }

            return Saturate(coverage);
        }

        /// <summary>
        /// The root eligibility code from the sign bits of the three sample-relative
        /// perpendicular coordinates: bit 0 = first root contributes, bit 8 = second root.
        /// </summary>
        private static uint CalcRootCode(float p1, float p2, float p3)
        {
            var i1 = BitConverter.SingleToUInt32Bits(p1) >> 31;
            var i2 = BitConverter.SingleToUInt32Bits(p2) >> 30;
            var i3 = BitConverter.SingleToUInt32Bits(p3) >> 29;

            var shift = (i2 & 2u) | (i1 & ~2u);

            shift = (i3 & 4u) | (shift & ~4u);

            return (0x2E74u >> (int)shift) & 0x0101u;
        }

        /// <summary>
        /// Solves the perpendicular polynomial a·t² − 2b·t + c = 0 (a = p1 − 2p2 + p3,
        /// b = p1 − p2, c = p1; imaginary roots collapse to the extremum, near-linear curves to
        /// the single linear root) and evaluates the ray-parallel coordinate at both roots.
        /// </summary>
        private static void SolveQuad(
            float perp1, float perp2, float perp3,
            float para1, float para2, float para3,
            out float r1, out float r2)
        {
            var a = perp1 - 2f * perp2 + perp3;
            var b = perp1 - perp2;
            var aPara = para1 - 2f * para2 + para3;
            var bPara = para1 - para2;
            var ra = 1f / a;
            var rb = 0.5f / b;
            var d = MathF.Sqrt(MathF.Max(b * b - a * perp1, 0f));
            var t1 = (b - d) * ra;
            var t2 = (b + d) * ra;

            if (MathF.Abs(a) < 1f / 65536f)
            {
                t1 = t2 = perp1 * rb;
            }

            r1 = (aPara * t1 - bPara * 2f) * t1 + para1;
            r2 = (aPara * t2 - bPara * 2f) * t2 + para1;
        }

        private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);
    }
}
