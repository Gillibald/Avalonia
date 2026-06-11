using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgFilterTests
{
    private static bool Resolve(string defs, Rect bounds, out Rect region, out IImmutableEffect? effect)
    {
        using var document = SvgDocument.Parse(
            $"""<svg xmlns="http://www.w3.org/2000/svg">{defs}</svg>""");
        var context = new SvgCompileContext(document, new Size(100, 100));
        var style = SvgStyle.CreateDefault(new Size(100, 100));
        return SvgFilters.TryResolve(context, "f", bounds, style, out region, out effect);
    }

    private static Rect DefaultBounds => new(0, 0, 100, 100);

    [Fact]
    public void Blur_Primitive_Converts_Sigma_To_Radius()
    {
        Assert.True(Resolve(
            """<filter id="f"><feGaussianBlur stdDeviation="2"/></filter>""",
            DefaultBounds, out _, out var effect));

        var blur = Assert.IsType<ImmutableBlurEffect>(effect);
        // sigma = 0.288675 · radius + 0.5 inverted.
        Assert.Equal((2 - 0.5) / 0.288675, blur.Radius, 6);
    }

    [Fact]
    public void Offset_Primitive()
    {
        Assert.True(Resolve(
            """<filter id="f"><feOffset dx="10" dy="-5"/></filter>""",
            DefaultBounds, out _, out var effect));

        var offset = Assert.IsType<ImmutableOffsetEffect>(effect);
        Assert.Equal(10, offset.OffsetX);
        Assert.Equal(-5, offset.OffsetY);
    }

    [Fact]
    public void ColorMatrix_Explicit_Values()
    {
        Assert.True(Resolve(
            """<filter id="f"><feColorMatrix type="matrix" values="0 0 0 0 1  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0"/></filter>""",
            DefaultBounds, out _, out var effect));

        var matrix = Assert.IsType<ImmutableColorMatrixEffect>(effect);
        Assert.Equal(1, matrix.Matrix[4]);
        Assert.Equal(1, matrix.Matrix[18]);
        Assert.Equal(0, matrix.Matrix[0]);
    }

    [Fact]
    public void ColorMatrix_Saturate_Zero_Is_Grayscale()
    {
        Assert.True(Resolve(
            """<filter id="f"><feColorMatrix type="saturate" values="0"/></filter>""",
            DefaultBounds, out _, out var effect));

        var matrix = Assert.IsType<ImmutableColorMatrixEffect>(effect);
        Assert.Equal(0.213, matrix.Matrix[0], 9);
        Assert.Equal(0.715, matrix.Matrix[1], 9);
        Assert.Equal(0.072, matrix.Matrix[2], 9);
        // All three color rows identical for saturate(0).
        Assert.Equal(matrix.Matrix[0], matrix.Matrix[5], 9);
        Assert.Equal(matrix.Matrix[1], matrix.Matrix[11], 9);
    }

    [Fact]
    public void ColorMatrix_LuminanceToAlpha()
    {
        Assert.True(Resolve(
            """<filter id="f"><feColorMatrix type="luminanceToAlpha"/></filter>""",
            DefaultBounds, out _, out var effect));

        var matrix = Assert.IsType<ImmutableColorMatrixEffect>(effect);
        Assert.Equal(0, matrix.Matrix[0]);
        Assert.Equal(0.2125, matrix.Matrix[15], 9);
        Assert.Equal(0.7154, matrix.Matrix[16], 9);
    }

    [Fact]
    public void DropShadow_Primitive_With_Flood_Color()
    {
        Assert.True(Resolve(
            """<filter id="f"><feDropShadow dx="4" dy="6" stdDeviation="1" flood-color="red" flood-opacity="0.5"/></filter>""",
            DefaultBounds, out _, out var effect));

        var shadow = Assert.IsType<ImmutableDropShadowEffect>(effect);
        Assert.Equal(4, shadow.OffsetX);
        Assert.Equal(6, shadow.OffsetY);
        Assert.Equal(Colors.Red, shadow.Color);
        Assert.Equal(0.5, shadow.Opacity);
        Assert.True(shadow.BlurRadius > 0);
    }

    [Fact]
    public void Linear_Chain_Collapses_To_Composite()
    {
        Assert.True(Resolve(
            """
            <filter id="f">
              <feGaussianBlur stdDeviation="2" result="b"/>
              <feOffset in="b" dx="5" dy="5"/>
            </filter>
            """,
            DefaultBounds, out _, out var effect));

        var composite = Assert.IsType<ImmutableCompositeEffect>(effect);
        Assert.Equal(2, composite.Children.Count);
        Assert.IsType<ImmutableBlurEffect>(composite.Children[0]);
        Assert.IsType<ImmutableOffsetEffect>(composite.Children[1]);
    }

    [Fact]
    public void Classic_Merge_Builds_A_Merge_Graph()
    {
        Assert.True(Resolve(
            """
            <filter id="f">
              <feGaussianBlur in="SourceAlpha" stdDeviation="2" result="blur"/>
              <feOffset dx="4" dy="4" result="shadow"/>
              <feMerge>
                <feMergeNode in="shadow"/>
                <feMergeNode in="SourceGraphic"/>
              </feMerge>
            </filter>
            """,
            DefaultBounds, out _, out var effect));

        // The shadow chain stacks under the unmodified source graphic.
        var merge = Assert.IsType<ImmutableMergeEffect>(effect);
        Assert.Equal(2, merge.Inputs.Count);
        Assert.IsType<ImmutableCompositeEffect>(merge.Inputs[0]);
        Assert.Null(merge.Inputs[1]);
    }

    [Fact]
    public void Region_Defaults_To_Minus_Ten_Percent()
    {
        Assert.True(Resolve(
            """<filter id="f"><feGaussianBlur stdDeviation="1"/></filter>""",
            new Rect(0, 0, 100, 100), out var region, out _));

        Assert.Equal(new Rect(-10, -10, 120, 120), region);
    }

    [Fact]
    public void Region_UserSpace_Units()
    {
        Assert.True(Resolve(
            """<filter id="f" filterUnits="userSpaceOnUse" x="5" y="6" width="50" height="40"><feGaussianBlur stdDeviation="1"/></filter>""",
            DefaultBounds, out var region, out _));

        Assert.Equal(new Rect(5, 6, 50, 40), region);
    }

    [Fact]
    public void Unsupported_Primitive_Renders_Unfiltered()
    {
        Assert.False(Resolve(
            """<filter id="f"><feTurbulence baseFrequency="0.05"/></filter>""",
            DefaultBounds, out _, out _));
    }

    [Fact]
    public void NonLinear_Input_Resolves_Through_The_Graph()
    {
        // A primitive may branch from SourceGraphic regardless of earlier
        // results: inputs form a graph, not a chain.
        Assert.True(Resolve(
            """
            <filter id="f">
              <feGaussianBlur stdDeviation="2" result="b"/>
              <feOffset in="SourceGraphic" dx="5" dy="5"/>
            </filter>
            """,
            DefaultBounds, out _, out var effect));

        Assert.IsType<ImmutableOffsetEffect>(effect);
    }

    [Fact]
    public void Empty_Filter_Hides_The_Element()
    {
        Assert.True(Resolve("""<filter id="f"/>""", DefaultBounds, out _, out var effect));
        Assert.Null(effect);

        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><filter id="f"/></defs>
              <rect width="50" height="50" fill="red" filter="url(#f)"/>
            </svg>
            """);
        using var recording = DrawingRecording.Create(ctx =>
            SvgCompiler.CompileDocument(document, ctx, document.GetIntrinsicSize()));

        Assert.Equal(default, recording.Bounds);
    }

    [Fact]
    public void Filtered_Shape_Bounds_Inflate_By_The_Effect()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs><filter id="f"><feGaussianBlur stdDeviation="3"/></filter></defs>
              <rect x="20" y="20" width="40" height="40" fill="red" filter="url(#f)"/>
            </svg>
            """);
        using var recording = DrawingRecording.Create(ctx =>
            SvgCompiler.CompileDocument(document, ctx, document.GetIntrinsicSize()));

        var bounds = recording.Bounds;
        Assert.True(bounds.X < 20, $"bounds.X {bounds.X} should include blur padding");
        Assert.True(bounds.Right > 60, $"bounds.Right {bounds.Right} should include blur padding");
    }

    [Fact]
    public void Missing_Filter_Renders_Unfiltered()
    {
        using var document = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect width="50" height="50" fill="red" filter="url(#missing)"/></svg>""");
        using var recording = DrawingRecording.Create(ctx =>
            SvgCompiler.CompileDocument(document, ctx, document.GetIntrinsicSize()));

        Assert.Equal(new Rect(0, 0, 50, 50), recording.Bounds);
    }
}
