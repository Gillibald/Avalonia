using System;
using Avalonia.Media.Fonts.Tables.Glyf;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class OutlineEmboldenTests
    {
        // TrueType outer contours wind clockwise in y-up space.
        private static Point[] ClockwiseSquare(double min, double max) =>
            new[] { new Point(min, min), new Point(min, max), new Point(max, max), new Point(max, min) };

        private static Point[] CounterClockwiseSquare(double min, double max) =>
            new[] { new Point(min, min), new Point(max, min), new Point(max, max), new Point(min, max) };

        [Fact]
        public void Outer_Contour_Grows_By_Half_The_Strength_On_Every_Side()
        {
            var points = ClockwiseSquare(0, 100);

            OutlineEmbolden.Embolden(points, new[] { 3 }, 10, 10);

            AssertBounds(points, -5, -5, 105, 105);
        }

        [Fact]
        public void Counter_Clockwise_Outline_Grows_As_Well()
        {
            var points = CounterClockwiseSquare(0, 100);

            OutlineEmbolden.Embolden(points, new[] { 3 }, 10, 10);

            AssertBounds(points, -5, -5, 105, 105);
        }

        [Fact]
        public void Counter_Shrinks()
        {
            var outer = ClockwiseSquare(0, 100);
            var counter = CounterClockwiseSquare(30, 70);

            var points = new Point[8];
            outer.CopyTo(points, 0);
            counter.CopyTo(points, 4);

            OutlineEmbolden.Embolden(points, new[] { 3, 7 }, 10, 10);

            AssertBounds(points.AsSpan(0, 4), -5, -5, 105, 105);
            AssertBounds(points.AsSpan(4, 4), 35, 35, 65, 65);
        }

        [Fact]
        public void Strength_Can_Differ_Per_Axis()
        {
            var points = ClockwiseSquare(0, 100);

            OutlineEmbolden.Embolden(points, new[] { 3 }, 20, 0);

            AssertBounds(points, -10, 0, 110, 100);
        }

        [Fact]
        public void Coincident_Points_Move_Together()
        {
            var points = new[]
            {
                new Point(0, 0), new Point(0, 100), new Point(0, 100), new Point(100, 100), new Point(100, 0)
            };

            OutlineEmbolden.Embolden(points, new[] { 4 }, 10, 10);

            Assert.Equal(points[1], points[2]);
            AssertBounds(points, -5, -5, 105, 105);
        }

        [Fact]
        public void Outline_Without_Area_Is_Left_Unchanged()
        {
            var points = new[] { new Point(0, 0), new Point(100, 0), new Point(50, 0) };
            var original = (Point[])points.Clone();

            OutlineEmbolden.Embolden(points, new[] { 2 }, 10, 10);

            Assert.Equal(original, points);
        }

        [Fact]
        public void Malformed_Contour_Ends_Leave_The_Outline_Unchanged()
        {
            var points = ClockwiseSquare(0, 100);
            var original = (Point[])points.Clone();

            OutlineEmbolden.Embolden(points, new[] { 7 }, 10, 10);

            Assert.Equal(original, points);
        }

        private static void AssertBounds(ReadOnlySpan<Point> points, double xMin, double yMin, double xMax,
            double yMax)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

            foreach (var point in points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            Assert.Equal(xMin, minX, 6);
            Assert.Equal(yMin, minY, 6);
            Assert.Equal(xMax, maxX, 6);
            Assert.Equal(yMax, maxY, 6);
        }
    }
}
