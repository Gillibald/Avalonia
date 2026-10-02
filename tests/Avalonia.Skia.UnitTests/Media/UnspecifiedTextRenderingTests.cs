using System;
using System.Linq;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Text whose rendering mode is unspecified renders subpixel on a display surface only where
    /// the platform renders it so by default; an explicit request for subpixel text is honoured
    /// everywhere the surface allows it.
    /// </summary>
    public class UnspecifiedTextRenderingTests
    {
        [Fact]
        public void Unspecified_Text_Renders_Grayscale_Where_The_Platform_Does()
        {
            var previous = TextRasterizationDefaults.UnspecifiedRendersSubpixel;

            try
            {
                TextRasterizationDefaults.UnspecifiedRendersSubpixel = false;

                var unspecified = Render(TextRenderingMode.Unspecified);
                var grayscale = Render(TextRenderingMode.Antialias);
                var subpixel = Render(TextRenderingMode.SubpixelAntialias);

                Assert.Equal(grayscale, unspecified);
                Assert.Equal(0, CountChroma(unspecified));
                Assert.True(CountChroma(subpixel) > 0, "explicit subpixel text drew no stripes");

                TextRasterizationDefaults.UnspecifiedRendersSubpixel = true;

                Assert.Equal(subpixel, Render(TextRenderingMode.Unspecified));
            }
            finally
            {
                TextRasterizationDefaults.UnspecifiedRendersSubpixel = previous;
            }
        }

        private static uint[] Render(TextRenderingMode mode)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 15, new Point(6.37, 20.2));

            var info = new SKImageInfo(160, 30, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));

            surface.Canvas.Clear(SKColors.White);

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                context.PushTextOptions(new TextOptions { TextRenderingMode = mode });
                context.DrawGlyphRun(Brushes.Black, run);
                context.PopTextOptions();
            }

            using var pixmap = surface.PeekPixels();

            return pixmap.GetPixelSpan<uint>().ToArray();
        }

        private static int CountChroma(uint[] pixels)
            => pixels.Count(pixel => (byte)pixel != (byte)(pixel >> 8) || (byte)pixel != (byte)(pixel >> 16));
    }
}
