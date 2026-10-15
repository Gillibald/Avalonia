using System;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// What managed text tunes, decides and caches ends with the application that did it: a test
    /// that changes it, or fails halfway through changing it, leaves the next application as a new
    /// process would find it.
    /// </summary>
    public class RasterStateLifetimeTests
    {
        [Fact]
        public void Zoom_Raster_Pixel_Cost_Does_Not_Outlive_Application()
        {
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => TestRasterTuning.Set(t => t with { ZoomRasterPixelCost = 1e6 }));
            }

            using (TextTestApplication.Enter())
            {
                Assert.Equal(5, TuningOfNewContext().ZoomRasterPixelCost);
            }
        }

        /// <summary>The tuning a drawing context created now draws text with.</summary>
        private static GlyphRasterTuning TuningOfNewContext()
        {
            using var surface = SKSurface.Create(new SKImageInfo(8, 8));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });

            return ((IGlyphTargetContext)context).RasterTuning;
        }

        /// <summary>A test setup that makes <paramref name="change"/> and fails before its cleanup is in place.</summary>
        private static void FailingSetup(Action change)
        {
            try
            {
                change();

                throw new InvalidOperationException("setup failed");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
