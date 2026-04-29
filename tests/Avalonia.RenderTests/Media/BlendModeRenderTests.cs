using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xunit;

#if AVALONIA_SKIA
namespace Avalonia.Skia.RenderTests;

/// <summary>
/// Render-coverage for every <see cref="BitmapBlendingMode"/> when used as a
/// <see cref="LayerOptions.BlendMode"/>. Ensures both the SVG <c>mix-blend-mode</c>
/// modes (<see cref="BitmapBlendingMode.Multiply"/> .. <see cref="BitmapBlendingMode.Luminosity"/>)
/// and the COLR v1 <c>CompositeMode</c> Porter-Duff modes
/// (<see cref="BitmapBlendingMode.Source"/> .. <see cref="BitmapBlendingMode.Plus"/>)
/// produce the expected pixels through the layer pipeline.
/// </summary>
public class BlendModeRenderTests : TestBase
{
    public BlendModeRenderTests() : base(@"Media\BlendMode")
    {
    }

    // Porter-Duff modes — required by COLR v1 (the colr-v1 CompositeMode enum
    // contains all of these in a 1:1 mapping with our values). Scene uses two
    // partially-overlapping ellipses so alpha varies and modes that depend on
    // the source/destination alpha intersection are visually distinct.
    [Theory]
    [InlineData(BitmapBlendingMode.SourceOver)]
    [InlineData(BitmapBlendingMode.Source)]
    [InlineData(BitmapBlendingMode.Destination)]
    [InlineData(BitmapBlendingMode.DestinationOver)]
    [InlineData(BitmapBlendingMode.SourceIn)]
    [InlineData(BitmapBlendingMode.DestinationIn)]
    [InlineData(BitmapBlendingMode.SourceOut)]
    [InlineData(BitmapBlendingMode.DestinationOut)]
    [InlineData(BitmapBlendingMode.SourceAtop)]
    [InlineData(BitmapBlendingMode.DestinationAtop)]
    [InlineData(BitmapBlendingMode.Xor)]
    [InlineData(BitmapBlendingMode.Plus)]
    public Task PorterDuff(BitmapBlendingMode mode) => RunBlendModeCase(mode, isPorterDuff: true);

    // Separable blend modes — required by SVG mix-blend-mode and COLR v1. Scene
    // uses full-canvas crossed RGB / CMY stripes for rich colour pairings.
    [Theory]
    [InlineData(BitmapBlendingMode.Multiply)]
    [InlineData(BitmapBlendingMode.Screen)]
    [InlineData(BitmapBlendingMode.Overlay)]
    [InlineData(BitmapBlendingMode.Darken)]
    [InlineData(BitmapBlendingMode.Lighten)]
    [InlineData(BitmapBlendingMode.ColorDodge)]
    [InlineData(BitmapBlendingMode.ColorBurn)]
    [InlineData(BitmapBlendingMode.HardLight)]
    [InlineData(BitmapBlendingMode.SoftLight)]
    [InlineData(BitmapBlendingMode.Difference)]
    [InlineData(BitmapBlendingMode.Exclusion)]
    public Task Separable(BitmapBlendingMode mode) => RunBlendModeCase(mode, isPorterDuff: false);

    // Non-separable HSL blend modes — required by SVG mix-blend-mode and COLR v1.
    [Theory]
    [InlineData(BitmapBlendingMode.Hue)]
    [InlineData(BitmapBlendingMode.Saturation)]
    [InlineData(BitmapBlendingMode.Color)]
    [InlineData(BitmapBlendingMode.Luminosity)]
    public Task NonSeparable(BitmapBlendingMode mode) => RunBlendModeCase(mode, isPorterDuff: false);

    private async Task RunBlendModeCase(BitmapBlendingMode mode, bool isPorterDuff)
    {
        var target = new BlendModeRenderer(mode, isPorterDuff)
        {
            Width = 180, Height = 180
        };

        var testName = "BlendMode_" + mode;
        await RenderToFile(target, testName);
        CompareImages(testName, skipImmediate: true);
    }

    /// <summary>
    /// Renders one of two scenes depending on which mode family is being tested:
    ///
    ///   Porter-Duff: a red ellipse (destination) overlapping a blue ellipse
    ///   (source) on a white canvas. Each blend mode produces a distinct shape:
    ///   the source-only crescent, destination-only crescent, intersection lens,
    ///   and outside-both region all behave differently per mode.
    ///
    ///   Color blends (separable + HSL): a full-canvas backdrop of three
    ///   horizontal RGB stripes overlaid with three vertical CMY stripes inside
    ///   the layer. The 3×3 grid of colour pairings makes each mode's colour
    ///   formula visually distinct.
    /// </summary>
    private sealed class BlendModeRenderer : Control
    {
        private readonly BitmapBlendingMode _mode;
        private readonly bool _isPorterDuff;

        public BlendModeRenderer(BitmapBlendingMode mode, bool isPorterDuff)
        {
            _mode = mode;
            _isPorterDuff = isPorterDuff;
        }

        public override void Render(DrawingContext context)
        {
            const double size = 180;

            // Opaque white canvas — non-source, non-destination regions resolve
            // to white in the output (rather than transparent black, which the
            // diff isn't tuned for).
            context.FillRectangle(Brushes.White, new Rect(0, 0, size, size));

            if (_isPorterDuff)
                RenderPorterDuff(context, size);
            else
                RenderColourBlend(context, size);
        }

        private void RenderPorterDuff(DrawingContext context, double size)
        {
            // Destination: red ellipse offset to the upper-left.
            context.DrawEllipse(Brushes.Red, null, new Rect(20, 20, 110, 110));

            // Source: blue ellipse offset to the lower-right, overlapping
            // partially. Drawn inside a layer with the requested blend mode.
            using (context.PushLayer(new LayerOptions { BlendMode = _mode }))
                context.DrawEllipse(Brushes.Blue, null, new Rect(50, 50, 110, 110));
        }

        private void RenderColourBlend(DrawingContext context, double size)
        {
            var third = size / 3;

            // Backdrop: three horizontal RGB stripes (the destination).
            context.FillRectangle(Brushes.Red,   new Rect(0, 0,         size, third));
            context.FillRectangle(Brushes.Lime,  new Rect(0, third,     size, third));
            context.FillRectangle(Brushes.Blue,  new Rect(0, 2 * third, size, third));

            // Foreground inside a layer: vertical CMY stripes (the source).
            using (context.PushLayer(new LayerOptions { BlendMode = _mode }))
            {
                context.FillRectangle(Brushes.Cyan,    new Rect(0,         0, third, size));
                context.FillRectangle(Brushes.Magenta, new Rect(third,     0, third, size));
                context.FillRectangle(Brushes.Yellow,  new Rect(2 * third, 0, third, size));
            }
        }
    }
}
#endif
