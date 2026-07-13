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

        [Fact]
        public void Band_Lists_Are_Sorted_By_The_Descending_Hull_Maximum()
        {
            var data = SlugBandEncoder.Encode(BuildBlob())!;

            for (var b = 0; b < data.HorizontalBandCount; b++)
            {
                var band = data.GetHorizontalBand(b);

                for (var i = 1; i < band.Length; i++)
                {
                    var previous = data.GetCurve(band[i - 1]);
                    var current = data.GetCurve(band[i]);

                    Assert.True(
                        Math.Max(previous.X1, Math.Max(previous.X2, previous.X3)) >=
                        Math.Max(current.X1, Math.Max(current.X2, current.X3)),
                        $"Horizontal band {b} is not sorted at position {i}.");
                }
            }

            for (var b = 0; b < data.VerticalBandCount; b++)
            {
                var band = data.GetVerticalBand(b);

                for (var i = 1; i < band.Length; i++)
                {
                    var previous = data.GetCurve(band[i - 1]);
                    var current = data.GetCurve(band[i]);

                    Assert.True(
                        Math.Max(previous.Y1, Math.Max(previous.Y2, previous.Y3)) >=
                        Math.Max(current.Y1, Math.Max(current.Y2, current.Y3)),
                        $"Vertical band {b} is not sorted at position {i}.");
                }
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
