using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class GradientRenderTests : SvgRenderTestBase
{
    public GradientRenderTests() : base("Gradients")
    {
    }

    [Fact]
    public async Task Linear_ObjectBoundingBox()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <linearGradient id="g">
                  <stop offset="0" stop-color="red"/>
                  <stop offset="1" stop-color="blue"/>
                </linearGradient>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="180" height="80" fill="url(#g)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Linear_GradientTransform_Rotates_In_Box_Space()
    {
        // rotate(90, 0.5, 0.5) in unit-box space turns the horizontal gradient
        // vertical — exactly, even though the box is non-square.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <linearGradient id="g" gradientTransform="rotate(90, 0.5, 0.5)">
                  <stop offset="0" stop-color="red"/>
                  <stop offset="1" stop-color="blue"/>
                </linearGradient>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="180" height="80" fill="url(#g)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Linear_SpreadMethods()
    {
        // A narrow userSpace gradient span: pad clamps, reflect mirrors,
        // repeat tiles.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="120">
              <defs>
                <linearGradient id="pad" gradientUnits="userSpaceOnUse" x1="80" x2="120" spreadMethod="pad">
                  <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
                </linearGradient>
                <linearGradient id="reflect" gradientUnits="userSpaceOnUse" x1="80" x2="120" spreadMethod="reflect">
                  <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
                </linearGradient>
                <linearGradient id="repeat" gradientUnits="userSpaceOnUse" x1="80" x2="120" spreadMethod="repeat">
                  <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
                </linearGradient>
              </defs>
              <rect width="200" height="120" fill="white"/>
              <rect x="10" y="10" width="180" height="30" fill="url(#pad)"/>
              <rect x="10" y="45" width="180" height="30" fill="url(#reflect)"/>
              <rect x="10" y="80" width="180" height="30" fill="url(#repeat)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Radial_With_Focal_Offset()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <defs>
                <radialGradient id="g" fx="0.3" fy="0.3">
                  <stop offset="0" stop-color="white"/>
                  <stop offset="1" stop-color="darkblue"/>
                </radialGradient>
              </defs>
              <rect width="200" height="200" fill="white"/>
              <circle cx="100" cy="100" r="90" fill="url(#g)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Gradient_Stop_Opacity_Fades_Over_Background()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <linearGradient id="g">
                  <stop offset="0" stop-color="purple"/>
                  <stop offset="1" stop-color="purple" stop-opacity="0"/>
                </linearGradient>
              </defs>
              <rect width="200" height="100" fill="gold"/>
              <rect width="200" height="100" fill="url(#g)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Gradient_Href_Inheritance()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <linearGradient id="base">
                  <stop offset="0" stop-color="orange"/>
                  <stop offset="1" stop-color="green"/>
                </linearGradient>
                <linearGradient id="vertical" href="#base" x2="0" y2="1"/>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="85" height="80" fill="url(#base)"/>
              <rect x="105" y="10" width="85" height="80" fill="url(#vertical)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }
}
