using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgHitTestTests
{
    [Fact]
    public void Hit_Chain_Is_Innermost_First()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="group">
                <rect id="r" x="10" y="10" width="20" height="20" fill="red"/>
              </g>
            </svg>
            """);
        using var image = new SvgImage(document);

        var chain = image.HitTestElements(new Point(15, 15));

        Assert.Equal(new[] { "rect", "g", "svg" }, chain.Select(e => e.Name));
        Assert.Same(document.GetElementById("r"), chain[0]);
        Assert.Same(document.GetElementById("group"), chain[1]);
    }

    [Fact]
    public void Miss_Returns_Empty_Chain()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="10" y="10" width="20" height="20" fill="red"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(50, 50)));
        Assert.False(image.HitTest(new Point(50, 50)));
        Assert.True(image.HitTest(new Point(15, 15)));
    }

    [Fact]
    public void Topmost_Of_Overlapping_Siblings_Wins()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="below" x="0" y="0" width="40" height="40" fill="red"/>
              <rect id="above" x="20" y="20" width="40" height="40" fill="blue"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("above"), image.HitTestElements(new Point(30, 30))[0]);
        Assert.Same(document.GetElementById("below"), image.HitTestElements(new Point(10, 10))[0]);
    }

    [Fact]
    public void Transform_Is_Inverted()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g transform="translate(50 0)">
                <rect id="r" width="10" height="10" fill="red"/>
              </g>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(55, 5))[0]);
        Assert.Empty(image.HitTestElements(new Point(5, 5)));
    }

    [Fact]
    public void Shape_Transform_Is_Inverted()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" width="10" height="10" fill="red" transform="translate(30 40)"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(35, 45))[0]);
        Assert.Empty(image.HitTestElements(new Point(5, 5)));
    }

    [Fact]
    public void ViewBox_Mapping_Applies()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 10 10">
              <rect id="r" width="5" height="5" fill="red"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        // The rect covers the upper-left 50×50 viewport square under the 10× viewBox scale.
        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(25, 25))[0]);
        Assert.Empty(image.HitTestElements(new Point(75, 75)));
    }

    [Fact]
    public void Circle_Uses_Radial_Distance()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <circle id="c" cx="50" cy="50" r="10" fill="red"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("c"), image.HitTestElements(new Point(50, 55))[0]);
        // Inside the bounding box but outside the circle.
        Assert.Empty(image.HitTestElements(new Point(58, 58)));
    }

    [Fact]
    public void Line_Hits_Within_Stroke_Width()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <line id="l" x1="0" y1="10" x2="100" y2="10" stroke="black" stroke-width="10"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("l"), image.HitTestElements(new Point(50, 14))[0]);
        Assert.Empty(image.HitTestElements(new Point(50, 16)));
    }

    [Fact]
    public void Unpainted_Shape_Is_Not_Hit_By_Default()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="10" y="10" width="20" height="20" fill="none"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(15, 15)));
    }

    [Fact]
    public void Pointer_Events_All_Hits_Unpainted_Shape()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" x="10" y="10" width="20" height="20" fill="none" pointer-events="all"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(15, 15))[0]);
    }

    [Fact]
    public void Pointer_Events_None_Disables_Hits()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="10" y="10" width="20" height="20" fill="red" pointer-events="none"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(15, 15)));
    }

    [Fact]
    public void Pointer_Events_Inherits_And_Can_Be_Overridden()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g pointer-events="none">
                <rect x="0" y="0" width="20" height="20" fill="red"/>
                <rect id="r" x="40" y="0" width="20" height="20" fill="red" pointer-events="visiblePainted"/>
              </g>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(10, 10)));
        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(50, 10))[0]);
    }

    [Fact]
    public void Pointer_Events_Stroke_Hits_Stroke_Only()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" x="20" y="20" width="40" height="40" fill="red" stroke="black" stroke-width="4"
                    pointer-events="stroke"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        // On the stroke band around the edge.
        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(20, 40))[0]);
        // Well inside the fill.
        Assert.Empty(image.HitTestElements(new Point(40, 40)));
    }

    [Fact]
    public void Hidden_Element_Is_Not_Hit_By_Default_But_Hit_With_Pointer_Events_All()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="0" y="0" width="20" height="20" fill="red" visibility="hidden"/>
              <rect id="r" x="40" y="0" width="20" height="20" fill="red" visibility="hidden" pointer-events="all"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(10, 10)));
        Assert.Same(document.GetElementById("r"), image.HitTestElements(new Point(50, 10))[0]);
    }

    [Fact]
    public void Use_Of_Symbol_Reports_Symbol_Content_And_Use_Site()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <symbol id="s" viewBox="0 0 10 10">
                <rect id="r" width="10" height="10" fill="red"/>
              </symbol>
              <use id="u" href="#s" x="20" y="20" width="50" height="50"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        var chain = image.HitTestElements(new Point(45, 45));

        Assert.Equal(new[] { "rect", "use", "svg" }, chain.Select(e => e.Name));
        Assert.Same(document.GetElementById("r"), chain[0]);
        Assert.Same(document.GetElementById("u"), chain[1]);
        Assert.Empty(image.HitTestElements(new Point(10, 10)));
    }

    [Fact]
    public void Use_Symbol_Viewport_Clips_Hits()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <symbol id="s">
                <rect width="100" height="100" fill="red"/>
              </symbol>
              <use href="#s" width="30" height="30"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        // The symbol viewport (30×30, overflow hidden) clips both pixels and hits.
        Assert.NotEmpty(image.HitTestElements(new Point(15, 15)));
        Assert.Empty(image.HitTestElements(new Point(60, 60)));
    }

    [Fact]
    public void Use_Of_Shape_Hits_At_Both_Sites()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" width="10" height="10" fill="red"/>
              <use id="u" href="#r" x="50"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        var direct = image.HitTestElements(new Point(5, 5));
        Assert.Equal(new[] { "rect", "svg" }, direct.Select(e => e.Name));

        var viaUse = image.HitTestElements(new Point(55, 5));
        Assert.Equal(new[] { "rect", "use", "svg" }, viaUse.Select(e => e.Name));
        Assert.Same(document.GetElementById("r"), viaUse[0]);
    }

    [Fact]
    public void Display_None_Subtree_Is_Not_Hit()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g display="none">
                <rect width="50" height="50" fill="red" pointer-events="all"/>
              </g>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Empty(image.HitTestElements(new Point(25, 25)));
    }
}
