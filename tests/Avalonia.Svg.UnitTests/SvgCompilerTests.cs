using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// Compiler tests observed through the public <see cref="DrawingRecording"/>
/// surface (bounds + hit-testing). Geometry-backed shapes (path/polyline/polygon)
/// require a render platform and are covered by the parser tests plus the render
/// test suite.
/// </summary>
public class SvgCompilerTests
{
    private static DrawingRecording Compile(string svg)
    {
        using var document = SvgDocument.Parse(svg);
        var size = document.GetIntrinsicSize();
        return DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, size));
    }

    [Fact]
    public void Rect_Produces_Expected_Bounds()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect x="10" y="20" width="30" height="40" fill="red"/></svg>""");

        Assert.Equal(new Rect(10, 20, 30, 40), recording.Bounds);
        Assert.True(recording.HitTest(new Point(25, 40)));
        Assert.False(recording.HitTest(new Point(5, 5)));
    }

    [Fact]
    public void Fill_Defaults_To_Black()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10"/></svg>""");

        Assert.Equal(new Rect(0, 0, 10, 10), recording.Bounds);
    }

    [Fact]
    public void Fill_None_Without_Stroke_Renders_Nothing()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" fill="none"/></svg>""");

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Stroke_Inflates_Bounds()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect x="10" y="20" width="30" height="40" fill="none" stroke="red" stroke-width="4"/></svg>""");

        Assert.Equal(new Rect(8, 18, 34, 44), recording.Bounds);
    }

    [Fact]
    public void Circle_Produces_Expected_Bounds()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><circle cx="50" cy="50" r="10" fill="red"/></svg>""");

        Assert.Equal(new Rect(40, 40, 20, 20), recording.Bounds);
        Assert.True(recording.HitTest(new Point(50, 50)));
        Assert.False(recording.HitTest(new Point(41, 41)));
    }

    [Fact]
    public void Ellipse_Produces_Expected_Bounds()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><ellipse cx="50" cy="50" rx="20" ry="10" fill="red"/></svg>""");

        Assert.Equal(new Rect(30, 40, 40, 20), recording.Bounds);
    }

    [Fact]
    public void Ellipse_Auto_Radius_Takes_The_Other_Radius()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><ellipse cx="50" cy="50" ry="10" fill="red"/></svg>""");

        Assert.Equal(new Rect(40, 40, 20, 20), recording.Bounds);
    }

    [Fact]
    public void Line_Is_Stroke_Only()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><line x1="0" y1="10" x2="100" y2="10" stroke="red" stroke-width="2"/></svg>""");

        Assert.Equal(new Rect(0, 9, 100, 2), recording.Bounds);
    }

    [Fact]
    public void Line_Without_Stroke_Renders_Nothing()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><line x1="0" y1="10" x2="100" y2="10" fill="red"/></svg>""");

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Group_Transform_Applies_To_Children()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g transform="translate(100, 50)">
                <rect width="10" height="10" fill="red"/>
              </g>
            </svg>
            """);

        Assert.Equal(new Rect(100, 50, 10, 10), recording.Bounds);
    }

    [Fact]
    public void Shape_Transform_Applies()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" fill="red" transform="scale(2)"/></svg>""");

        Assert.Equal(new Rect(0, 0, 20, 20), recording.Bounds);
    }

    [Fact]
    public void Presentation_Attributes_Inherit()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g fill="none">
                <rect width="10" height="10"/>
              </g>
            </svg>
            """);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Style_Attribute_Wins_Over_Presentation_Attribute()
    {
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10" fill="red" style="fill: none"/></svg>""");

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Display_None_Skips_Subtree()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g display="none">
                <rect width="10" height="10" fill="red"/>
              </g>
            </svg>
            """);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Defs_Content_Is_Not_Rendered()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <rect width="10" height="10" fill="red"/>
              </defs>
            </svg>
            """);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void CurrentColor_Resolves_Inherited_Color()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" color="red">
              <rect width="10" height="10" fill="currentColor"/>
            </svg>
            """);

        Assert.Equal(new Rect(0, 0, 10, 10), recording.Bounds);
    }

    [Fact]
    public void ViewBox_Scales_Content_To_Viewport()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" viewBox="0 0 100 100">
              <rect width="100" height="100" fill="red"/>
            </svg>
            """);

        Assert.Equal(new Rect(0, 0, 200, 200), recording.Bounds);
    }

    [Fact]
    public void ViewBox_Meet_Centers_Content()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 100 100">
              <rect width="100" height="100" fill="red"/>
            </svg>
            """);

        // Uniform scale 1, centered horizontally in the 200-wide viewport.
        Assert.Equal(new Rect(50, 0, 100, 100), recording.Bounds);
    }

    [Fact]
    public void Rect_Corner_Radius_Auto_Takes_The_Other_Value()
    {
        // rx auto + ry 5 must produce rounded corners: a hit test just inside the
        // corner square misses the rounded shape.
        using var recording = Compile(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="20" height="20" ry="5" fill="red"/></svg>""");

        Assert.Equal(new Rect(0, 0, 20, 20), recording.Bounds);
        Assert.False(recording.HitTest(new Point(0.5, 0.5)));
        Assert.True(recording.HitTest(new Point(10, 10)));
    }

    [Fact]
    public void Pen_Defaults_Follow_Svg_Initial_Values()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" stroke="red"/></svg>""");

        var style = SvgStyle.CreateDefault(new Size(100, 100));
        style.Apply(document.GetElementById("r")!);
        var pen = style.ResolvePen()!;

        Assert.Equal(1, pen.Thickness);
        Assert.Equal(PenLineCap.Flat, pen.LineCap);
        Assert.Equal(PenLineJoin.Miter, pen.LineJoin);
        // The SVG initial miter limit is 4, not Avalonia's pen default of 10.
        Assert.Equal(4, pen.MiterLimit);
        Assert.Null(pen.DashStyle);
    }

    [Fact]
    public void Dash_Array_Converts_To_Thickness_Multiples()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" stroke="red" stroke-width="2" stroke-dasharray="4 2" stroke-dashoffset="2"/></svg>""");

        var style = SvgStyle.CreateDefault(new Size(100, 100));
        style.Apply(document.GetElementById("r")!);
        var pen = style.ResolvePen()!;

        // SVG dash values are user units; Avalonia dash values are multiples of
        // the pen thickness.
        Assert.Equal(new double[] { 2, 1 }, pen.DashStyle!.Dashes);
        Assert.Equal(1, pen.DashStyle.Offset);
    }

    [Fact]
    public void Odd_Dash_Array_Repeats_Doubled()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" stroke="red" stroke-width="1" stroke-dasharray="4"/></svg>""");

        var style = SvgStyle.CreateDefault(new Size(100, 100));
        style.Apply(document.GetElementById("r")!);
        var pen = style.ResolvePen()!;

        Assert.Equal(new double[] { 4, 4 }, pen.DashStyle!.Dashes);
    }

    [Fact]
    public void SvgImage_Uses_Intrinsic_Size_And_Draws_Scaled()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="100" height="100" fill="red"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Equal(new Size(100, 100), image.Size);
        Assert.Equal(new Rect(0, 0, 100, 100), image.Recording.Bounds);

        using var drawn = DrawingRecording.Create(ctx =>
            image.Draw(ctx, new Rect(0, 0, 100, 100), new Rect(10, 10, 50, 50)));

        Assert.Equal(new Rect(10, 10, 50, 50), drawn.Bounds);
    }

    [Fact]
    public void Em_Geometry_Resolves_Against_The_Elements_Font_Size()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <g font-size="20">
                <rect x="1em" y="1em" width="8em" height="8em" fill="green"/>
              </g>
            </svg>
            """);

        Assert.Equal(new Rect(20, 20, 160, 160), recording.Bounds);
    }

    [Fact]
    public void Rem_Geometry_References_The_Root_Font_Size()
    {
        // rem ignores the element's own font-size (64) and resolves against
        // the document root's computed font size (32).
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" font-size="32">
              <rect font-size="64" x="1rem" y="1rem" width="4rem" height="4rem" fill="green"/>
            </svg>
            """);

        Assert.Equal(new Rect(32, 32, 128, 128), recording.Bounds);
    }

    [Fact]
    public void Viewport_Units_Resolve_Against_The_Viewport()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect x="5vw" y="5vh" width="30vw" height="30vmax" fill="green"/>
            </svg>
            """);

        Assert.Equal(new Rect(10, 5, 60, 60), recording.Bounds);
    }

    [Fact]
    public void Ch_Falls_Back_To_Half_Em_Without_A_Font_Manager()
    {
        // No font manager is registered in this test context, so ch resolves
        // with the CSS-sanctioned 0.5em fallback; with a platform present it
        // uses the '0' glyph advance (covered by the render-side corpus test).
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" font-size="32">
              <rect width="4ch" height="10" fill="green"/>
            </svg>
            """);

        Assert.Equal(new Rect(0, 0, 64, 10), recording.Bounds);
    }
}
