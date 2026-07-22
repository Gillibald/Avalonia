using Avalonia.Media;
using Avalonia.Media.Svg;
using Xunit;

namespace Avalonia.Svg.RenderTests;

/// <summary>
/// End-to-end cover for the <c>foreignObject</c> text fallback: unlike the unit
/// tests, this project configures a font manager, so the extracted lines actually
/// go through the text pipeline. Asserted on the compiled recording's bounds
/// rather than a golden image — what matters is that glyphs are produced and land
/// inside the object's rect.
/// </summary>
public class ForeignObjectRenderTests : SvgRenderTestBase
{
    public ForeignObjectRenderTests()
        : base("ForeignObject")
    {
    }

    private const string LabelDocument =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"
             font-family="Noto Sans" font-size="16">
          <g class="label">
            <foreignObject x="20" y="30" width="160" height="24">
              <div xmlns="http://www.w3.org/1999/xhtml" style="text-align: center;">
                <span><p>Is it working?</p></span>
              </div>
            </foreignObject>
          </g>
        </svg>
        """;

    [Fact]
    public void ForeignObject_Text_Renders_Through_The_Fallback()
    {
        using var document = SvgDocument.Parse(LabelDocument);
        using var image = new SvgImage(document);
        var bounds = image.Recording.Bounds;

        // Glyphs were produced at all - the whole point of the fallback.
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"nothing was drawn; bounds = {bounds}");

        // Centred in the object's rect (text-align: center) and sitting on its band.
        Assert.InRange(bounds.Center.X, 70, 130);
        Assert.InRange(bounds.Y, 20, 55);
        Assert.InRange(bounds.Bottom, 30, 70);
    }

    [Fact]
    public void ForeignObject_Without_Text_Draws_Nothing()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <foreignObject x="20" y="30" width="160" height="24">
                <div xmlns="http://www.w3.org/1999/xhtml"><span></span></div>
              </foreignObject>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Equal(default, image.Recording.Bounds);
    }
}
