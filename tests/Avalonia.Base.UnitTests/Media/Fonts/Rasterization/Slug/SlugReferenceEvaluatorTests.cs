using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// Validates the whole payload pipeline — sink, band encoder, texel serializer, and the
    /// ported shader math — against the analytic cell-coverage rasterizer as the oracle. Slug
    /// estimates coverage from two winding rays while the oracle computes exact area, so edge
    /// pixels may differ within tolerance; interior and exterior pixels must agree hard, which
    /// is what catches winding, banding, and layout defects.
    /// </summary>
    public class SlugReferenceEvaluatorTests
    {
        private static void AssertMatchesRasterizer(Action<SlugContourSink> draw,
            double meanGate, double worstGate, float scale = 64f)
        {
            var sink = new SlugContourSink();

            draw(sink);

            var data = SlugBandEncoder.Encode(sink);

            Assert.NotNull(data);

            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            // The oracle rasterizes the exact same quadratic chains, scaled to pixel space.
            var builder = new GlyphPathBuilder();

            builder.SetFillRule(data!.FillRule);

            for (var contour = 0; contour < data.ContourCount; contour++)
            {
                var start = data.GetContourStart(contour);
                var count = data.GetContourCurveCount(contour);
                var first = data.GetCurve(start);

                builder.BeginFigure(new Point(first.X1 * scale, first.Y1 * scale));

                for (var j = 0; j < count; j++)
                {
                    var curve = data.GetCurve(start + j);

                    builder.QuadraticBezierTo(
                        new Point(curve.X2 * scale, curve.Y2 * scale),
                        new Point(curve.X3 * scale, curve.Y3 * scale));
                }

                builder.EndFigure(true);
            }

            var startX = (int)Math.Floor(data.MinX * scale) - 2;
            var startY = (int)Math.Floor(data.MinY * scale) - 2;
            var width = (int)Math.Ceiling(data.MaxX * scale) + 3 - startX;
            var height = (int)Math.Ceiling(data.MaxY * scale) + 3 - startY;
            var mask = new byte[width * height];

            GlyphRasterizer.Rasterize(builder, width, height, -startX, -startY, aliased: false, mask);

            var emsPerPixel = 1f / scale;
            var sum = 0.0;
            var worst = 0.0;
            var misclassified = 0;

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var emX = (startX + px + 0.5f) * emsPerPixel;
                    var emY = (startY + py + 0.5f) * emsPerPixel;

                    var coverage = SlugReferenceEvaluator.Evaluate(
                        serializer.CurveTexels, serializer.BandTexels, in placement,
                        emX, emY, emsPerPixel, emsPerPixel);

                    var delta = Math.Abs(coverage - mask[py * width + px] / 255.0);

                    sum += delta;
                    worst = Math.Max(worst, delta);

                    if (delta > 0.5)
                    {
                        misclassified++;
                    }
                }
            }

            var mean = sum / (width * height);

            Assert.True(misclassified == 0 && mean <= meanGate && worst <= worstGate,
                FormattableString.Invariant(
                    $"mean {mean:0.00000} (gate {meanGate}), worst {worst:0.0000} (gate {worstGate}), misclassified {misclassified} of {width * height}"));
        }

        [Fact]
        public void Curved_Blobs_Match_The_Analytic_Rasterizer()
        {
            AssertMatchesRasterizer(sink =>
            {
                sink.BeginFigure(new Point(0, 0));
                sink.QuadraticBezierTo(new Point(2, 0.5), new Point(0.2, 1));
                sink.QuadraticBezierTo(new Point(-2, 1.5), new Point(0, 2));
                sink.LineTo(new Point(1, 2));
                sink.QuadraticBezierTo(new Point(3, 1), new Point(1.2, 0.2));
                sink.EndFigure(true);
            }, meanGate: 0.02, worstGate: 0.5);
        }

        [Fact]
        public void Dense_Polygons_Match_The_Analytic_Rasterizer()
        {
            AssertMatchesRasterizer(sink =>
            {
                sink.BeginFigure(Vertex(0));

                for (var i = 1; i < 48; i++)
                {
                    sink.LineTo(Vertex(i));
                }

                sink.EndFigure(true);

                static Point Vertex(int i)
                {
                    var angle = Math.PI * i / 24;
                    var radius = i % 2 == 0 ? 1.0 : 0.55;

                    return new Point(radius * Math.Cos(angle), radius * Math.Sin(angle));
                }
            }, meanGate: 0.02, worstGate: 0.5);
        }

        [Fact]
        public void Holes_Follow_Nonzero_Winding()
        {
            AssertMatchesRasterizer(sink =>
            {
                // Outer counter-clockwise, inner clockwise: the inner square is a hole.
                sink.BeginFigure(new Point(0, 0));
                sink.LineTo(new Point(2, 0));
                sink.LineTo(new Point(2, 2));
                sink.LineTo(new Point(0, 2));
                sink.EndFigure(true);

                sink.BeginFigure(new Point(0.5, 0.5));
                sink.LineTo(new Point(0.5, 1.5));
                sink.LineTo(new Point(1.5, 1.5));
                sink.LineTo(new Point(1.5, 0.5));
                sink.EndFigure(true);
            }, meanGate: 0.01, worstGate: 0.5);
        }

        [Fact]
        public void Even_Odd_Overlap_Empties()
        {
            AssertMatchesRasterizer(sink =>
            {
                sink.SetFillRule(FillRule.EvenOdd);

                sink.BeginFigure(new Point(0, 0));
                sink.LineTo(new Point(1.2, 0));
                sink.LineTo(new Point(1.2, 1.2));
                sink.LineTo(new Point(0, 1.2));
                sink.EndFigure(true);

                sink.BeginFigure(new Point(0.6, 0.6));
                sink.LineTo(new Point(1.8, 0.6));
                sink.LineTo(new Point(1.8, 1.8));
                sink.LineTo(new Point(0.6, 1.8));
                sink.EndFigure(true);
            }, meanGate: 0.01, worstGate: 0.5);
        }

        [Fact]
        public void Samples_Far_Outside_The_Bounds_Are_Empty()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(1, 1), new Point(0, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink);
            var serializer = new SlugTexelSerializer();

            Assert.True(serializer.TryAdd(data!, out var placement));

            Assert.Equal(0f, SlugReferenceEvaluator.Evaluate(
                serializer.CurveTexels, serializer.BandTexels, in placement, -10f, -10f, 1f / 64, 1f / 64));
            Assert.Equal(0f, SlugReferenceEvaluator.Evaluate(
                serializer.CurveTexels, serializer.BandTexels, in placement, 10f, 10f, 1f / 64, 1f / 64));
        }
    }
}
