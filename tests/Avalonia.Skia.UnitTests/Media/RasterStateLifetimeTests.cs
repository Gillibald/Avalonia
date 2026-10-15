using System;
using Avalonia.Media.Fonts.Rasterization;
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
            var cost = MaskGlyphRunRenderer.ZoomRasterPixelCost;

            try
            {
                using (TextTestApplication.Enter())
                {
                    FailingSetup(() => MaskGlyphRunRenderer.ZoomRasterPixelCost = 1e6);
                }

                using (TextTestApplication.Enter())
                {
                    Assert.Equal(5, MaskGlyphRunRenderer.ZoomRasterPixelCost);
                }
            }
            finally
            {
                MaskGlyphRunRenderer.ZoomRasterPixelCost = cost;
            }
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
