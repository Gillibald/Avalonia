using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization
{
    public class GlyphSimulationTests
    {
        private const float Tolerance = 1e-4f;

        [Fact]
        public void Oblique_Moves_A_Point_Above_The_Baseline_Forward_In_Y_Up_Space()
        {
            var points = new[] { 10f, 100f, 10f, 0f, 10f, -50f };

            GlyphSimulation.Slant(points, yDown: false);

            Assert.Equal(40f, points[0], Tolerance);
            Assert.Equal(100f, points[1], Tolerance);
            Assert.Equal(10f, points[2], Tolerance);
            Assert.Equal(-5f, points[4], Tolerance);
        }

        [Fact]
        public void Oblique_Moves_A_Point_Above_The_Baseline_Forward_In_Y_Down_Space()
        {
            // y-down: -100 is above the baseline.
            var points = new[] { 10f, -100f };

            GlyphSimulation.Slant(points, yDown: true);

            Assert.Equal(40f, points[0], Tolerance);
            Assert.Equal(-100f, points[1], Tolerance);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Embolden_Outsets_A_Square_On_All_Sides(bool clockwise)
        {
            var path = Square(0, 0, 10, 10, clockwise);

            GlyphSimulation.Embolden(path.Verbs, path.WritablePoints, 0.5f);

            Assert.True(path.TryGetPointBounds(out var minX, out var minY, out var maxX, out var maxY));
            Assert.Equal(-0.5f, minX, Tolerance);
            Assert.Equal(-0.5f, minY, Tolerance);
            Assert.Equal(10.5f, maxX, Tolerance);
            Assert.Equal(10.5f, maxY, Tolerance);

            // Every corner moves diagonally outwards to the miter point.
            var points = path.Points;

            for (var i = 0; i < points.Length; i += 2)
            {
                Assert.True(points[i] == -0.5f || points[i] == 10.5f, $"x {points[i]}");
                Assert.True(points[i + 1] == -0.5f || points[i + 1] == 10.5f, $"y {points[i + 1]}");
            }
        }

        [Fact]
        public void Embolden_Shrinks_A_Counter_While_Growing_Its_Ink()
        {
            // An outer contour and an oppositely wound hole, as a font draws an 'o'.
            var path = new GlyphPathBuilder();
            AddSquare(path, 0, 0, 20, 20, clockwise: false);
            AddSquare(path, 5, 5, 15, 15, clockwise: true);

            GlyphSimulation.Embolden(path.Verbs, path.WritablePoints, 1f);

            var points = path.Points;

            // Outer square: 4 points, then the hole's 4 points.
            for (var i = 0; i < 8; i += 2)
            {
                Assert.True(points[i] == -1f || points[i] == 21f, $"outer x {points[i]}");
            }

            for (var i = 8; i < 16; i += 2)
            {
                Assert.True(points[i] == 6f || points[i] == 14f, $"inner x {points[i]}");
                Assert.True(points[i + 1] == 6f || points[i + 1] == 14f, $"inner y {points[i + 1]}");
            }
        }

        [Fact]
        public void Embolden_Leaves_A_Degenerate_Outline_Untouched()
        {
            var path = new GlyphPathBuilder();
            path.BeginFigure(new Point(1, 1));
            path.LineTo(new Point(5, 1));
            path.EndFigure(true);

            GlyphSimulation.Embolden(path.Verbs, path.WritablePoints, 1f);

            Assert.Equal(new[] { 1f, 1f, 5f, 1f }, path.Points.ToArray());
        }

        [Theory]
        [InlineData(4f, 1f / 24)]
        [InlineData(9f, 1f / 24)]
        [InlineData(22.5f, (1f / 24 + 1f / 32) / 2)]
        [InlineData(36f, 1f / 32)]
        [InlineData(200f, 1f / 32)]
        public void Bold_Stroke_Ratio_Interpolates_Between_The_Anchor_Sizes(float emSize, float expected)
        {
            Assert.Equal(expected, GlyphSimulation.GetBoldStrokeRatio(emSize), 1e-6f);
        }

        [Fact]
        public void Embolden_Outset_Is_Half_The_Stroke()
        {
            Assert.Equal(64f / 32 / 2, GlyphSimulation.GetEmboldenOutset(FontSimulations.Bold, 64f), Tolerance);
            Assert.Equal(0f, GlyphSimulation.GetEmboldenOutset(FontSimulations.Oblique, 64f));
        }

        [Fact]
        public void Apply_Unstretches_A_Subpixel_Path_Around_The_Simulation()
        {
            // A 3x horizontally stretched square: the outset lands as 3x in x after restretching,
            // and the slant is that of the square-pixel glyph.
            var path = new GlyphPathBuilder();
            AddSquare(path, 0, -10, 30, 0, clockwise: false);

            GlyphSimulation.Apply(path, FontSimulations.Bold | FontSimulations.Oblique, 0.5f, yDown: true, xScale: 3f);

            Assert.True(path.TryGetPointBounds(out var minX, out var minY, out var maxX, out var maxY));
            Assert.Equal(-10.5f, minY, Tolerance);
            Assert.Equal(0.5f, maxY, Tolerance);

            // Bottom-left corner (-0.5, 0.5) slants to x = -0.5 - 0.3 * 0.5, top-right (10.5, -10.5)
            // to 10.5 + 0.3 * 10.5, both scaled back by 3.
            Assert.Equal((-0.5f - 0.15f) * 3, minX, 1e-3f);
            Assert.Equal((10.5f + 3.15f) * 3, maxX, 1e-3f);
        }

        private static GlyphPathBuilder Square(float x0, float y0, float x1, float y1, bool clockwise)
        {
            var path = new GlyphPathBuilder();
            AddSquare(path, x0, y0, x1, y1, clockwise);
            return path;
        }

        private static void AddSquare(GlyphPathBuilder path, float x0, float y0, float x1, float y1, bool clockwise)
        {
            // Counterclockwise in a y-up frame: (x0,y0) -> (x1,y0) -> (x1,y1) -> (x0,y1).
            path.BeginFigure(new Point(x0, y0));

            if (clockwise)
            {
                path.LineTo(new Point(x0, y1));
                path.LineTo(new Point(x1, y1));
                path.LineTo(new Point(x1, y0));
            }
            else
            {
                path.LineTo(new Point(x1, y0));
                path.LineTo(new Point(x1, y1));
                path.LineTo(new Point(x0, y1));
            }

            path.EndFigure(true);
        }
    }
}
