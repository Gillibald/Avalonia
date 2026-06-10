using System;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgDocumentTests
{
    [Fact]
    public void Parses_Element_Tree()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="group">
                <rect x="1" width="10" height="10"/>
                <circle r="5"/>
              </g>
            </svg>
            """);

        Assert.Equal("svg", document.Root.Name);
        var group = Assert.Single(document.Root.Children);
        Assert.Equal("g", group.Name);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal("rect", group.Children[0].Name);
        Assert.Equal("circle", group.Children[1].Name);
        Assert.Same(group, group.Children[0].Parent);
        Assert.Equal("1", group.Children[0].GetAttribute("x"));
    }

    [Fact]
    public void Maps_Elements_By_Id()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r1" width="1" height="1"/></svg>""");

        Assert.NotNull(document.GetElementById("r1"));
        Assert.Equal("rect", document.GetElementById("r1")!.Name);
        Assert.Null(document.GetElementById("missing"));
    }

    [Fact]
    public void Href_Resolves_Plain_And_Xlink()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <use id="legacy" xlink:href="#a"/>
              <use id="plain" href="#b"/>
              <use id="both" href="#plain-wins" xlink:href="#legacy-loses"/>
            </svg>
            """);

        Assert.Equal("#a", document.GetElementById("legacy")!.Href);
        Assert.Equal("#b", document.GetElementById("plain")!.Href);
        Assert.Equal("#plain-wins", document.GetElementById("both")!.Href);
    }

    [Fact]
    public void Style_Declaration_Wins_Over_Presentation_Attribute()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" fill="red" style="fill: blue"/></svg>""");

        var rect = document.GetElementById("r")!;
        Assert.Equal("blue", rect.GetStyleOrAttribute("fill"));
        Assert.Equal("red", rect.GetAttribute("fill"));
    }

    [Fact]
    public void Foreign_Namespace_Elements_Are_Skipped()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:ink="http://inkscape.org/ns">
              <ink:meta><ink:nested/></ink:meta>
              <rect width="1" height="1"/>
            </svg>
            """);

        var rect = Assert.Single(document.Root.Children);
        Assert.Equal("rect", rect.Name);
    }

    [Fact]
    public void Documents_Without_Namespace_Are_Accepted()
    {
        using var document = SvgDocument.Parse("""<svg><rect width="1" height="1"/></svg>""");

        Assert.Equal("svg", document.Root.Name);
        Assert.Single(document.Root.Children);
    }

    [Fact]
    public void Doctype_Is_Ignored()
    {
        using var document = SvgDocument.Parse(
            """
            <?xml version="1.0"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg"/>
            """);

        Assert.Equal("svg", document.Root.Name);
    }

    [Fact]
    public void Non_Svg_Root_Throws()
    {
        Assert.Throws<FormatException>(() => SvgDocument.Parse("<html/>"));
    }

    [Fact]
    public void Intrinsic_Size_Prefers_Width_And_Height()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 50 25"/>""");

        Assert.Equal(new Size(200, 100), document.GetIntrinsicSize());
    }

    [Fact]
    public void Intrinsic_Size_Falls_Back_To_ViewBox()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 50 25"/>""");

        Assert.Equal(new Size(50, 25), document.GetIntrinsicSize());
    }

    [Fact]
    public void Intrinsic_Size_Defaults_To_Css_Default()
    {
        using var document = SvgDocument.Parse("""<svg xmlns="http://www.w3.org/2000/svg"/>""");

        Assert.Equal(new Size(300, 150), document.GetIntrinsicSize());
    }

    [Fact]
    public void Percentage_Width_Falls_Back_To_ViewBox()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%" viewBox="0 0 50 25"/>""");

        Assert.Equal(new Size(50, 25), document.GetIntrinsicSize());
    }
}
