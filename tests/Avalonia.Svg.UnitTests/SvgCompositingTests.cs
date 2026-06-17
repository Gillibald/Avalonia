using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// Opacity, blend-mode, isolation, masks and patterns observed through the
/// recording surface; the pixel semantics are covered by the render suite.
/// </summary>
public class SvgCompositingTests
{
    private static DrawingRecording Compile(string svg, out SvgDocument document)
    {
        document = SvgDocument.Parse(svg);
        var doc = document;
        var size = doc.GetIntrinsicSize();
        return DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(doc, ctx, size));
    }

    [Fact]
    public void Group_Opacity_Preserves_Bounds()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g opacity="0.5">
                <rect x="10" y="10" width="30" height="30" fill="red"/>
              </g>
            </svg>
            """, out var document);
        using (document)
        {
            Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
            Assert.True(recording.HitTest(new Point(20, 20)));
        }
    }

    [Fact]
    public void Zero_Opacity_Skips_The_Subtree()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g opacity="0">
                <rect width="30" height="30" fill="red"/>
              </g>
            </svg>
            """, out var document);
        using (document)
        {
            Assert.Equal(default, recording.Bounds);
        }
    }

    [Fact]
    public void Blend_Mode_And_Isolation_Preserve_Bounds()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g style="isolation:isolate">
                <rect x="5" y="5" width="20" height="20" fill="red" style="mix-blend-mode: multiply"/>
              </g>
            </svg>
            """, out var document);
        using (document)
        {
            Assert.Equal(new Rect(5, 5, 20, 20), recording.Bounds);
        }
    }

    [Fact]
    public void Mask_Preserves_Geometry_Bounds_And_Hits()
    {
        using var recording = Compile(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <mask id="m">
                  <rect width="40" height="40" fill="white"/>
                </mask>
              </defs>
              <rect x="10" y="10" width="30" height="30" fill="red" mask="url(#m)"/>
            </svg>
            """, out var document);
        using (document)
        {
            Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
            Assert.True(recording.HitTest(new Point(20, 20)));
            Assert.Equal(1, document.SharedRecordingCount);
        }
    }

    [Fact]
    public void Pattern_Resolves_To_Immutable_Content_Brush()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <pattern id="p" width="0.25" height="0.25">
                  <rect width="10" height="10" fill="red"/>
                </pattern>
              </defs>
            </svg>
            """);
        var context = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(context.Viewport);

        var brush = SvgPaintServers.Resolve(context, "p", style, new Rect(0, 0, 80, 40), 1);

        // The mutable DrawingRecordingBrush snapshots to an immutable scene-brush
        // content so it can be captured by immutable recordings.
        var content = Assert.IsAssignableFrom<ISceneBrushContent>(brush);
        Assert.Equal(TileMode.Tile, content.Brush.TileMode);
        Assert.Equal(new RelativeRect(0, 0, 0.25, 0.25, RelativeUnit.Relative), content.Brush.DestinationRect);
        // patternUnits objectBoundingBox: the tile is 20x10 user units for the
        // 80x40 box; content units are user units.
        Assert.Equal(new RelativeRect(new Rect(0, 0, 20, 10), RelativeUnit.Absolute), content.Brush.SourceRect);
    }

    [Fact]
    public void Pattern_UserSpace_Units_And_Href_Inheritance()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <pattern id="base" width="20" height="10" patternUnits="userSpaceOnUse">
                  <rect width="10" height="10" fill="red"/>
                </pattern>
                <pattern id="p" href="#base"/>
              </defs>
            </svg>
            """);
        var context = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(context.Viewport);

        var brush = SvgPaintServers.Resolve(context, "p", style, new Rect(0, 0, 80, 40), 1);

        var content = Assert.IsAssignableFrom<ISceneBrushContent>(brush);
        Assert.Equal(new RelativeRect(0, 0, 20, 10, RelativeUnit.Absolute), content.Brush.DestinationRect);
        Assert.Equal(new RelativeRect(new Rect(0, 0, 20, 10), RelativeUnit.Absolute), content.Brush.SourceRect);
        // Content inherited through the chain produced one shared recording.
        Assert.Equal(1, document.SharedRecordingCount);
    }

    [Fact]
    public void Pattern_With_ViewBox_Maps_Content_To_Tile()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <pattern id="p" width="0.5" height="0.5" viewBox="0 0 10 10">
                  <rect width="10" height="10" fill="red"/>
                </pattern>
              </defs>
            </svg>
            """);
        var context = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(context.Viewport);

        var brush = SvgPaintServers.Resolve(context, "p", style, new Rect(0, 0, 100, 100), 1);

        var content = Assert.IsAssignableFrom<ISceneBrushContent>(brush);
        Assert.Equal(new RelativeRect(new Rect(0, 0, 10, 10), RelativeUnit.Absolute), content.Brush.SourceRect);
    }

    [Fact]
    public void Pattern_Without_Content_Or_Size_Returns_Null()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <pattern id="empty" width="0.5" height="0.5"/>
                <pattern id="zero"><rect width="1" height="1" fill="red"/></pattern>
              </defs>
            </svg>
            """);
        var context = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(context.Viewport);

        Assert.Null(SvgPaintServers.Resolve(context, "empty", style, new Rect(0, 0, 80, 40), 1));
        Assert.Null(SvgPaintServers.Resolve(context, "zero", style, new Rect(0, 0, 80, 40), 1));
    }

    [Fact]
    public void Fill_Opacity_Applies_To_The_Brush()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" fill="red" fill-opacity="0.5" width="10" height="10"/></svg>""");
        var style = SvgStyle.CreateDefault(new Size(100, 100));
        style.Apply(document.GetElementById("r")!);

        Assert.Equal(0.5, style.FillOpacity);
        var brush = style.ResolveBrush(style.Fill, style.FillOpacity)!;
        Assert.Equal(0.5, ((Avalonia.Media.Immutable.ImmutableSolidColorBrush)brush).Opacity);
    }
}
