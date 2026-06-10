using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class UseSymbolRenderTests : SvgRenderTestBase
{
    public UseSymbolRenderTests() : base("UseSymbol")
    {
    }

    [Fact]
    public async Task Symbol_Reused_At_Different_Sizes()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="120">
              <defs>
                <symbol id="star" viewBox="0 0 100 100">
                  <path d="M 50 5 L 61 38 L 95 38 L 68 59 L 79 92 L 50 72 L 21 92 L 32 59 L 5 38 L 39 38 Z"
                        fill="goldenrod" stroke="black" stroke-width="3"/>
                </symbol>
              </defs>
              <rect width="200" height="120" fill="white"/>
              <use href="#star" x="10" y="10" width="100" height="100"/>
              <use href="#star" x="120" y="10" width="50" height="50"/>
              <use href="#star" x="120" y="70" width="40" height="40" opacity="0.4"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Use_Of_Group_With_Transform()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <g id="pair">
                  <rect width="30" height="30" fill="teal"/>
                  <circle cx="55" cy="15" r="15" fill="crimson"/>
                </g>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <use href="#pair" x="10" y="10"/>
              <use href="#pair" x="10" y="55" transform="translate(100,0)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task ClipPath_Circle_Clips_Group()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <defs>
                <clipPath id="hole">
                  <circle cx="100" cy="100" r="70"/>
                </clipPath>
              </defs>
              <rect width="200" height="200" fill="white"/>
              <g clip-path="url(#hole)">
                <rect width="100" height="100" fill="coral"/>
                <rect x="100" width="100" height="100" fill="steelblue"/>
                <rect y="100" width="100" height="100" fill="seagreen"/>
                <rect x="100" y="100" width="100" height="100" fill="orchid"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task ClipPath_ObjectBoundingBox()
    {
        // The unit-space half-box clip leaves only the left half of the rect.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <clipPath id="half" clipPathUnits="objectBoundingBox">
                  <rect width="0.5" height="1"/>
                </clipPath>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="20" y="10" width="160" height="80" fill="darkcyan" clip-path="url(#half)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }
}
