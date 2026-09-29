using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class CurveBoundsGeometryContextTests
    {
        [Fact]
        public void Quadratic_Extent_Stops_At_The_Curve_Not_The_Control_Point()
        {
            var context = new CurveBoundsGeometryContext();

            // The apex of this parabola is at y = 50, half way to the control point.
            context.BeginFigure(new Point(0, 0));
            context.QuadraticBezierTo(new Point(50, 100), new Point(100, 0));
            context.EndFigure(true);

            Assert.Equal(new GlyphBounds(0, 0, 100, 50), context.ToGlyphBounds());
        }

        [Fact]
        public void Cubic_Extent_Includes_Both_Interior_Extrema()
        {
            var context = new CurveBoundsGeometryContext();

            // B(t).y = 300 t (1 - t) (1 - 2 t) peaks at +-28.87 for t = 0.211 and 0.789.
            context.BeginFigure(new Point(0, 0));
            context.CubicBezierTo(new Point(30, 100), new Point(60, -100), new Point(90, 0));
            context.EndFigure(true);

            Assert.Equal(new GlyphBounds(0, -29, 90, 29), context.ToGlyphBounds());
        }

        [Fact]
        public void Lines_Report_Their_End_Points()
        {
            var context = new CurveBoundsGeometryContext();

            Assert.True(context.IsEmpty);

            context.BeginFigure(new Point(-10.5, 3));
            context.LineTo(new Point(20.25, -7));
            context.EndFigure(true);

            Assert.False(context.IsEmpty);
            Assert.Equal(new GlyphBounds(-11, -7, 21, 3), context.ToGlyphBounds());
        }
    }
}
