using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgMarkerTests
{
    private static DrawingRecording Compile(SvgDocument document)
    {
        var size = document.GetIntrinsicSize();
        return DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, size));
    }

    [Fact]
    public void Marker_End_Places_Content_At_Line_End()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="4" markerHeight="4">
                  <rect width="2" height="2" fill="red"/>
                </marker>
              </defs>
              <line x1="0" y1="0" x2="100" y2="0" stroke="none" stroke-width="2" marker-end="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        // markerUnits default to strokeWidth: the 2x2 content scales by 2 and
        // lands at the line end.
        Assert.True(recording.HitTest(new Point(102, 2)));
        Assert.False(recording.HitTest(new Point(102, 6)));
    }

    [Fact]
    public void Marker_UserSpace_Units_Ignore_Stroke_Width()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="4" markerHeight="4" markerUnits="userSpaceOnUse">
                  <rect width="2" height="2" fill="red"/>
                </marker>
              </defs>
              <line x1="0" y1="0" x2="100" y2="0" stroke="none" stroke-width="10" marker-end="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        // The 2x2 content stays unscaled despite the 10-wide stroke.
        Assert.True(recording.HitTest(new Point(101, 1)));
        Assert.False(recording.HitTest(new Point(108, 1)));
    }

    [Fact]
    public void Marker_Auto_Orientation_Rotates_With_The_Path()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="6" markerHeight="6" orient="auto" markerUnits="userSpaceOnUse">
                  <rect width="4" height="2" fill="red"/>
                </marker>
              </defs>
              <line x1="0" y1="0" x2="0" y2="100" stroke="none" marker-end="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        // The +y line rotates the 4x2 marker content 90°: it now extends in +y
        // and -x from the vertex at (0,100).
        Assert.True(recording.HitTest(new Point(-1, 102)));
        Assert.False(recording.HitTest(new Point(3, 101)));
    }

    [Fact]
    public void Marker_Auto_Start_Reverse_Flips_The_Start_Marker()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="6" markerHeight="6" orient="auto-start-reverse" markerUnits="userSpaceOnUse">
                  <rect width="4" height="2" fill="red"/>
                </marker>
              </defs>
              <line x1="0" y1="0" x2="100" y2="0" stroke="none" marker-start="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        // Reversed at the start of a +x line: content extends towards -x and -y.
        Assert.True(recording.HitTest(new Point(-2, -1)));
        Assert.False(recording.HitTest(new Point(2, 1)));
    }

    [Fact]
    public void Marker_Mid_Applies_To_Interior_Polyline_Vertices()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="4" markerHeight="4" markerUnits="userSpaceOnUse">
                  <rect width="2" height="2" fill="red"/>
                </marker>
              </defs>
              <polyline points="0,50 50,50 100,50" fill="none" stroke="none" marker-mid="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.True(recording.HitTest(new Point(51, 51)));
        Assert.False(recording.HitTest(new Point(1, 52)));
        Assert.False(recording.HitTest(new Point(101, 52)));
    }

    [Fact]
    public void Marker_RefPoint_And_ViewBox_Align_The_Reference_To_The_Vertex()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="10" markerHeight="10" refX="5" refY="5" viewBox="0 0 10 10" markerUnits="userSpaceOnUse">
                  <rect width="10" height="10" fill="red"/>
                </marker>
              </defs>
              <line x1="50" y1="50" x2="100" y2="50" stroke="none" marker-start="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        // The reference point (5,5) lands on the vertex: content centered there.
        Assert.True(recording.HitTest(new Point(46, 46)));
        Assert.True(recording.HitTest(new Point(54, 54)));
        Assert.False(recording.HitTest(new Point(56, 56)));
    }

    [Fact]
    public void Markers_Share_One_Recording()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <marker id="m" markerWidth="4" markerHeight="4">
                  <rect width="2" height="2" fill="red"/>
                </marker>
              </defs>
              <polyline points="0,0 50,0 100,0" fill="none" stroke="none"
                        marker-start="url(#m)" marker-mid="url(#m)" marker-end="url(#m)"/>
            </svg>
            """);
        using var recording = Compile(document);

        Assert.Equal(1, document.SharedRecordingCount);
    }
}
