using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class FilterRenderTests : SvgRenderTestBase
{
    public FilterRenderTests() : base("Filters")
    {
    }

    [Fact]
    public async Task Blur_On_Group()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs><filter id="f"><feGaussianBlur stdDeviation="3"/></filter></defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="20" y="20" width="60" height="60" fill="crimson"/>
              <g filter="url(#f)">
                <rect x="120" y="20" width="60" height="60" fill="crimson"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task DropShadow_Primitive()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs><filter id="f"><feDropShadow dx="6" dy="6" stdDeviation="2" flood-color="navy" flood-opacity="0.6"/></filter></defs>
              <rect width="200" height="100" fill="white"/>
              <circle cx="100" cy="45" r="30" fill="gold" filter="url(#f)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Saturate_Zero_Grayscale()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs><filter id="f"><feColorMatrix type="saturate" values="0"/></filter></defs>
              <rect width="200" height="100" fill="white"/>
              <g>
                <rect x="15" y="20" width="30" height="60" fill="red"/>
                <rect x="45" y="20" width="30" height="60" fill="lime"/>
              </g>
              <g filter="url(#f)">
                <rect x="125" y="20" width="30" height="60" fill="red"/>
                <rect x="155" y="20" width="30" height="60" fill="lime"/>
              </g>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Classic_Merge_Drop_Shadow()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <filter id="f">
                  <feGaussianBlur in="SourceAlpha" stdDeviation="2" result="blur"/>
                  <feOffset dx="5" dy="5" result="shadow"/>
                  <feMerge>
                    <feMergeNode in="shadow"/>
                    <feMergeNode in="SourceGraphic"/>
                  </feMerge>
                </filter>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="60" y="20" width="80" height="50" rx="8" fill="seagreen" filter="url(#f)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Filter_Region_Clips_The_Output()
    {
        // A tight userSpace region: the blur is hard-clipped at the region edge.
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <filter id="f" filterUnits="userSpaceOnUse" x="20" y="20" width="70" height="60">
                  <feGaussianBlur stdDeviation="4"/>
                </filter>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="30" y="30" width="60" height="40" fill="indigo" filter="url(#f)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Unsupported_Filter_Renders_Unfiltered()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs><filter id="f"><feTurbulence baseFrequency="0.05"/></filter></defs>
              <rect width="200" height="100" fill="white"/>
              <rect x="70" y="20" width="60" height="60" fill="teal" filter="url(#f)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }
}
