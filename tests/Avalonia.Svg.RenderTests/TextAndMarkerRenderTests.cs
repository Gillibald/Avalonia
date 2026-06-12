using System.Threading.Tasks;
using Xunit;

namespace Avalonia.Svg.RenderTests;

public class TextAndMarkerRenderTests : SvgRenderTestBase
{
    public TextAndMarkerRenderTests() : base("TextAndMarkers")
    {
    }

    private const string TestFont =
        "resm:Avalonia.Svg.RenderTests.Assets?assembly=Avalonia.Svg.RenderTests#Noto Mono";

    [Fact]
    public async Task Text_Anchors()
    {
        var target = new SvgHost(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="120" font-family="{TestFont}">
              <rect width="200" height="120" fill="white"/>
              <line x1="100" y1="0" x2="100" y2="120" stroke="lightgray"/>
              <text x="100" y="30" font-size="16">start</text>
              <text x="100" y="60" font-size="16" text-anchor="middle">middle</text>
              <text x="100" y="90" font-size="16" text-anchor="end">end</text>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Text_Tspans_With_Styles_And_Offsets()
    {
        var target = new SvgHost(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="220" height="80" font-family="{TestFont}">
              <rect width="220" height="80" fill="white"/>
              <text x="10" y="40" font-size="16" fill="black">ab<tspan fill="red" font-size="24">cd</tspan><tspan dy="-8" fill="blue">up</tspan></text>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Text_On_A_Full_Circle_Path()
    {
        // One arc command spans the whole circle: the sampler must flatten
        // adaptively or the glyphs share chord tangents and render as
        // straight runs with 15° kinks (the orrery label-orbit regression).
        var target = new SvgHost(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="220" height="220" font-family="{TestFont}">
              <defs>
                <path id="ring" d="M 110 20 A 90 90 0 1 1 109.99 20"/>
              </defs>
              <rect width="220" height="220" fill="white"/>
              <circle cx="110" cy="110" r="90" fill="none" stroke="#dddddd"/>
              <text font-size="14" letter-spacing="4" fill="darkblue">
                <textPath href="#ring">RETAINED RECORDINGS ON ONE CIRCLE</textPath>
              </text>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Text_On_A_Transformed_Path()
    {
        // The referenced path's own transform + transform-origin apply to the
        // glyph layout, so the text rides the path exactly where it renders —
        // including a non-uniform scale, which reshapes the arc.
        var target = new SvgHost(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" font-family="{TestFont}">
              <rect width="200" height="200" fill="white"/>
              <path id="bent" d="M 30 100 A 70 70 0 0 1 170 100" fill="none" stroke="#cccccc"
                    transform="scale(1 0.6) rotate(25)" transform-origin="center"/>
              <text font-size="14" fill="darkblue">
                <textPath href="#bent">follow the line</textPath>
              </text>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Text_On_Path()
    {
        var target = new SvgHost(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="120" font-family="{TestFont}">
              <defs>
                <path id="arc" d="M 20 100 A 80 80 0 0 1 180 100"/>
              </defs>
              <rect width="200" height="120" fill="white"/>
              <text font-size="14" fill="darkblue">
                <textPath href="#arc" startOffset="10">curved baseline</textPath>
              </text>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Markers_On_Polyline()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="120">
              <defs>
                <marker id="dot" markerWidth="6" markerHeight="6" refX="3" refY="3" viewBox="0 0 6 6">
                  <circle cx="3" cy="3" r="3" fill="crimson"/>
                </marker>
                <marker id="arrow" markerWidth="8" markerHeight="6" refX="1" refY="3" orient="auto" viewBox="0 0 8 6">
                  <path d="M 0 0 L 8 3 L 0 6 Z" fill="darkblue"/>
                </marker>
              </defs>
              <rect width="200" height="120" fill="white"/>
              <polyline points="20,100 70,30 130,80 180,20" fill="none" stroke="gray" stroke-width="2"
                        marker-start="url(#dot)" marker-mid="url(#dot)" marker-end="url(#arrow)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Markers_Auto_Start_Reverse_On_Path()
    {
        var target = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <marker id="arrow" markerWidth="8" markerHeight="6" refX="1" refY="3" orient="auto-start-reverse" viewBox="0 0 8 6">
                  <path d="M 0 0 L 8 3 L 0 6 Z" fill="seagreen"/>
                </marker>
              </defs>
              <rect width="200" height="100" fill="white"/>
              <path d="M 30 50 C 70 10 130 90 170 50" fill="none" stroke="gray" stroke-width="2"
                    marker-start="url(#arrow)" marker-end="url(#arrow)"/>
            </svg>
            """);

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task Small_Caps_Uses_The_Fonts_Smcp_Feature()
    {
        // Source Sans Pro carries a real smcp feature; Noto Sans does not
        // (its small-caps go through the synthesis fallback instead).
        Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
            new Avalonia.Media.Typeface("fonts:svg-corpus#Source Sans Pro"), out var sourceSans));
        Assert.Contains(Avalonia.Media.Fonts.OpenTypeTag.Parse("smcp"), sourceSans!.SupportedFeatures);

        // The feature must reach shaping: the small-caps render differs from
        // the plain one.
        var plain = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="80" font-family="Source Sans Pro">
              <rect width="200" height="80" fill="white"/>
              <text x="10" y="50" font-size="32">Text</text>
            </svg>
            """);
        var smallCaps = new SvgHost(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="80" font-family="Source Sans Pro">
              <rect width="200" height="80" fill="white"/>
              <text x="10" y="50" font-size="32" font-variant="small-caps">Text</text>
            </svg>
            """);

        await RenderToFile(plain, "Smcp_Plain");
        await RenderToFile(smallCaps, "Smcp_SmallCaps");

        using var plainImage = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            System.IO.Path.Combine(OutputPath, "Smcp_Plain.composited.out.png"));
        using var capsImage = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            System.IO.Path.Combine(OutputPath, "Smcp_SmallCaps.composited.out.png"));

        var different = 0;
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 200; x++)
        {
            if (plainImage[x, y].R != capsImage[x, y].R)
                different++;
        }

        Assert.True(different > 100, $"smcp shaping should change the render ({different} px differ)");
    }
}