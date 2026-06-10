using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgVisibilityTests
{
    private static DrawingRecording Compile(SvgDocument document)
    {
        var size = document.GetIntrinsicSize();
        return DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, size));
    }

    [Fact]
    public void Hidden_Shape_Is_Not_Drawn()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="50" height="50" fill="red" visibility="hidden"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Collapse_Behaves_As_Hidden()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="50" height="50" fill="red" visibility="collapse"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Visibility_Inherits_Into_Group_Content()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g visibility="hidden">
                <rect width="50" height="50" fill="red"/>
                <circle cx="80" cy="80" r="10" fill="blue"/>
              </g>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Child_Can_Reenable_Visibility_Inside_Hidden_Group()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g visibility="hidden">
                <rect width="50" height="50" fill="red"/>
                <rect x="60" y="60" width="10" height="10" fill="blue" visibility="visible"/>
              </g>
            </svg>
            """);
        using var recording = Compile(document);

        // Unlike display:none, hidden subtrees still compile their children, so
        // a child can opt back in.
        Assert.Equal(new Rect(60, 60, 10, 10), recording.Bounds);
    }

    [Fact]
    public void Hidden_Shape_Skips_Its_Markers()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <defs>
                <marker id="m" markerWidth="10" markerHeight="10">
                  <rect width="10" height="10" fill="blue"/>
                </marker>
              </defs>
              <line x1="10" y1="10" x2="50" y2="10" stroke="none" marker-end="url(#m)" visibility="hidden"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Hidden_Geometry_Still_Contributes_To_Fill_Box()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="g">
                <rect x="10" y="20" width="30" height="40" fill="red" visibility="hidden"/>
              </g>
            </svg>
            """);

        var compileContext = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(new Size(100, 100));

        // getBBox() semantics: visibility:hidden geometry is included (only
        // display:none is excluded).
        var bounds = SvgCompiler.GetFillBounds(document.GetElementById("g")!, compileContext, style);

        Assert.Equal(new Rect(10, 20, 30, 40), bounds);
    }

    [Fact]
    public void Display_None_Geometry_Is_Excluded_From_Fill_Box()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="g">
                <rect x="10" y="20" width="30" height="40" fill="red" display="none"/>
              </g>
            </svg>
            """);

        var compileContext = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(new Size(100, 100));

        var bounds = SvgCompiler.GetFillBounds(document.GetElementById("g")!, compileContext, style);

        Assert.Equal(default, bounds);
    }

    [Fact]
    public void Measuring_Use_Does_Not_Pollute_The_Shared_Recording_Cache()
    {
        // The g[filter] forces a measuring pass over its <use> child before the
        // real compilation. Measuring skips decorations (markers), so the
        // measuring-time symbol recording must not be cached — otherwise the
        // second, unfiltered <use> would replay marker-less content.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <defs>
                <filter id="f"><feGaussianBlur stdDeviation="1"/></filter>
                <marker id="m" markerWidth="10" markerHeight="10">
                  <rect width="10" height="10" fill="blue"/>
                </marker>
                <symbol id="s">
                  <line x1="0" y1="5" x2="20" y2="5" stroke="none" marker-end="url(#m)"/>
                </symbol>
              </defs>
              <g filter="url(#f)">
                <use href="#s" width="30" height="30"/>
              </g>
              <use href="#s" x="100" width="30" height="30"/>
            </svg>
            """);
        using var recording = Compile(document);

        // The second use site replays the full-fidelity shared recording: the
        // marker rect sits at the line end vertex (120, 5)–(130, 15).
        Assert.True(recording.HitTest(new Point(125, 10)));
    }
}
