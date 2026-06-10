using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class CompositingRenderTests : SvgRenderTestBase
{
    public CompositingRenderTests() : base("Compositing")
    {
    }

    [Fact]
    public async Task Group_Opacity_Differs_From_Fill_Opacity()
    {
        // Left: fill-opacity blends each disk with the backdrop independently —
        // the overlap is darker. Right: group opacity composites the overlapping
        // opaque disks as a unit — uniform tint.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <g fill="crimson" fill-opacity="0.5">
                <circle cx="40" cy="50" r="28"/>
                <circle cx="65" cy="50" r="28"/>
              </g>
              <g fill="crimson" opacity="0.5">
                <circle cx="135" cy="50" r="28"/>
                <circle cx="160" cy="50" r="28"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Mix_Blend_Mode_With_Isolation()
    {
        // Left: multiply blends with the yellow backdrop. Right: the isolated
        // group bounds the blend, so the circle keeps its own color.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="20" width="80" height="40" fill="gold"/>
              <circle cx="50" cy="60" r="25" fill="magenta" style="mix-blend-mode: multiply"/>
              <rect x="110" y="20" width="80" height="40" fill="gold"/>
              <g isolation="isolate">
                <circle cx="150" cy="60" r="25" fill="magenta" style="mix-blend-mode: multiply"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Paint_Order_Stroke_First()
    {
        // Normal order paints the stroke over the fill (thick stroke straddles
        // the edge); stroke-first tucks the stroke's inner half under the fill.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="200" height="100" fill="white"/>
              <circle cx="50" cy="50" r="30" fill="gold" stroke="darkblue" stroke-width="14"/>
              <circle cx="150" cy="50" r="30" fill="gold" stroke="darkblue" stroke-width="14" paint-order="stroke fill"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }
}
