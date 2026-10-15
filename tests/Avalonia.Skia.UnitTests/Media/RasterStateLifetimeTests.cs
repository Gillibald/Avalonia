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

        [Fact]
        public void Motion_Budget_Overrides_Do_Not_Outlive_Application()
        {
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => TestRasterTuning.Set(t => t with
                {
                    MotionBudgetMicroseconds = 1e9,
                    MotionGlyphCostMicroseconds = 1,
                    MotionFrameIntervalMicroseconds = 1e6,
                    MotionNonTextMicroseconds = 0,
                }));
            }

            using (TextTestApplication.Enter())
            {
                var tuning = TuningOfNewContext();

                // The motion budget of this assembly's applications affords nothing.
                Assert.Equal(0, tuning.MotionBudgetMicroseconds);
                Assert.Null(tuning.MotionGlyphCostMicroseconds);
                Assert.Null(tuning.MotionFrameIntervalMicroseconds);
                Assert.Null(tuning.MotionNonTextMicroseconds);
            }
        }

        [Fact]
        public void Colour_Glyph_And_Blit_Path_Switches_Do_Not_Outlive_Application()
        {
            var recordings = ColorGlyphRunSplitter.UseRecordings;
            var masks = ColorGlyphRunSplitter.UseColorMasks;
            var atlas = ColorGlyphRunSplitter.UseColorAtlas;
            var path = GlyphMaskBlitter.Path;

            try
            {
                using (TextTestApplication.Enter())
                {
                    FailingSetup(() =>
                    {
                        ColorGlyphRunSplitter.UseRecordings = false;
                        ColorGlyphRunSplitter.UseColorMasks = false;
                        ColorGlyphRunSplitter.UseColorAtlas = false;
                        GlyphMaskBlitter.Path = GlyphBlitPath.Scalar;
                    });
                }

                using (TextTestApplication.Enter())
                {
                    Assert.True(ColorGlyphRunSplitter.UseRecordings);
                    Assert.True(ColorGlyphRunSplitter.UseColorMasks);
                    Assert.True(ColorGlyphRunSplitter.UseColorAtlas);
                    Assert.Equal(path, GlyphMaskBlitter.Path);
                }
            }
            finally
            {
                ColorGlyphRunSplitter.UseRecordings = recordings;
                ColorGlyphRunSplitter.UseColorMasks = masks;
                ColorGlyphRunSplitter.UseColorAtlas = atlas;
                GlyphMaskBlitter.Path = path;
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
