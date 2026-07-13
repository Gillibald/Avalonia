using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugContourSinkTests
    {
        [Fact]
        public void Quadratics_Pass_Through_And_The_Contour_Closes_With_A_Line()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(1, 1), new Point(2, 0));
            sink.EndFigure(true);

            Assert.Equal(1, sink.ContourCount);
            Assert.Equal(2, sink.GetCurveCount(0));

            var quad = sink.GetCurve(0, 0);

            Assert.Equal((0f, 0f, 1f, 1f, 2f, 0f), (quad.X1, quad.Y1, quad.X2, quad.Y2, quad.X3, quad.Y3));

            var closer = sink.GetCurve(0, 1);

            Assert.Equal((2f, 0f, 0f, 0f, 0f, 0f),
                (closer.X1, closer.Y1, closer.X2, closer.Y2, closer.X3, closer.Y3));
        }

        [Fact]
        public void Lines_Degenerate_To_Quads_With_A_Duplicated_Endpoint()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.LineTo(new Point(0, 1));
            sink.EndFigure(true);

            Assert.Equal(1, sink.ContourCount);
            Assert.Equal(3, sink.GetCurveCount(0));

            for (var i = 0; i < 3; i++)
            {
                var curve = sink.GetCurve(0, i);

                Assert.Equal(curve.X3, curve.X2);
                Assert.Equal(curve.Y3, curve.Y2);
            }
        }

        [Fact]
        public void The_Closing_Line_Is_Omitted_When_The_Pen_Returns_To_The_Start()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.LineTo(new Point(0, 1));
            sink.LineTo(new Point(0, 0));
            sink.EndFigure(true);

            Assert.Equal(1, sink.ContourCount);
            Assert.Equal(3, sink.GetCurveCount(0));
        }

        [Fact]
        public void An_Elevated_Quadratic_Cubic_Collapses_To_A_Single_Quad()
        {
            // The degree elevation of the quadratic {(0,0), (1,1), (2,0)} has a zero third
            // difference, so the sink recovers the original control point without splitting.
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.CubicBezierTo(new Point(2.0 / 3, 2.0 / 3), new Point(4.0 / 3, 2.0 / 3), new Point(2, 0));
            sink.EndFigure(true);

            Assert.Equal(2, sink.GetCurveCount(0));

            var quad = sink.GetCurve(0, 0);

            Assert.Equal(1, quad.X2, 1e-6);
            Assert.Equal(1, quad.Y2, 1e-6);
            Assert.Equal(2f, quad.X3);
            Assert.Equal(0f, quad.Y3);
        }

        [Theory]
        [InlineData(1.0 / 64, 2)]
        [InlineData(1.0 / 1024, 8)]
        public void Cubic_Subdivision_Depth_Follows_The_Flatten_Tolerance(double tolerance, int expectedQuads)
        {
            // Cup cubic (0,0)-(0,1)-(1,1)-(1,0): third difference (-2, 0), single-quad error
            // bound sqrt(3)/36 * 2 ~ 0.0962, dividing by 8 per split.
            var sink = new SlugContourSink(tolerance);

            sink.BeginFigure(new Point(0, 0));
            sink.CubicBezierTo(new Point(0, 1), new Point(1, 1), new Point(1, 0));
            sink.EndFigure(true);

            Assert.Equal(expectedQuads + 1, sink.GetCurveCount(0));
        }

        [Fact]
        public void Flattened_Cubics_Stay_Within_The_Tolerance_Of_The_Source_Curve()
        {
            const double tolerance = 1.0 / 256;

            var sink = new SlugContourSink(tolerance);

            sink.BeginFigure(new Point(0, 0));
            sink.CubicBezierTo(new Point(0, 1), new Point(1, 1), new Point(1, 0));
            sink.EndFigure(true);

            var quadCount = sink.GetCurveCount(0) - 1;
            var worst = 0.0;

            // Uniform halving assigns quad j the cubic's parameter range [j/n, (j+1)/n], so the
            // deviation can be checked pointwise at matched parameters.
            for (var j = 0; j < quadCount; j++)
            {
                var quad = sink.GetCurve(0, j);

                for (var step = 0; step <= 256; step++)
                {
                    var u = step / 256.0;
                    var t = (j + u) / quadCount;

                    var qx = (1 - u) * (1 - u) * quad.X1 + 2 * u * (1 - u) * quad.X2 + u * u * quad.X3;
                    var qy = (1 - u) * (1 - u) * quad.Y1 + 2 * u * (1 - u) * quad.Y2 + u * u * quad.Y3;

                    var s = 1 - t;
                    var cx = s * s * s * 0 + 3 * s * s * t * 0 + 3 * s * t * t * 1 + t * t * t * 1;
                    var cy = s * s * s * 0 + 3 * s * s * t * 1 + 3 * s * t * t * 1 + t * t * t * 0;

                    worst = Math.Max(worst, Math.Sqrt((qx - cx) * (qx - cx) + (qy - cy) * (qy - cy)));
                }
            }

            Assert.True(worst <= tolerance * 1.0001 + 1e-6, $"Deviation {worst} exceeds tolerance {tolerance}.");
        }

        [Fact]
        public void Empty_Figures_And_Zero_Length_Segments_Are_Dropped()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(1, 1));
            sink.EndFigure(true);

            Assert.Equal(0, sink.ContourCount);

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(0, 0));
            sink.EndFigure(true);

            Assert.Equal(0, sink.ContourCount);

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.LineTo(new Point(1, 0));
            sink.EndFigure(true);

            Assert.Equal(1, sink.ContourCount);
            Assert.Equal(2, sink.GetCurveCount(0));
        }

        [Fact]
        public void A_Dangling_Figure_Is_Closed_When_The_Next_One_Begins()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.BeginFigure(new Point(5, 5));
            sink.LineTo(new Point(6, 5));
            sink.EndFigure(true);

            Assert.Equal(2, sink.ContourCount);
            Assert.Equal(2, sink.GetCurveCount(0));
            Assert.Equal(2, sink.GetCurveCount(1));

            var closer = sink.GetCurve(0, 1);

            Assert.Equal((1f, 0f, 0f, 0f, 0f, 0f),
                (closer.X1, closer.Y1, closer.X2, closer.Y2, closer.X3, closer.Y3));
        }

        [Fact]
        public void Reset_Clears_Contours_And_Restores_The_Nonzero_Fill_Rule()
        {
            var sink = new SlugContourSink();

            sink.SetFillRule(FillRule.EvenOdd);
            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.LineTo(new Point(0, 1));
            sink.EndFigure(true);

            sink.Reset();

            Assert.Equal(0, sink.ContourCount);
            Assert.Equal(0, sink.TotalCurveCount);
            Assert.Equal(FillRule.NonZero, sink.FillRule);

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(2, 0));
            sink.LineTo(new Point(0, 2));
            sink.EndFigure(true);

            Assert.Equal(1, sink.ContourCount);
            Assert.Equal(3, sink.GetCurveCount(0));
        }

        [Fact]
        public void Curve_Endpoints_Chain_Exactly_Across_The_Contour()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.CubicBezierTo(new Point(0, 1), new Point(1, 1), new Point(1, 0));
            sink.EndFigure(true);

            var count = sink.GetCurveCount(0);

            for (var i = 0; i < count; i++)
            {
                var curve = sink.GetCurve(0, i);
                var next = sink.GetCurve(0, (i + 1) % count);

                Assert.Equal(curve.X3, next.X1);
                Assert.Equal(curve.Y3, next.Y1);
            }

            // A single-curve loop wraps onto itself.
            sink.Reset();
            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(1, 1), new Point(0, 0));
            sink.EndFigure(true);

            Assert.Equal(1, sink.GetCurveCount(0));

            var loop = sink.GetCurve(0, 0);

            Assert.Equal((0f, 0f, 0f, 0f), (loop.X1, loop.Y1, loop.X3, loop.Y3));
        }

        [Fact]
        public void Arcs_Degrade_To_Straight_Segments()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.ArcTo(new Point(1, 0), new Size(1, 1), 0, false, SweepDirection.Clockwise);
            sink.EndFigure(true);

            Assert.Equal(2, sink.GetCurveCount(0));

            var curve = sink.GetCurve(0, 0);

            Assert.Equal((0f, 0f, 1f, 0f, 1f, 0f),
                (curve.X1, curve.Y1, curve.X2, curve.Y2, curve.X3, curve.Y3));
        }
    }
}
