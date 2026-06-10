using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class ShapeRenderTests : SvgRenderTestBase
{
    public ShapeRenderTests() : base("Shapes")
    {
    }

    [Fact]
    public async Task Basic_Shapes()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <rect width="200" height="200" fill="white"/>
              <rect x="10" y="10" width="80" height="60" rx="10" fill="red" stroke="black" stroke-width="2"/>
              <circle cx="150" cy="40" r="30" fill="blue"/>
              <ellipse cx="50" cy="140" rx="40" ry="20" fill="green"/>
              <line x1="110" y1="110" x2="190" y2="190" stroke="purple" stroke-width="6" stroke-linecap="round"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Polygon_And_Polyline()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <polygon points="50,10 90,90 10,90" fill="orange" stroke="black" stroke-width="2"/>
              <polyline points="110,90 130,10 150,90 170,10" fill="none" stroke="teal" stroke-width="4" stroke-linejoin="round"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Path_With_Curves_And_Arcs()
    {
        // A heart built from two cubics plus a closed arc wedge — exercises
        // C, A, Z and relative commands together.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <path d="M 50 30 C 50 20 35 15 30 25 C 25 15 10 20 10 30 C 10 45 30 55 30 60 C 30 55 50 45 50 30 Z"
                    fill="crimson" transform="translate(10,10) scale(1.2)"/>
              <path d="M 150 50 L 150 15 A 35 35 0 0 1 185 50 Z" fill="gold" stroke="black" stroke-width="2"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task FillRule_EvenOdd_Vs_NonZero()
    {
        // The same self-intersecting star: nonzero fills the center, evenodd
        // leaves it hollow.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <path d="M 50 5 L 71 70 L 16 30 L 84 30 L 29 70 Z" fill="navy"/>
              <path d="M 150 5 L 171 70 L 116 30 L 184 30 L 129 70 Z" fill="navy" fill-rule="evenodd"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Stroke_Dasharray()
    {
        // Dash lengths are user units, independent of stroke width: both lines
        // must show the same 8-on/4-off pattern. The second line also offsets
        // the pattern by half a dash.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <line x1="10" y1="25" x2="190" y2="25" stroke="black" stroke-width="2" stroke-dasharray="8 4"/>
              <line x1="10" y1="50" x2="190" y2="50" stroke="black" stroke-width="8" stroke-dasharray="8 4"/>
              <line x1="10" y1="75" x2="190" y2="75" stroke="black" stroke-width="8" stroke-dasharray="8 4" stroke-dashoffset="4"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task ViewBox_Meet_Letterboxes()
    {
        // A square viewBox in a wide viewport: uniform scale, centered
        // horizontally, background visible left and right.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 100 100">
              <rect width="100" height="100" fill="lightgray"/>
              <circle cx="50" cy="50" r="40" fill="seagreen"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Nested_Transforms()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <rect width="200" height="200" fill="white"/>
              <g transform="translate(100,100)">
                <g transform="rotate(45)">
                  <rect x="-40" y="-40" width="80" height="80" fill="steelblue"/>
                  <rect x="-20" y="-20" width="40" height="40" fill="white" transform="rotate(45)"/>
                </g>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Inherited_Styles_And_CurrentColor()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" color="darkorange">
              <rect width="200" height="100" fill="white"/>
              <g fill="currentColor" stroke="black" stroke-width="2">
                <rect x="10" y="10" width="60" height="60"/>
                <circle cx="120" cy="40" r="30" fill="purple"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }
}
