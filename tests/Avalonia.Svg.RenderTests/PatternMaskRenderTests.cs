using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class PatternMaskRenderTests : SvgRenderTestBase
{
    public PatternMaskRenderTests() : base("PatternsAndMasks")
    {
    }

    [Fact]
    public async Task Pattern_Checkerboard_ObjectBoundingBox()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <pattern id="checker" width="0.2" height="0.4">
                  <rect width="10" height="10" fill="midnightblue"/>
                  <rect x="10" y="10" width="10" height="10" fill="midnightblue"/>
                  <rect x="10" width="10" height="10" fill="skyblue"/>
                  <rect y="10" width="10" height="10" fill="skyblue"/>
                </pattern>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="100" height="50" fill="url(#checker)" stroke="black"/>
              <circle cx="160" cy="55" r="35" fill="url(#checker)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Pattern_UserSpace_With_Transform()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <pattern id="stripes" width="10" height="10" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
                  <rect width="5" height="10" fill="seagreen"/>
                </pattern>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="180" height="80" fill="url(#stripes)" stroke="black"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Pattern_With_ViewBox_Scales_Content()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <pattern id="p" width="0.25" height="0.5" viewBox="0 0 10 10">
                  <circle cx="5" cy="5" r="4" fill="indigo"/>
                </pattern>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="10" y="10" width="180" height="80" fill="url(#p)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Mask_Luminance_Gradient_Fade()
    {
        // The default luminance mask: white passes, black hides — the red bar
        // fades out along the gradient.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <linearGradient id="fade">
                  <stop offset="0" stop-color="white"/>
                  <stop offset="1" stop-color="black"/>
                </linearGradient>
                <mask id="m">
                  <rect x="10" y="10" width="180" height="80" fill="url(#fade)"/>
                </mask>
              </defs>
              <rect width="200" height="100" fill="lightgray"/>
              <rect x="10" y="10" width="180" height="80" fill="crimson" mask="url(#m)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Mask_Alpha_Mode()
    {
        // mask-type="alpha": a fully-opaque green rect passes everything where it
        // covers, regardless of its luminance; the uncovered half hides.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <mask id="m" mask-type="alpha">
                  <rect x="10" y="10" width="90" height="80" fill="green"/>
                </mask>
              </defs>
              <rect width="200" height="100" fill="lightgray"/>
              <rect x="10" y="10" width="180" height="80" fill="crimson" mask="url(#m)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Mask_On_Group()
    {
        // The mask region defaults to the group's objectBoundingBox (measured
        // through the throwaway-recording fill box).
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <defs>
                <mask id="hole">
                  <rect width="200" height="200" fill="white"/>
                  <circle cx="100" cy="100" r="40" fill="black"/>
                </mask>
              </defs>
              <rect width="200" height="200" fill="white"/>
              <g mask="url(#hole)">
                <rect x="20" y="20" width="160" height="160" fill="teal"/>
                <circle cx="100" cy="100" r="60" fill="orange"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }
}
