using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugBandSplitTests
    {
        private const int BarCount = 16;
        private const double BarPitch = 1.0 / 16;
        private const double BarWidth = 1.0 / 64;

        /// <summary>
        /// Sixteen separated full-height bars. Every vertical edge crosses every horizontal band,
        /// so each horizontal band list holds all 32 of them, and each bar's top and bottom edge
        /// share its vertical band. The bars sit at multiples of 1/16 em with the x midpoint
        /// (61/128 em) in the gap between bars 7 and 8, more than the split margin away from
        /// both, so no edge lands on both sides of the split.
        /// </summary>
        private static void DrawBars(SlugContourSink sink)
        {
            for (var i = 0; i < BarCount; i++)
            {
                var left = i * BarPitch;
                var right = left + BarWidth;

                sink.BeginFigure(new Point(left, 0));
                sink.LineTo(new Point(right, 0));
                sink.LineTo(new Point(right, 1));
                sink.LineTo(new Point(left, 1));
                sink.EndFigure(true);
            }
        }

        private static (SlugTexelSerializer Serializer, SlugGlyphData Data, SlugGlyphPlacement Placement)
            Prepare(Action<SlugContourSink> draw)
        {
            var sink = new SlugContourSink();

            draw(sink);

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            return (serializer, data!, placement);
        }

        [Fact]
        public void Split_Bands_Halve_The_Loop_Bound_Of_Separated_Bars()
        {
            var (_, _, placement) = Prepare(DrawBars);

            Assert.Equal(2 * BarCount, placement.LongestHorizontalList);
            Assert.Equal(2, placement.LongestVerticalList);

            // At 64 px per em the half-pixel reach fits inside the margin, so each pixel walks
            // only the edges on its side of the midpoint: the 16 edges of bars 0-7 or of bars
            // 8-15, with nothing shared. Vertically each band holds one bar's bottom edge
            // (below the y midpoint) and its top edge (above it).
            Assert.Equal(BarCount, placement.GetHorizontalLoopBound(64));
            Assert.Equal(1, placement.GetVerticalLoopBound(64));

            // Below the enable threshold the whole list is walked.
            Assert.Equal(2 * BarCount, placement.GetHorizontalLoopBound(16));
            Assert.Equal(2, placement.GetVerticalLoopBound(16));
        }

        private static void DrawBlob(SlugContourSink sink)
        {
            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(2, 0.5), new Point(0.2, 1));
            sink.QuadraticBezierTo(new Point(-2, 1.5), new Point(0, 2));
            sink.LineTo(new Point(1, 2));
            sink.QuadraticBezierTo(new Point(3, 1), new Point(1.2, 0.2));
            sink.EndFigure(true);
        }

        /// <summary>
        /// Two overlapping curved rings under even-odd: windings of 0 to 3 across the glyph,
        /// with curves crossing the split point on both axes.
        /// </summary>
        private static void DrawEvenOddRings(SlugContourSink sink)
        {
            sink.SetFillRule(FillRule.EvenOdd);

            void Ring(double centerX, double centerY, double radius)
            {
                sink.BeginFigure(new Point(centerX + radius, centerY));
                sink.QuadraticBezierTo(new Point(centerX + radius, centerY + radius), new Point(centerX, centerY + radius));
                sink.QuadraticBezierTo(new Point(centerX - radius, centerY + radius), new Point(centerX - radius, centerY));
                sink.QuadraticBezierTo(new Point(centerX - radius, centerY - radius), new Point(centerX, centerY - radius));
                sink.QuadraticBezierTo(new Point(centerX + radius, centerY - radius), new Point(centerX + radius, centerY));
                sink.EndFigure(true);
            }

            Ring(0.4, 0.5, 0.4);
            Ring(0.4, 0.5, 0.2);
            Ring(0.6, 0.45, 0.35);
        }

        public static TheoryData<string, float, float> SplitCases()
        {
            var data = new TheoryData<string, float, float>();

            foreach (var glyph in new[] { "bars", "blob", "rings" })
            {
                foreach (var pixelsPerEm in new[] { 32f, 48f, 200f })
                {
                    data.Add(glyph, pixelsPerEm, 0f);
                    data.Add(glyph, pixelsPerEm, 30f);
                }
            }

            return data;
        }

        /// <summary>
        /// The split walk must reproduce the whole-list walk: each pixel at or past the split
        /// point sees the curves a forward ray can reach, any other pixel the curves a backward
        /// ray can reach, and the backward ray's winding negates to the forward one. The grid is
        /// a device-pixel grid under a scale and rotation, and columns of extra samples sit at
        /// quarter-pixel steps within a pixel of each split point.
        /// </summary>
        [Theory]
        [MemberData(nameof(SplitCases))]
        public void Split_Coverage_Matches_Whole_List_Coverage(string glyph, float pixelsPerEm, float rotationDegrees)
        {
            Action<SlugContourSink> draw = glyph switch
            {
                "bars" => DrawBars,
                "blob" => DrawBlob,
                _ => DrawEvenOddRings,
            };
            var (serializer, data, placement) = Prepare(draw);

            // Device -> em: rotate, then scale down. The footprint per em axis is the L1 norm of
            // that axis's device-space gradient, like the shader's fwidth replacement; the scale
            // is chosen so that footprint is exactly one pixel at pixelsPerEm, keeping the
            // split live at the 32 px threshold under rotation too.
            var angle = rotationDegrees * MathF.PI / 180f;
            var l1 = MathF.Abs(MathF.Cos(angle)) + MathF.Abs(MathF.Sin(angle));
            var cos = MathF.Cos(angle) / (pixelsPerEm * l1);
            var sin = MathF.Sin(angle) / (pixelsPerEm * l1);
            var emsPerPixelX = 1f / pixelsPerEm;
            var emsPerPixelY = 1f / pixelsPerEm;

            Assert.True(SlugGlyphPlacement.IsSplitEnabled(1f / emsPerPixelX));
            Assert.True(SlugGlyphPlacement.IsSplitEnabled(1f / emsPerPixelY));

            var worst = 0f;
            var samples = 0;

            void Sample(float emX, float emY)
            {
                var split = SlugReferenceEvaluator.Evaluate(
                    serializer.CurveTexels, serializer.BandTexels, in placement,
                    emX, emY, emsPerPixelX, emsPerPixelY);
                var whole = SlugReferenceEvaluator.Evaluate(
                    serializer.CurveTexels, serializer.BandTexels, in placement,
                    emX, emY, emsPerPixelX, emsPerPixelY, splitBands: false);

                worst = MathF.Max(worst, MathF.Abs(split - whole));
                samples++;
            }

            var marginX = 3 * emsPerPixelX;
            var marginY = 3 * emsPerPixelY;
            var spanX = data.MaxX - data.MinX + 2 * marginX;
            var spanY = data.MaxY - data.MinY + 2 * marginY;
            var extent = (int)MathF.Ceiling(MathF.Max(spanX, spanY) * pixelsPerEm * 1.5f);
            var centerX = (data.MinX + data.MaxX) * 0.5f;
            var centerY = (data.MinY + data.MaxY) * 0.5f;

            for (var py = -extent / 2; py <= extent / 2; py++)
            {
                for (var px = -extent / 2; px <= extent / 2; px++)
                {
                    var dx = px + 0.5f;
                    var dy = py + 0.5f;

                    Sample(centerX + cos * dx - sin * dy, centerY + sin * dx + cos * dy);
                }
            }

            for (var step = -4; step <= 4; step++)
            {
                var nearX = placement.HorizontalSplit + step * 0.25f * emsPerPixelX;
                var nearY = placement.VerticalSplit + step * 0.25f * emsPerPixelY;

                for (var t = 0; t <= 256; t++)
                {
                    Sample(nearX, data.MinY - marginY + spanY * t / 256);
                    Sample(data.MinX - marginX + spanX * t / 256, nearY);
                }
            }

            Assert.True(samples > 1000);
            Assert.True(worst <= 1e-4f, $"{glyph} {pixelsPerEm}px rot{rotationDegrees}: worst {worst}");
        }
    }
}
