using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Xunit;

#if AVALONIA_SKIA
namespace Avalonia.Skia.RenderTests;

/// <summary>
/// Render-coverage for the layer effect graph. Every effect is applied through
/// <see cref="LayerOptions.Effect"/> over one shared scene, so a golden captures
/// what that effect does to known input pixels. The scene deliberately mixes
/// saturated fills, a soft overlap and a thin stroke, since several effects
/// (morphology, convolution, lighting) only differ visibly on edges.
/// </summary>
public class EffectRenderTests : TestBase
{
    private const double Size = 160;

    public EffectRenderTests() : base(@"Media\Effects")
    {
    }

    private async Task Run(IEffect effect, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var target = new EffectRenderer(new LayerOptions
        {
            Bounds = new Rect(0, 0, Size, Size),
            Effect = effect
        })
        {
            Width = Size, Height = Size
        };

        await RenderToFile(target, testName);
        CompareImages(testName, skipImmediate: true);
    }

    [Fact]
    public Task Offset() => Run(new ImmutableOffsetEffect(24, 16));

    [Fact]
    public Task ColorMatrix_Grayscale() => Run(new ImmutableColorMatrixEffect(new double[]
    {
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0,      0,      0,      1, 0
    }));

    [Fact]
    public Task Composite_Chain() => Run(new ImmutableCompositeEffect(new IEffect[]
    {
        new ImmutableColorMatrixEffect(new double[]
        {
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0.2126, 0.7152, 0.0722, 0, 0,
            0,      0,      0,      1, 0
        }),
        new ImmutableOffsetEffect(20, 12)
    }));

    [Fact]
    public Task Flood() => Run(new ImmutableFloodEffect(Colors.MediumVioletRed, 0.6));

    [Fact]
    public Task Merge() => Run(new ImmutableMergeEffect(new IEffect?[]
    {
        new ImmutableOffsetEffect(-18, -12),
        null
    }));

    [Fact]
    public Task Blend_Multiply() => Run(new ImmutableBlendEffect(
        BitmapBlendingMode.Multiply, null, new ImmutableOffsetEffect(22, 22)));

    // Multiply is separable; Xor is Porter-Duff. Both families go through
    // ToSKBlendMode, so one of each is covered.
    [Fact]
    public Task Blend_Screen() => Run(new ImmutableBlendEffect(
        BitmapBlendingMode.Screen, null, new ImmutableOffsetEffect(22, 22)));

    [Fact]
    public Task Blend_Xor() => Run(new ImmutableBlendEffect(
        BitmapBlendingMode.Xor, null, new ImmutableOffsetEffect(22, 22)));

    [Fact]
    public Task ArithmeticComposite() => Run(new ImmutableArithmeticCompositeEffect(
        0, 0.6, 0.6, 0, null, new ImmutableOffsetEffect(20, 0)));

    // The case above leaves k1 and k4 at zero, so neither the fg·bg product term
    // nor the constant offset is exercised. Both are non-zero here.
    [Fact]
    public Task ArithmeticComposite_Product_And_Constant() =>
        Run(new ImmutableArithmeticCompositeEffect(
            0.9, 0.25, 0.25, 0.12, null, new ImmutableOffsetEffect(20, 0)));

    // Tile and Crop require an explicit input; an identity offset stands in for
    // the source graphic that a null input means elsewhere in the graph.
    // The source rect straddles the crimson rect, the blue ellipse and bare
    // canvas, so the repeat is visible; a rect inside one flat fill would tile
    // to a uniform field and assert nothing.
    [Fact]
    public Task Tile() => Run(new ImmutableTileEffect(
        new Rect(55, 45, 45, 45), new Rect(0, 0, Size, Size), new ImmutableOffsetEffect(0, 0)));

    [Fact]
    public Task Morphology_Dilate() => Run(new ImmutableMorphologyEffect(3, 3, dilate: true, null));

    [Fact]
    public Task Morphology_Erode() => Run(new ImmutableMorphologyEffect(2, 2, dilate: false, null));

    // Independent radii: a horizontal-only dilate smears sideways and leaves
    // horizontal edges where they were.
    [Fact]
    public Task Morphology_Anisotropic() =>
        Run(new ImmutableMorphologyEffect(6, 0, dilate: true, null));

    [Fact]
    public Task ComponentTransfer_Invert()
    {
        var invert = new byte[256];
        for (var i = 0; i < 256; i++)
            invert[i] = (byte)(255 - i);

        return Run(new ImmutableComponentTransferEffect(invert, invert, invert, null, null));
    }

    /// <summary>
    /// The alpha channel, which every other transfer case leaves null. Halving
    /// alpha fades the scene into the white drawn under the layer.
    /// </summary>
    [Fact]
    public Task ComponentTransfer_Alpha_Ramp()
    {
        var half = new byte[256];
        for (var i = 0; i < 256; i++)
            half[i] = (byte)(i / 2);

        return Run(new ImmutableComponentTransferEffect(null, null, null, half, null));
    }

    private static readonly double[] EdgeDetectKernel = { 0, -1, 0, -1, 4, -1, 0, -1, 0 };

    // ConvolveMatrixEdgeMode has no render coverage on purpose: Skia produces
    // byte-identical output for Duplicate, Wrap and None here, whether the
    // convolution runs over the whole layer or a cropped input, so a golden per
    // mode would pin three identical images and advertise coverage that does not
    // exist. The divisor, bias and preserveAlpha knobs below do take effect.

    [Fact]
    public Task ConvolveMatrix_EdgeDetect() => Run(new ImmutableConvolveMatrixEffect(
        3, 3, EdgeDetectKernel,
        divisor: 1, bias: 0, targetX: 1, targetY: 1,
        ConvolveMatrixEdgeMode.Duplicate, preserveAlpha: false, null));

    // preserveAlpha inverts into Skia's convolveAlpha flag, so the convolution
    // runs on unpremultiplied colour and leaves alpha untouched.
    [Fact]
    public Task ConvolveMatrix_PreserveAlpha() => Run(new ImmutableConvolveMatrixEffect(
        3, 3, EdgeDetectKernel,
        divisor: 1, bias: 0, targetX: 1, targetY: 1,
        ConvolveMatrixEdgeMode.Duplicate, preserveAlpha: true, null));

    // Bias is an SVG [0, 1] fraction the backend scales to Skia's colour units.
    [Fact]
    public Task ConvolveMatrix_Bias() => Run(new ImmutableConvolveMatrixEffect(
        3, 3, EdgeDetectKernel,
        divisor: 1, bias: 0.4, targetX: 1, targetY: 1,
        ConvolveMatrixEdgeMode.Duplicate, preserveAlpha: false, null));

    [Fact]
    public Task Crop() => Run(new ImmutableCropEffect(
        new Rect(20, 20, 80, 80), new ImmutableOffsetEffect(0, 0)));

    // Each light kind crossed with diffuse/specular reaches a different SkiaSharp
    // constructor, so all six combinations get a golden.
    private static ImmutableLightingEffect Light(
        LightSourceKind kind, bool specular, Color color,
        Point position = default, double z = 0, Point pointsAt = default, double pointsAtZ = 0,
        double spotExponent = 1, double? cone = null, double azimuth = 0, double elevation = 0,
        double surfaceScale = 4, double lightingConstant = 1, double shininess = 1) =>
        new(kind, position, z, pointsAt, pointsAtZ, spotExponent, cone, azimuth, elevation,
            color, surfaceScale, lightingConstant, shininess, specular, null);

    [Fact]
    public Task Lighting_Diffuse() => Run(Light(
        LightSourceKind.Distant, specular: false, Colors.White, azimuth: 45, elevation: 55));

    // Coloured light and a low exponent: a white specular highlight over a white
    // canvas is invisible, which would make the golden assert nothing.
    [Fact]
    public Task Lighting_Specular() => Run(Light(
        LightSourceKind.Point, specular: true, Colors.OrangeRed, new Point(60, 50), z: 30,
        surfaceScale: 10, lightingConstant: 1.4, shininess: 4));

    [Fact]
    public Task Lighting_Distant_Specular() => Run(Light(
        LightSourceKind.Distant, specular: true, Colors.OrangeRed, azimuth: 45, elevation: 40,
        surfaceScale: 10, lightingConstant: 1.4, shininess: 4));

    [Fact]
    public Task Lighting_Point_Diffuse() => Run(Light(
        LightSourceKind.Point, specular: false, Colors.White, new Point(60, 50), z: 30,
        surfaceScale: 6));

    // A spot light adds the cone: exponent and limiting angle only exist here.
    [Fact]
    public Task Lighting_Spot_Diffuse() => Run(Light(
        LightSourceKind.Spot, specular: false, Colors.White, new Point(50, 30), z: 70,
        pointsAt: new Point(90, 100), spotExponent: 2, cone: 35, surfaceScale: 6));

    [Fact]
    public Task Lighting_Spot_Specular() => Run(Light(
        LightSourceKind.Spot, specular: true, Colors.OrangeRed, new Point(50, 30), z: 70,
        pointsAt: new Point(90, 100), spotExponent: 2, cone: 35,
        surfaceScale: 10, lightingConstant: 1.4, shininess: 4));

    // Stitching selects a different SkiaSharp overload, and fractalNoise picks a
    // different generator, so all four combinations get a golden.
    private static readonly Rect StitchTile = new(0, 0, Size / 2, Size / 2);

    [Fact]
    public Task Turbulence() => Run(new ImmutableTurbulenceEffect(
        0.05, 0.05, octaves: 2, seed: 1, fractalNoise: true, stitch: false, default));

    [Fact]
    public Task Turbulence_Fractal_Stitched() => Run(new ImmutableTurbulenceEffect(
        0.05, 0.05, octaves: 2, seed: 1, fractalNoise: true, stitch: true, StitchTile));

    [Fact]
    public Task Turbulence_Noise() => Run(new ImmutableTurbulenceEffect(
        0.05, 0.05, octaves: 2, seed: 1, fractalNoise: false, stitch: false, default));

    [Fact]
    public Task Turbulence_Noise_Stitched() => Run(new ImmutableTurbulenceEffect(
        0.05, 0.05, octaves: 2, seed: 1, fractalNoise: false, stitch: true, StitchTile));

    /// <summary>
    /// A recorded draw list as a filter source: the recording replaces the layer
    /// content, rasterized within the effect bounds. This is the only effect that
    /// runs a nested drawing context inside the backend.
    /// </summary>
    [Fact]
    public Task Recording()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            // The base extends past the bounds below on every side, so the
            // golden shows the rasterization extent as a clean cut.
            ctx.DrawRectangle(Brushes.MediumSlateBlue, null, new Rect(10, 10, 140, 140));
            ctx.DrawEllipse(Brushes.Orange, null, new Rect(45, 45, 70, 70));
            ctx.DrawLine(new ImmutablePen(Brushes.Black, 5), new Point(15, 145), new Point(145, 15));
        });

        return Run(new ImmutableRecordingEffect(recording, new Rect(25, 25, 110, 110)));
    }

    [Fact]
    public Task AnisotropicBlur() => Run(new ImmutableAnisotropicBlurEffect(8, 1, null));

    // A zero radius must leave that axis sharp; the backend branches on it.
    [Fact]
    public Task AnisotropicBlur_Vertical_Only() =>
        Run(new ImmutableAnisotropicBlurEffect(0, 10, null));

    /// <summary>
    /// Draws the shared scene, then opens a layer with the effect under test.
    /// Content drawn inside the layer (if any) composites through the effect
    /// when the layer pops.
    /// </summary>
    private sealed class EffectRenderer : Control
    {
        private readonly LayerOptions _options;

        public EffectRenderer(LayerOptions options)
        {
            _options = options;
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, Size, Size));

            using (context.PushLayer(_options))
                DrawScene(context);
        }

        private static void DrawScene(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Crimson, null, new Rect(20, 20, 70, 55));
            context.DrawEllipse(Brushes.DodgerBlue, null, new Rect(60, 55, 75, 75));
            context.DrawRectangle(Brushes.Gold, null, new Rect(28, 96, 46, 34));
            context.DrawLine(new ImmutablePen(Brushes.Black, 3), new Point(14, 142), new Point(146, 142));
        }
    }
}
#endif
