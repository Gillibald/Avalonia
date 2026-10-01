using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Where <see cref="DrawingContextImpl.DrawGlyphRun"/> sends the draws the upright mask
    /// tier rejects: transformed masks on every context.
    /// </summary>
    public class TransformedTextRoutingTests
    {
        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 22.5 / 180) *
            Matrix.CreateTranslation(60, 40);

        [Fact]
        public void Rotated_Draws_Take_The_Transformed_Mask_Tier_On_A_Raster_Context()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotated text", 24, new Point(8, 32));

            DrawOnRaster(run, s_rotation);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.Null(run.NativeTextArtifact);
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void Rotated_Draws_Take_The_Transformed_Mask_Tier_On_A_Gpu_Context(GpuBackend backend)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotated text", 24, new Point(8, 32));

            DrawOnGpu(gpu!, run, s_rotation);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.Null(run.NativeTextArtifact);
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl)]
        [InlineData(GpuBackend.Angle)]
        public void An_Animated_Rotation_On_A_Gpu_Takes_The_Transformed_Mask_Tier(GpuBackend backend)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotating text", 24, new Point(8, 32));

            var info = new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu!.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var counting = TextTierDiagnostics.CountTiers;

            TextTierDiagnostics.CountTiers = true;
            TextTierDiagnostics.ResetCounters();

            try
            {
                using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                       {
                           Surface = surface,
                           GrContext = gpu.GrContext,
                           Dpi = new Vector(96, 96),
                       }))
                {
                    for (var frame = 0; frame < 12; frame++)
                    {
                        context.Transform = Matrix.CreateRotation(Math.PI * (10 + frame * 2.3) / 180) *
                            Matrix.CreateTranslation(60, 40);
                        context.DrawGlyphRun(Brushes.Black, run);
                    }
                }

                gpu.GrContext.Flush();

                // A hardware GPU rasterizes every animated frame into transient buffers.
                Assert.Equal(12, TextTierDiagnostics.TransformedMaskTierDraws);
            }
            finally
            {
                TextTierDiagnostics.CountTiers = counting;
                TextTierDiagnostics.ResetCounters();
            }
        }

        [Fact]
        public void Upright_Draws_Keep_The_Upright_Mask_Tier()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Upright text", 24, new Point(8, 32));

            DrawOnRaster(run, Matrix.CreateTranslation(3, 4));
            DrawOnRaster(run, Matrix.CreateTranslation(3.5, 4));

            Assert.Equal(2, run.RunMasks.Count);
            Assert.Equal(0, run.TransformedSprites.Count);
            Assert.Null(run.NativeTextArtifact);
        }

        [Fact]
        public void Transformed_Draws_Count_As_Their_Own_Tier()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Counted text", 24, new Point(8, 32));

            var counting = TextTierDiagnostics.CountTiers;

            TextTierDiagnostics.CountTiers = true;
            TextTierDiagnostics.ResetCounters();

            try
            {
                DrawOnRaster(run, s_rotation);
                DrawOnRaster(run, Matrix.CreateTranslation(3, 4));

                Assert.Equal(1, TextTierDiagnostics.TransformedMaskTierDraws);
                Assert.Equal(1, TextTierDiagnostics.MaskTierDraws);
                Assert.Equal(0, TextTierDiagnostics.BlobTierDraws);
            }
            finally
            {
                TextTierDiagnostics.CountTiers = counting;
                TextTierDiagnostics.ResetCounters();
            }
        }

        [Fact]
        public void A_Warm_Transformed_Frame_Allocates_Nothing()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Warm rotated frames", 24, new Point(8, 32));

            var info = new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            context.Transform = s_rotation;

            // The cold draw composes and caches; the second settles pools and the image wrap.
            context.DrawGlyphRun(Brushes.Black, run);
            context.DrawGlyphRun(Brushes.Black, run);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < 100; i++)
            {
                context.DrawGlyphRun(Brushes.Black, run);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"100 warm transformed draws allocated {allocated} bytes");
        }

        private static void DrawOnRaster(ManagedGlyphRunImpl run, Matrix transform)
        {
            var info = new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            context.Transform = transform;
            context.DrawGlyphRun(Brushes.Black, run);
        }

        private static void DrawOnGpu(GpuTestContext gpu, ManagedGlyphRunImpl run, Matrix transform)
        {
            var info = new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                   }))
            {
                context.Transform = transform;
                context.DrawGlyphRun(Brushes.Black, run);
            }

            gpu.GrContext.Flush();
        }
    }
}
