using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

    [Fact]
    public Task ArithmeticComposite() => Run(new ImmutableArithmeticCompositeEffect(
        0, 0.6, 0.6, 0, null, new ImmutableOffsetEffect(20, 0)));

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

    [Fact]
    public Task ComponentTransfer_Invert()
    {
        var invert = new byte[256];
        for (var i = 0; i < 256; i++)
            invert[i] = (byte)(255 - i);

        return Run(new ImmutableComponentTransferEffect(invert, invert, invert, null, null));
    }

    [Fact]
    public Task ConvolveMatrix_EdgeDetect() => Run(new ImmutableConvolveMatrixEffect(
        3, 3,
        new double[] { 0, -1, 0, -1, 4, -1, 0, -1, 0 },
        divisor: 1, bias: 0, targetX: 1, targetY: 1,
        ConvolveMatrixEdgeMode.Duplicate, preserveAlpha: false, null));

    [Fact]
    public Task Crop() => Run(new ImmutableCropEffect(
        new Rect(20, 20, 80, 80), new ImmutableOffsetEffect(0, 0)));

    [Fact]
    public Task Lighting_Diffuse() => Run(new ImmutableLightingEffect(
        LightSourceKind.Distant, default, 0, default, 0, 1, null,
        azimuth: 45, elevation: 55, Colors.White,
        surfaceScale: 4, lightingConstant: 1, shininess: 1, specular: false, null));

    // Coloured light and a low exponent: a white specular highlight over a white
    // canvas is invisible, which would make the golden assert nothing.
    [Fact]
    public Task Lighting_Specular() => Run(new ImmutableLightingEffect(
        LightSourceKind.Point, new Point(60, 50), 30, default, 0, 1, null,
        azimuth: 0, elevation: 0, Colors.OrangeRed,
        surfaceScale: 10, lightingConstant: 1.4, shininess: 4, specular: true, null));

    [Fact]
    public Task Turbulence() => Run(new ImmutableTurbulenceEffect(
        0.05, 0.05, octaves: 2, seed: 1, fractalNoise: true, stitch: false, default));

    [Fact]
    public Task AnisotropicBlur() => Run(new ImmutableAnisotropicBlurEffect(8, 1, null));

    /// <summary>
    /// The backdrop path: the layer is initialized with a blurred copy of what
    /// is already on the surface, then a translucent wash is drawn over it. This
    /// is the only effect that filters the content behind the layer rather than
    /// the layer's own content, so a plain composite-time filter cannot express it.
    /// </summary>
    [Fact]
    public async Task Backdrop_Blur()
    {
        var target = new EffectRenderer(new LayerOptions
        {
            Bounds = new Rect(30, 45, 100, 70),
            BackdropEffect = new ImmutableBlurEffect(6)
        },
        overlay: ctx => ctx.FillRectangle(
            new ImmutableSolidColorBrush(Colors.White, 0.35), new Rect(30, 45, 100, 70)))
        {
            Width = Size, Height = Size
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    // The band the backdrop layer filters. Kept clear of the canvas edges so the
    // unfiltered surround stays visible for comparison.
    private static readonly Rect BackdropBand = new(18, 46, 124, 68);

    private static readonly double[] GrayscaleMatrix =
    {
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0,      0,      0,      1, 0
    };

    private static Bitmap LoadStar()
    {
        var directory = Path.GetDirectoryName(typeof(EffectRenderTests).Assembly.Location)!;
        return new Bitmap(Path.Join(directory, "Assets", "Star512.png"));
    }

    /// <summary>
    /// Renders a bitmap backdrop, then a layer whose <see cref="LayerOptions.BackdropEffect"/>
    /// filters it. Photo-like input is what these filters exist for, and it shows
    /// the detail loss a flat vector scene hides. <paramref name="wash"/> adds the
    /// translucent overlay of real frosted-glass usage; without it the band shows
    /// the filtered backdrop alone.
    /// </summary>
    /// <remarks>
    /// The wash is not cosmetic: a backdrop layer that draws nothing inside
    /// itself composites to a no-op, so every case here puts content in the layer.
    /// </remarks>
    private async Task RunImageBackdrop(
        IEffect backdrop, bool wash = true,
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        using var image = LoadStar();

        var target = new EffectRenderer(
            new LayerOptions { Bounds = BackdropBand, BackdropEffect = backdrop },
            overlay: ctx =>
            {
                if (wash)
                    ctx.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.3), BackdropBand);
            },
            background: image)
        {
            Width = Size, Height = Size
        };

        await RenderToFile(target, testName);
        CompareImages(testName, skipImmediate: true);
    }

    [Fact]
    public Task Backdrop_Blur_Over_Image() => RunImageBackdrop(new ImmutableBlurEffect(6), wash: true);

    [Fact]
    public Task Backdrop_Grayscale_Over_Image() =>
        RunImageBackdrop(new ImmutableColorMatrixEffect(GrayscaleMatrix));

    [Fact]
    public Task Backdrop_Invert_Over_Image()
    {
        var invert = new byte[256];
        for (var i = 0; i < 256; i++)
            invert[i] = (byte)(255 - i);

        return RunImageBackdrop(new ImmutableComponentTransferEffect(invert, invert, invert, null, null));
    }

    [Fact]
    public Task Backdrop_AnisotropicBlur_Over_Image() =>
        RunImageBackdrop(new ImmutableAnisotropicBlurEffect(10, 1, null));

    [Fact]
    public Task Backdrop_Chain_Over_Image() =>
        RunImageBackdrop(new ImmutableCompositeEffect(new IEffect[]
        {
            new ImmutableBlurEffect(4),
            new ImmutableColorMatrixEffect(GrayscaleMatrix)
        }));

    /// <summary>
    /// Draws the shared scene (or a bitmap), then opens a layer with the effect
    /// under test. Content drawn inside the layer (if any) composites through
    /// the effect when the layer pops.
    /// </summary>
    private sealed class EffectRenderer : Control
    {
        private readonly LayerOptions _options;
        private readonly Action<DrawingContext>? _overlay;
        private readonly IImage? _background;

        public EffectRenderer(
            LayerOptions options, Action<DrawingContext>? overlay = null, IImage? background = null)
        {
            _options = options;
            _overlay = overlay;
            _background = background;
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, Size, Size));

            if (_overlay != null)
            {
                // Backdrop case: the background is what the layer filters, so it
                // is drawn outside the layer and the layer only carries the wash.
                DrawBackground(context);

                // LayerOptions.Bounds only hints the offscreen size, it does not
                // clip, so a backdrop confined to a region needs a real clip -
                // otherwise the filter reaches the whole surface. This is the
                // shape any frosted-glass panel takes.
                using (context.PushClip(_options.Bounds ?? new Rect(0, 0, Size, Size)))
                using (context.PushLayer(_options))
                    _overlay(context);
                return;
            }

            using (context.PushLayer(_options))
                DrawBackground(context);
        }

        private void DrawBackground(DrawingContext context)
        {
            if (_background is { } image)
            {
                context.DrawImage(image, new Rect(0, 0, Size, Size));

                // The asset is a black outline on opaque white, and grayscale is
                // identity over pure black and white, so tint it into quadrants:
                // the backdrop then carries both image detail and real colour and
                // a colour filter has something to change.
                var half = Size / 2;
                Tint(context, Colors.Crimson, new Rect(0, 0, half, half));
                Tint(context, Colors.DodgerBlue, new Rect(half, 0, half, half));
                Tint(context, Colors.Gold, new Rect(0, half, half, half));
                Tint(context, Colors.MediumSeaGreen, new Rect(half, half, half, half));
            }
            else
            {
                DrawScene(context);
            }

            static void Tint(DrawingContext context, Color color, Rect rect) =>
                context.FillRectangle(new ImmutableSolidColorBrush(color, 0.55), rect);
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
