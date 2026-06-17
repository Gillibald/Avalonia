using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgUseSymbolTests
{
    private static DrawingRecording Compile(SvgDocument document)
    {
        var size = document.GetIntrinsicSize();
        return DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, size));
    }

    [Fact]
    public void Use_Translates_Target()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><rect id="r" width="10" height="10" fill="red"/></defs>
              <use href="#r" x="100" y="50"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(new Rect(100, 50, 10, 10), recording.Bounds);
        Assert.True(recording.HitTest(new Point(105, 55)));
        Assert.False(recording.HitTest(new Point(5, 5)));
    }

    [Fact]
    public void Use_Works_With_Xlink_Href()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <defs><rect id="r" width="10" height="10" fill="red"/></defs>
              <use xlink:href="#r" x="20"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(new Rect(20, 0, 10, 10), recording.Bounds);
    }

    [Fact]
    public void Use_Of_Rendered_Element_Duplicates_It()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="r" width="10" height="10" fill="red"/>
              <use href="#r" x="50"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(new Rect(0, 0, 60, 10), recording.Bounds);
        Assert.True(recording.HitTest(new Point(5, 5)));
        Assert.True(recording.HitTest(new Point(55, 5)));
        Assert.False(recording.HitTest(new Point(30, 5)));
    }

    [Fact]
    public void Symbol_With_ViewBox_Scales_To_Use_Size()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <symbol id="s" viewBox="0 0 10 10">
                <rect width="10" height="10" fill="red"/>
              </symbol>
              <use href="#s" width="100" height="100"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.True(recording.HitTest(new Point(50, 50)));
        Assert.True(recording.HitTest(new Point(95, 95)));
        Assert.False(recording.HitTest(new Point(150, 50)));
    }

    [Fact]
    public void Symbol_Viewport_Clips_Overflowing_Content()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <symbol id="s" viewBox="0 0 10 10">
                <rect width="30" height="10" fill="red"/>
              </symbol>
              <use href="#s" width="100" height="100"/>
            </svg>
            """);
        using var recording = Compile(document);

        // Content extending past the symbol viewport is clipped (overflow hidden).
        Assert.True(recording.HitTest(new Point(95, 50)));
        Assert.False(recording.HitTest(new Point(150, 50)));
    }

    [Fact]
    public void Same_Target_Compiles_One_Shared_Recording()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><rect id="r" width="10" height="10" fill="red"/></defs>
              <use href="#r"/>
              <use href="#r" x="20"/>
              <use href="#r" x="40"/>
              <use href="#r" x="60"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(1, document.SharedRecordingCount);
        Assert.Equal(new Rect(0, 0, 70, 10), recording.Bounds);
    }

    [Fact]
    public void Nested_Use_Expands()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <rect id="leaf" width="10" height="10" fill="red"/>
                <g id="pair">
                  <use href="#leaf"/>
                  <use href="#leaf" x="20"/>
                </g>
              </defs>
              <use href="#pair" y="100"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(new Rect(0, 100, 30, 10), recording.Bounds);
        Assert.Equal(2, document.SharedRecordingCount);
    }

    [Fact]
    public void Use_Cycle_Is_Pruned()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="a">
                <rect width="10" height="10" fill="red"/>
                <use href="#a" x="20"/>
              </g>
              <use href="#a" x="100"/>
            </svg>
            """);
        using var recording = Compile(document);

        // The self-reference inside #a is pruned; both the direct render and the
        // top-level use draw the pruned expansion.
        Assert.Equal(new Rect(0, 0, 110, 10), recording.Bounds);
    }

    [Fact]
    public void Use_With_Missing_Target_Renders_Nothing()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><use href="#missing"/></svg>""");
        using var recording = Compile(document);

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Document_Dispose_Releases_Shared_Recordings()
    {
        var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <defs><rect id="r" width="10" height="10" fill="red"/></defs>
              <use href="#r" x="10" y="10"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.Equal(1, document.SharedRecordingCount);
        Assert.Equal(new Rect(10, 10, 10, 10), image.ContentBounds);

        document.Dispose();

        // The cache is released; already-compiled content keeps replaying since
        // use sites reference the recording as a Shared child.
        Assert.Equal(0, document.SharedRecordingCount);
        Assert.Equal(new Rect(10, 10, 10, 10), image.ContentBounds);
        Assert.True(image.Recording.HitTest(new Point(15, 15)));
    }
}
