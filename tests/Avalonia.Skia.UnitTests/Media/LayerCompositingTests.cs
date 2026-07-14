using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Pixel-level checks for <see cref="DrawingContext.PushLayer"/> on the Skia backend, using the
    /// exact layer shape the COLR v1 painter emits for a PaintComposite: an isolated SourceOver
    /// wrapper around the whole group, and an isolated blend-mode layer around the source paint.
    /// </summary>
    public class LayerCompositingTests
    {
        private const int Canvas = 256;

        [Fact]
        public void Isolated_SourceIn_Layer_Composites_Against_The_Group_Not_The_Page()
        {
            using var app = UnitTestApplication.Start(
                TestServices.MockPlatformRenderInterface.With(renderInterface: new PlatformRenderInterface(null)));

            var info = new SKImageInfo(Canvas, Canvas, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var impl = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            using (var context = new PlatformDrawingContext(impl, false))
            {
                // Wrapper: the whole composite group is isolated from the white page.
                using (context.PushLayer(new LayerOptions { Isolate = true }))
                {
                    // Backdrop of the group: a red square.
                    context.FillRectangle(Brushes.Red, new Rect(20, 20, 100, 100));

                    // Source: a blue square composited onto the group with SourceIn — it must
                    // survive only where the group already has content (the red square), and the
                    // rest of the group must be erased, NOT the page below.
                    using (context.PushLayer(new LayerOptions
                    {
                        BlendMode = BitmapBlendingMode.SourceIn,
                        Isolate = true,
                    }))
                    {
                        context.FillRectangle(Brushes.Blue, new Rect(70, 70, 100, 100));
                    }
                }
            }

            // Overlap of both squares: blue survived the SourceIn.
            AssertPixel(bitmap, 95, 95, SKColors.Blue);

            // Red-only area: erased by the SourceIn restore (source transparent there), so the
            // white page below the isolated group shows through — not red.
            AssertPixel(bitmap, 30, 30, SKColors.White);

            // Blue-only area: no group content beneath it, so nothing survives.
            AssertPixel(bitmap, 150, 150, SKColors.White);

            // Far corner: the page is untouched — the SourceIn applied inside the wrapper, not
            // against the page.
            AssertPixel(bitmap, 220, 220, SKColors.White);
        }

        [Fact]
        public void Layer_Opacity_Applies_To_The_Group_As_A_Whole()
        {
            using var app = UnitTestApplication.Start(
                TestServices.MockPlatformRenderInterface.With(renderInterface: new PlatformRenderInterface(null)));

            var info = new SKImageInfo(Canvas, Canvas, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var impl = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            using (var context = new PlatformDrawingContext(impl, false))
            using (context.PushLayer(new LayerOptions { Opacity = 0.5 }))
            {
                // Two overlapping opaque reds: group opacity must blend them INSIDE the layer
                // first, so the overlap is the same 50% red as the rest — not double-covered.
                context.FillRectangle(Brushes.Red, new Rect(20, 20, 100, 100));
                context.FillRectangle(Brushes.Red, new Rect(70, 70, 100, 100));
            }

            var single = bitmap.GetPixel(30, 30);
            var overlap = bitmap.GetPixel(95, 95);

            Assert.True(single.Green > 100 && single.Green < 155,
                $"Expected ~50% red over white at the single-covered pixel, got {single}");
            Assert.Equal(single, overlap);
        }

        private static void AssertPixel(SKBitmap bitmap, int x, int y, SKColor expected)
        {
            var actual = bitmap.GetPixel(x, y);

            Assert.True(Diff(actual.Red, expected.Red) <= 4 &&
                        Diff(actual.Green, expected.Green) <= 4 &&
                        Diff(actual.Blue, expected.Blue) <= 4,
                $"Pixel ({x},{y}): expected {expected}, got {actual}");
        }

        private static int Diff(byte a, byte b) => a > b ? a - b : b - a;
    }
}
