using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.Slug
{
    public class SlugBandEncoderTests
    {
        private static SlugContourSink BuildBlob()
        {
            // An irregular outline mixing quads and lines across both axes.
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(2, 0.5), new Point(0.2, 1));
            sink.QuadraticBezierTo(new Point(-2, 1.5), new Point(0, 2));
            sink.LineTo(new Point(1, 2));
            sink.QuadraticBezierTo(new Point(3, 1), new Point(1.2, 0.2));
            sink.EndFigure(true);

            return sink;
        }

        private static (double MinX, double MaxX, double MinY, double MaxY) SampleExtent(SlugQuadCurve curve)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            for (var i = 0; i <= 2048; i++)
            {
                var t = i / 2048.0;
                var s = 1 - t;
                var x = s * s * curve.X1 + 2 * t * s * curve.X2 + t * t * curve.X3;
                var y = s * s * curve.Y1 + 2 * t * s * curve.Y2 + t * t * curve.Y3;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            return (minX, maxX, minY, maxY);
        }

        private static bool Contains(ReadOnlySpan<int> band, int ordinal)
        {
            foreach (var entry in band)
            {
                if (entry == ordinal)
                {
                    return true;
                }
            }

            return false;
        }

        [Fact]
        public void Encode_Returns_Null_For_An_Empty_Outline()
        {
            Assert.Null(SlugBandEncoder.Encode(new SlugContourSink()));
        }

        [Fact]
        public void Every_Curve_Lands_In_Every_Band_Its_Extent_Overlaps()
        {
            const double slack = 1e-5;

            var data = SlugBandEncoder.Encode(BuildBlob())!;

            var hSize = (data.MaxY - data.MinY) / data.HorizontalBandCount;
            var vSize = (data.MaxX - data.MinX) / data.VerticalBandCount;

            for (var ordinal = 0; ordinal < data.TotalCurveCount; ordinal++)
            {
                var curve = data.GetCurve(ordinal);
                var extent = SampleExtent(curve);
                var isHorizontalLine = curve.Y1 == curve.Y2 && curve.Y2 == curve.Y3;
                var isVerticalLine = curve.X1 == curve.X2 && curve.X2 == curve.X3;

                for (var b = 0; b < data.HorizontalBandCount; b++)
                {
                    var lo = data.MinY + b * hSize - SlugBandEncoder.BandEpsilon;
                    var hi = data.MinY + (b + 1) * hSize + SlugBandEncoder.BandEpsilon;
                    var member = Contains(data.GetHorizontalBand(b), ordinal);

                    if (isHorizontalLine)
                    {
                        Assert.False(member);
                    }
                    else if (extent.MaxY >= lo + slack && extent.MinY <= hi - slack)
                    {
                        Assert.True(member, $"Curve {ordinal} missing from horizontal band {b}.");
                    }
                    else if (extent.MaxY < lo - slack || extent.MinY > hi + slack)
                    {
                        Assert.False(member, $"Curve {ordinal} misassigned to horizontal band {b}.");
                    }
                }

                for (var b = 0; b < data.VerticalBandCount; b++)
                {
                    var lo = data.MinX + b * vSize - SlugBandEncoder.BandEpsilon;
                    var hi = data.MinX + (b + 1) * vSize + SlugBandEncoder.BandEpsilon;
                    var member = Contains(data.GetVerticalBand(b), ordinal);

                    if (isVerticalLine)
                    {
                        Assert.False(member);
                    }
                    else if (extent.MaxX >= lo + slack && extent.MinX <= hi - slack)
                    {
                        Assert.True(member, $"Curve {ordinal} missing from vertical band {b}.");
                    }
                    else if (extent.MaxX < lo - slack || extent.MinX > hi + slack)
                    {
                        Assert.False(member, $"Curve {ordinal} misassigned to vertical band {b}.");
                    }
                }
            }
        }

        private static (float Min, float Max) TexelHull(float p1, float p2, float p3)
        {
            var q1 = (float)(Half)p1;
            var q2 = (float)(Half)p2;
            var q3 = (float)(Half)p3;

            return (Math.Min(q1, Math.Min(q2, q3)), Math.Max(q1, Math.Max(q2, q3)));
        }

        private static void AssertSegmented(SlugGlyphData data, ReadOnlySpan<int> band,
            (int ForwardOnly, int Shared, int BackwardOnly) segments, float split, bool horizontal, string label)
        {
            Assert.Equal(band.Length, segments.ForwardOnly + segments.Shared + segments.BackwardOnly);

            for (var i = 0; i < band.Length; i++)
            {
                var curve = data.GetCurve(band[i]);
                var (lo, hi) = horizontal
                    ? TexelHull(curve.X1, curve.X2, curve.X3)
                    : TexelHull(curve.Y1, curve.Y2, curve.Y3);
                var forward = hi >= split - SlugBandEncoder.SplitMargin;
                var backward = lo <= split + SlugBandEncoder.SplitMargin;
                var expectedSegment = i < segments.ForwardOnly ? 0 : i < segments.ForwardOnly + segments.Shared ? 1 : 2;
                var actualSegment = forward ? (backward ? 1 : 0) : 2;

                Assert.True(forward || backward, $"{label}: entry {i} is on neither side of the split.");
                Assert.True(expectedSegment == actualSegment, $"{label}: entry {i} sits in the wrong segment.");

                if (i > 0 && i != segments.ForwardOnly && i != segments.ForwardOnly + segments.Shared)
                {
                    var previous = data.GetCurve(band[i - 1]);
                    var previousKey = horizontal
                        ? Math.Max(previous.X1, Math.Max(previous.X2, previous.X3))
                        : Math.Max(previous.Y1, Math.Max(previous.Y2, previous.Y3));
                    var key = horizontal
                        ? Math.Max(curve.X1, Math.Max(curve.X2, curve.X3))
                        : Math.Max(curve.Y1, Math.Max(curve.Y2, curve.Y3));

                    Assert.True(previousKey >= key, $"{label}: segment is not sorted at position {i}.");
                }
            }
        }

        [Fact]
        public void Band_Lists_Are_Segmented_Around_The_Split_And_Sorted_Within_Each_Segment()
        {
            var data = SlugBandEncoder.Encode(BuildBlob())!;

            Assert.Equal((data.MinX + data.MaxX) * 0.5f, data.HorizontalSplit);
            Assert.Equal((data.MinY + data.MaxY) * 0.5f, data.VerticalSplit);

            for (var b = 0; b < data.HorizontalBandCount; b++)
            {
                AssertSegmented(data, data.GetHorizontalBand(b), data.GetHorizontalSegments(b),
                    data.HorizontalSplit, horizontal: true, $"Horizontal band {b}");
            }

            for (var b = 0; b < data.VerticalBandCount; b++)
            {
                AssertSegmented(data, data.GetVerticalBand(b), data.GetVerticalSegments(b),
                    data.VerticalSplit, horizontal: false, $"Vertical band {b}");
            }
        }

        [Fact]
        public void A_Curve_On_A_Band_Edge_Joins_Both_Neighbors()
        {
            var sink = new SlugContourSink();

            // A frame fixing the bounds to y in [0, 2] ...
            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(0.1, 0));
            sink.LineTo(new Point(0.1, 2));
            sink.LineTo(new Point(0, 2));
            sink.EndFigure(true);

            // ... and a segment whose maximum y is exactly the two-band edge at 1.
            sink.BeginFigure(new Point(0.5, 0.5));
            sink.LineTo(new Point(0.6, 1.0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink, horizontalBandCount: 2)!;

            Assert.Equal(2, data.HorizontalBandCount);
            Assert.True(Contains(data.GetHorizontalBand(0), 4));
            Assert.True(Contains(data.GetHorizontalBand(1), 4));
        }

        [Fact]
        public void Parallel_Lines_Stay_Out_Of_Their_Parallel_Bands()
        {
            var sink = new SlugContourSink();

            // Rectangle: ordinals 0 = bottom, 1 = right, 2 = top, 3 = left (the closer).
            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(2, 0));
            sink.LineTo(new Point(2, 1));
            sink.LineTo(new Point(0, 1));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink)!;

            // Both verticals span the full y extent, so every horizontal band holds exactly them.
            Assert.True(Contains(data.GetHorizontalBand(0), 1));
            Assert.True(Contains(data.GetHorizontalBand(0), 3));
            Assert.True(Contains(data.GetVerticalBand(0), 0));
            Assert.True(Contains(data.GetVerticalBand(0), 2));

            for (var b = 0; b < data.HorizontalBandCount; b++)
            {
                foreach (var ordinal in data.GetHorizontalBand(b))
                {
                    Assert.True(ordinal == 1 || ordinal == 3, "Horizontal band holds a horizontal line.");
                }
            }

            for (var b = 0; b < data.VerticalBandCount; b++)
            {
                foreach (var ordinal in data.GetVerticalBand(b))
                {
                    Assert.True(ordinal == 0 || ordinal == 2, "Vertical band holds a vertical line.");
                }
            }
        }

        [Fact]
        public void Assignment_Uses_Exact_Extents_Not_The_Control_Hull()
        {
            var sink = new SlugContourSink();

            // The control point pushes the hull to y = 10, but the curve itself peaks at y = 5.
            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(0.5, 10), new Point(1, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink, horizontalBandCount: 4)!;

            Assert.Equal(10, data.MaxY);
            Assert.True(Contains(data.GetHorizontalBand(0), 0));
            Assert.True(Contains(data.GetHorizontalBand(1), 0));
            Assert.True(Contains(data.GetHorizontalBand(2), 0));
            Assert.False(Contains(data.GetHorizontalBand(3), 0));
        }

        [Fact]
        public void A_Flat_Outline_Collapses_To_One_Empty_Horizontal_Band()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.LineTo(new Point(1, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink)!;

            Assert.Equal(1, data.HorizontalBandCount);
            Assert.Equal(0, data.GetHorizontalBand(0).Length);
            Assert.Equal(1, data.VerticalBandCount);
            Assert.True(Contains(data.GetVerticalBand(0), 0));
            Assert.True(Contains(data.GetVerticalBand(0), 1));
        }

        [Fact]
        public void A_Single_Curve_Loop_Gets_One_Band_Per_Axis()
        {
            var sink = new SlugContourSink();

            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(1, 1), new Point(0, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink)!;

            Assert.Equal(1, data.HorizontalBandCount);
            Assert.Equal(1, data.VerticalBandCount);
            Assert.True(Contains(data.GetHorizontalBand(0), 0));
            Assert.True(Contains(data.GetVerticalBand(0), 0));
        }

        [Fact]
        public void Bounds_And_Fill_Rule_Pass_Through()
        {
            var sink = new SlugContourSink();

            sink.SetFillRule(FillRule.EvenOdd);
            sink.BeginFigure(new Point(0, 0));
            sink.QuadraticBezierTo(new Point(1, 1), new Point(0, 0));
            sink.EndFigure(true);

            var data = SlugBandEncoder.Encode(sink)!;

            Assert.Equal(FillRule.EvenOdd, data.FillRule);
            Assert.Equal(0, data.MinX);
            Assert.Equal(0, data.MinY);
            Assert.Equal(1, data.MaxX);
            Assert.Equal(1, data.MaxY);
        }
    }
}
