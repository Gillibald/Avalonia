using System;
using Avalonia.Media;
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
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => TestRasterTuning.Set(t => t with
                {
                    UseColorRecordings = false,
                    UseColorMasks = false,
                    UseColorAtlas = false,
                    BlitPath = GlyphBlitPath.Scalar,
                }));
            }

            using (TextTestApplication.Enter())
            {
                using var surface = SKSurface.Create(new SKImageInfo(8, 8));
                using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });
                var tuning = ((IGlyphTargetContext)context).RasterTuning;

                Assert.True(tuning.UseColorRecordings);
                Assert.True(tuning.UseColorMasks);
                Assert.True(tuning.UseColorAtlas);
                Assert.True(((ITransformedGlyphContext)context).TryGetBlitTarget(out var target));
                Assert.Equal(GlyphMaskBlitter.DetectedPath, target.Path);
            }
        }

        [Fact]
        public void Text_Rasterization_Defaults_Do_Not_Outlive_Application()
        {
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => AvaloniaLocator.CurrentMutable.Bind<TextRasterizationDefaults>().ToConstant(
                    new TextRasterizationDefaults(TextRasterizationMode.Backend, unspecifiedRendersSubpixel: false)));
            }

            using (TextTestApplication.Enter())
            {
                using var surface = SKSurface.Create(new SKImageInfo(8, 8));
                using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });

                // This assembly's applications render managed, unspecified text subpixel.
                Assert.Equal(TextRasterizationMode.Managed, new FontManagerOptions().TextRasterizationMode);
                Assert.True(((IGlyphTargetContext)context).RasterizationDefaults.UnspecifiedRendersSubpixel);
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
