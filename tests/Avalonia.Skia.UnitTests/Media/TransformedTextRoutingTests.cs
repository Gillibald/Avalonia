using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;
using Backend = Avalonia.Skia.UnitTests.Media.SlugGpuRenderingTests.Backend;
using GpuContext = Avalonia.Skia.UnitTests.Media.SlugGpuRenderingTests.GpuContext;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Where <see cref="DrawingContextImpl.DrawGlyphRun"/> sends the draws the upright mask
    /// tier rejects: transformed masks on every context by default, the Slug tier (GPU) or
    /// the native blob (raster) when the internal routing switch selects Slug.
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

        [Fact]
        public void The_Switch_Sends_Rotated_Raster_Draws_To_The_Native_Blob()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotated text", 24, new Point(8, 32));

            using (RouteTransformedText(TransformedTextRouting.Slug))
            {
                DrawOnRaster(run, s_rotation);
            }

            // A raster context has no Slug tier, so the draw ends on the native blob.
            Assert.Equal(0, run.TransformedSprites.Count);
            Assert.NotNull(run.NativeTextArtifact);
        }

        [Theory]
        [InlineData(Backend.NativeGl)]
        [InlineData(Backend.Angle)]
        public void Rotated_Draws_Take_The_Transformed_Mask_Tier_On_A_Gpu_Context(Backend backend)
        {
            using var gpu = GpuContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotated text", 24, new Point(8, 32));

            DrawOnGpu(gpu!, run, s_rotation);

            Assert.Equal(1, run.TransformedSprites.Count);
            Assert.Null(run.SlugRunArtifact);
            Assert.Null(run.NativeTextArtifact);
        }

        [Theory]
        [InlineData(Backend.NativeGl)]
        [InlineData(Backend.Angle)]
        public void The_Switch_Sends_Rotated_Gpu_Draws_To_Slug(Backend backend)
        {
            using var gpu = GpuContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Rotated text", 24, new Point(8, 32));

            using (RouteTransformedText(TransformedTextRouting.Slug))
            {
                DrawOnGpu(gpu!, run, s_rotation);
            }

            Assert.Equal(0, run.TransformedSprites.Count);
            Assert.NotNull(run.SlugRunArtifact);
        }

        [Fact]
        public void Upright_Draws_Keep_The_Upright_Mask_Tier_Under_Either_Routing()
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Upright text", 24, new Point(8, 32));

            DrawOnRaster(run, Matrix.CreateTranslation(3, 4));

            using (RouteTransformedText(TransformedTextRouting.Slug))
            {
                DrawOnRaster(run, Matrix.CreateTranslation(3.5, 4));
            }

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
                Assert.Equal(0, TextTierDiagnostics.SlugTierDraws);
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

        /// <summary>Selects the transformed-text tier until disposed.</summary>
        internal static IDisposable RouteTransformedText(TransformedTextRouting routing)
        {
            var previous = MaskGlyphRunRenderer.TransformedTextRouting;

            MaskGlyphRunRenderer.TransformedTextRouting = routing;

            return new Restore(previous);
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

        private static void DrawOnGpu(GpuContext gpu, ManagedGlyphRunImpl run, Matrix transform)
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

        private sealed class Restore : IDisposable
        {
            private readonly TransformedTextRouting _previous;

            public Restore(TransformedTextRouting previous) => _previous = previous;

            public void Dispose() => MaskGlyphRunRenderer.TransformedTextRouting = _previous;
        }
    }
}
