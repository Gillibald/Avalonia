using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Immutable;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgGradientTests
{
    private static IImmutableBrush? Resolve(string defs, string id, Rect bounds, Size? viewport = null)
    {
        using var document = SvgDocument.Parse(
            $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">{defs}</svg>""");
        var context = new SvgCompileContext(document, viewport ?? new Size(100, 100));
        var style = SvgStyle.CreateDefault(context.Viewport);
        return SvgPaintServers.Resolve(context, id, style, bounds);
    }

    private static Rect DefaultBounds => new(0, 0, 50, 50);

    [Fact]
    public void Linear_Defaults_To_Horizontal_ObjectBoundingBox()
    {
        var brush = Resolve(
            """<linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>""",
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(new RelativePoint(0, 0, RelativeUnit.Relative), gradient.StartPoint);
        Assert.Equal(new RelativePoint(1, 0, RelativeUnit.Relative), gradient.EndPoint);
        Assert.Equal(GradientSpreadMethod.Pad, gradient.SpreadMethod);
        Assert.Equal(2, gradient.GradientStops.Count);
        Assert.Equal(Colors.Red, gradient.GradientStops[0].Color);
        Assert.Equal(Colors.Blue, gradient.GradientStops[1].Color);
    }

    [Fact]
    public void Linear_UserSpace_Resolves_Absolute_Points()
    {
        var brush = Resolve(
            """
            <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="10" y1="20" x2="30" y2="40">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(new RelativePoint(10, 20, RelativeUnit.Absolute), gradient.StartPoint);
        Assert.Equal(new RelativePoint(30, 40, RelativeUnit.Absolute), gradient.EndPoint);
    }

    [Fact]
    public void Linear_UserSpace_Default_X2_Is_Viewport_Width()
    {
        var brush = Resolve(
            """
            <linearGradient id="g" gradientUnits="userSpaceOnUse">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds, new Size(200, 100));

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(new RelativePoint(200, 0, RelativeUnit.Absolute), gradient.EndPoint);
    }

    [Fact]
    public void SpreadMethod_Maps()
    {
        var brush = Resolve(
            """
            <linearGradient id="g" spreadMethod="reflect">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds);

        Assert.Equal(GradientSpreadMethod.Reflect, Assert.IsType<ImmutableLinearGradientBrush>(brush).SpreadMethod);
    }

    [Fact]
    public void Stop_Offsets_Clamp_And_Are_Monotonic()
    {
        var brush = Resolve(
            """
            <linearGradient id="g">
              <stop offset="80%" stop-color="red"/>
              <stop offset="0.2" stop-color="green"/>
              <stop offset="2" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(0.8, gradient.GradientStops[0].Offset);
        // The out-of-order stop clamps up to the previous offset.
        Assert.Equal(0.8, gradient.GradientStops[1].Offset);
        Assert.Equal(1.0, gradient.GradientStops[2].Offset);
    }

    [Fact]
    public void Stop_Opacity_Multiplies_Alpha()
    {
        var brush = Resolve(
            """
            <linearGradient id="g">
              <stop offset="0" stop-color="red" stop-opacity="0.5"/>
              <stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(128, gradient.GradientStops[0].Color.A);
    }

    [Fact]
    public void Single_Stop_Is_Solid_Color()
    {
        var brush = Resolve(
            """<linearGradient id="g"><stop offset="0" stop-color="lime"/></linearGradient>""",
            "g", DefaultBounds);

        Assert.Equal(Colors.Lime, Assert.IsType<ImmutableSolidColorBrush>(brush).Color);
    }

    [Fact]
    public void No_Stops_Returns_Null()
    {
        Assert.Null(Resolve("""<linearGradient id="g"/>""", "g", DefaultBounds));
    }

    [Fact]
    public void Href_Inherits_Stops_And_Attributes()
    {
        var brush = Resolve(
            """
            <linearGradient id="base" x1="0.25" spreadMethod="repeat">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            <linearGradient id="g" href="#base" x1="0.5"/>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        Assert.Equal(2, gradient.GradientStops.Count);
        // Own attribute wins; unset attributes inherit through the chain.
        Assert.Equal(new RelativePoint(0.5, 0, RelativeUnit.Relative), gradient.StartPoint);
        Assert.Equal(GradientSpreadMethod.Repeat, gradient.SpreadMethod);
    }

    [Fact]
    public void Href_Cycle_Is_Safe()
    {
        Assert.Null(Resolve(
            """
            <linearGradient id="g" href="#h"/>
            <linearGradient id="h" href="#g"/>
            """,
            "g", DefaultBounds));
    }

    [Fact]
    public void GradientTransform_UserSpace_Passes_Through()
    {
        var brush = Resolve(
            """
            <linearGradient id="g" gradientUnits="userSpaceOnUse" gradientTransform="translate(10,20)">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        var transform = Assert.IsType<ImmutableTransform>(gradient.Transform);
        Assert.Equal(Matrix.CreateTranslation(10, 20), transform.Value);
    }

    [Fact]
    public void GradientTransform_ObjectBoundingBox_Conjugates_With_Bounds()
    {
        var brush = Resolve(
            """
            <linearGradient id="g" gradientTransform="translate(0.5, 0)">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </linearGradient>
            """,
            "g", new Rect(0, 0, 200, 100));

        // A half-box translation in unit space is 100 user units for a 200-wide box.
        var gradient = Assert.IsType<ImmutableLinearGradientBrush>(brush);
        var transform = Assert.IsType<ImmutableTransform>(gradient.Transform);
        Assert.Equal(100, transform.Value.M31, 9);
        Assert.Equal(0, transform.Value.M32, 9);
    }

    [Fact]
    public void ObjectBoundingBox_With_Zero_Area_Returns_Null()
    {
        Assert.Null(Resolve(
            """<linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>""",
            "g", new Rect(0, 0, 0, 10)));
    }

    [Fact]
    public void Radial_Defaults_To_Centered_Circle()
    {
        var brush = Resolve(
            """<radialGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></radialGradient>""",
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableRadialGradientBrush>(brush);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), gradient.Center);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), gradient.GradientOrigin);
        Assert.Equal(new RelativeScalar(0.5, RelativeUnit.Relative), gradient.RadiusX);
        Assert.Equal(new RelativeScalar(0.5, RelativeUnit.Relative), gradient.RadiusY);
    }

    [Fact]
    public void Radial_Focal_Point_Maps_To_GradientOrigin()
    {
        var brush = Resolve(
            """
            <radialGradient id="g" fx="0.2" fy="0.3">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </radialGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableRadialGradientBrush>(brush);
        Assert.Equal(new RelativePoint(0.2, 0.3, RelativeUnit.Relative), gradient.GradientOrigin);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), gradient.Center);
    }

    [Fact]
    public void Radial_UserSpace_Resolves_Absolute_Values()
    {
        var brush = Resolve(
            """
            <radialGradient id="g" gradientUnits="userSpaceOnUse" cx="50" cy="60" r="25">
              <stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/>
            </radialGradient>
            """,
            "g", DefaultBounds);

        var gradient = Assert.IsType<ImmutableRadialGradientBrush>(brush);
        Assert.Equal(new RelativePoint(50, 60, RelativeUnit.Absolute), gradient.Center);
        Assert.Equal(new RelativeScalar(25, RelativeUnit.Absolute), gradient.RadiusX);
    }

    [Fact]
    public void Unknown_Reference_Returns_Null()
    {
        Assert.Null(Resolve("""<rect id="g" width="1" height="1"/>""", "g", DefaultBounds));
        Assert.Null(Resolve("", "missing", DefaultBounds));
    }
}
