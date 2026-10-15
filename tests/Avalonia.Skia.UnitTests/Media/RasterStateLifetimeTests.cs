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

        [Fact]
        public void Skia_Text_Seams_Do_Not_Outlive_Application()
        {
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => AvaloniaLocator.CurrentMutable.Bind<SkiaOptions>().ToConstant(new SkiaOptions
                {
                    MaxPendingGlyphBatches = 1,
                    StretchVerticesMinSprites = 99,
                    ResidentGlyphPages = false,
                    TintTextTiers = true,
                    CountTextTiers = true,
                }));
            }

            using (TextTestApplication.Enter())
            {
                using var surface = SKSurface.Create(new SKImageInfo(8, 8));
                using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });

                Assert.Equal(32, context.MaxPendingBatches);
                Assert.Equal(4, context.StretchVerticesMinSprites);
                Assert.False(context.TintsTextTiers);
                Assert.False(context.CountsTextTiers);

                // What the platform setup creates the render interface from.
                Assert.True((AvaloniaLocator.Current.GetService<SkiaOptions>() ?? new SkiaOptions()).ResidentGlyphPages);
            }
        }

        [Fact]
        public void Phase_Timers_Switched_On_Do_Not_Record_In_The_Next_Application()
        {
            using (TextTestApplication.Enter())
            {
                FailingSetup(() => GlyphRasterServices.Current.Diagnostics.PhaseTimersEnabled = true);
            }

            using (WideRunMaskTests.CreateEnvironment(out var typeface))
            {
                var timers = GlyphRasterServices.Current.ForCurrentThread().PhaseTimers;
                var before = timers.GetCount(GlyphTimerPhase.GlyphRun);

                DrawText(typeface);

                Assert.Equal(before, timers.GetCount(GlyphTimerPhase.GlyphRun));
            }
        }

        [Fact]
        public void Motion_Hold_Does_Not_Carry_Into_Next_Application()
        {
            // A budget a run of 15 glyphs at a microsecond each does not fit, and one it fits many
            // times over; neither depends on the machine.
            var tight = GlyphRasterTuning.Default with
            {
                MotionBudgetMicroseconds = 10, MotionGlyphCostMicroseconds = 1, MotionNonTextMicroseconds = 0,
            };
            var ample = tight with { MotionBudgetMicroseconds = 1e9 };

            using (WideRunMaskTests.CreateEnvironment(out var typeface))
            using (var run = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 15, new Point(4.3, 20)))
            using (GlyphCacheBudget.Shared.BeginFrame())
            {
                // The try runs out of budget: moving runs stretch for the hold that follows.
                Assert.False(GlyphRasterServices.Current.ForCurrentThread().Motion.PrefersSharp(run, 15, 1, tight));
            }

            using (WideRunMaskTests.CreateEnvironment(out var typeface))
            using (var run = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 15, new Point(4.3, 20)))
            using (GlyphCacheBudget.Shared.BeginFrame())
            {
                Assert.True(GlyphRasterServices.Current.ForCurrentThread().Motion.PrefersSharp(run, 15, 1, ample));
            }
        }

        [Fact]
        public void Unsettled_Draws_And_Raster_Ticks_Of_A_New_Application_Start_At_Zero()
        {
            using (WideRunMaskTests.CreateEnvironment(out var typeface))
            {
                // A zoom on a CPU surface where rasterizing costs more than stretching: the frames
                // after the motion is recognized stretch the settled mask.
                TestRasterTuning.Set(t => t with { ZoomRasterPixelCost = 1e6 });

                ZoomOnRaster(typeface);

                Assert.NotEqual(0, MaskGlyphRunRenderer.UnsettledDraws);
                Assert.NotEqual(0, GlyphRasterCost.TicksOnThread);
            }

            using (WideRunMaskTests.CreateEnvironment(out _))
            {
                Assert.Equal(0, MaskGlyphRunRenderer.UnsettledDraws);
                Assert.Equal(0, GlyphRasterCost.TicksOnThread);
            }
        }

        /// <summary>Draws ten frames of a run zooming on a CPU surface, through one drawing context.</summary>
        private static void ZoomOnRaster(GlyphTypeface typeface)
        {
            using var run = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 18, new Point(8.37, 32.61));
            using var surface = SKSurface.Create(new SKImageInfo(400, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface, Dpi = new Vector(96, 96),
            });

            for (var frame = 0; frame < 10; frame++)
            {
                context.Transform = Matrix.CreateScale(1 + 0.02 * frame, 1 + 0.02 * frame) *
                                    Matrix.CreateTranslation(10.25, 12.5);
                context.DrawGlyphRun(Brushes.Black, run);
            }
        }

        /// <summary>Draws a run of text through a drawing context of the current application.</summary>
        private static void DrawText(GlyphTypeface typeface)
        {
            using var run = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv", 15, new Point(4.3, 20));
            using var surface = SKSurface.Create(new SKImageInfo(160, 32));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo { Surface = surface });

            context.DrawGlyphRun(Brushes.Black, run);
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
