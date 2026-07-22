using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// The text-only <c>foreignObject</c> fallback: its (X)HTML content survives
/// parsing — every other foreign subtree still does not — and flattens to lines
/// the way HTML would break them. Drawing the lines needs a font manager, so the
/// rendered result belongs to the render suite.
/// </summary>
public class SvgForeignObjectTests
{
    private static string[] Lines(string svg, string id = "fo")
    {
        using var document = SvgDocument.Parse(svg);
        return SvgForeignObject.ExtractLines(document.GetElementById(id)!).ToArray();
    }

    [Fact]
    public void ForeignObject_Content_Survives_Parsing()
    {
        Assert.Equal(new[] { "Start" }, Lines(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject id="fo" width="100" height="24">
                <div xmlns="http://www.w3.org/1999/xhtml"><span class="nodeLabel"><p>Start</p></span></div>
              </foreignObject>
            </svg>
            """));
    }

    [Fact]
    public void Foreign_Subtree_Outside_A_ForeignObject_Is_Still_Skipped()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <metadata id="m">
                <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                  <rdf:Description>editor metadata</rdf:Description>
                </rdf:RDF>
              </metadata>
            </svg>
            """);

        Assert.Empty(document.GetElementById("m")!.Children);
    }

    [Fact]
    public void Block_Elements_Break_Lines_And_Inline_Elements_Do_Not()
    {
        Assert.Equal(new[] { "One", "Two and a half", "Three" }, Lines(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject id="fo" width="100" height="72">
                <div xmlns="http://www.w3.org/1999/xhtml"><p>One</p><p>Two<span> and a half</span></p><br/>Three</div>
              </foreignObject>
            </svg>
            """));
    }

    [Fact]
    public void Whitespace_Collapses_Like_Html()
    {
        Assert.Equal(new[] { "a b c" }, Lines(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject id="fo" width="100" height="24">
                <div xmlns="http://www.w3.org/1999/xhtml">   a   b
                     c   </div>
              </foreignObject>
            </svg>
            """));
    }

    [Fact]
    public void Nested_Blocks_Do_Not_Open_Empty_Lines()
    {
        Assert.Equal(new[] { "Is it working?" }, Lines(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject id="fo" width="100" height="24">
                <div xmlns="http://www.w3.org/1999/xhtml"><div><p>Is it working?</p></div></div>
              </foreignObject>
            </svg>
            """));
    }

    [Fact]
    public void ForeignObject_Without_Text_Produces_No_Lines()
    {
        Assert.Empty(Lines(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject id="fo" width="100" height="24">
                <div xmlns="http://www.w3.org/1999/xhtml"><span></span></div>
              </foreignObject>
            </svg>
            """));
    }
}
