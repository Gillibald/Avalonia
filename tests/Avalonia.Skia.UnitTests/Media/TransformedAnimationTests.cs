using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;
using Backend = Avalonia.Skia.UnitTests.Media.SlugGpuRenderingTests.Backend;
using GpuContext = Avalonia.Skia.UnitTests.Media.SlugGpuRenderingTests.GpuContext;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A transform that changes every frame. On a CPU surface or a software GPU the run draws
    /// the batch of its last static frame under the change of transform, allocating and
    /// rasterizing nothing; on a hardware GPU it goes to the Slug tier. Upright zoom gestures
    /// stretch the last static run mask on the CPU and software GPUs. The first frame that
    /// repeats its transform rasterizes again, at that transform.
    /// </summary>
    public class TransformedAnimationTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";
        private const int Width = 520;
        private const int Height = 420;

        public enum Target
        {
            Raster,
            SoftwareGl,
            HardwareGl,
            HardwareAngle,
        }

        public static IEnumerable<object[]> Targets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target };
            }
        }

        private static Matrix Rotation(double degrees)
            => Matrix.CreateRotation(Math.PI * degrees / 180) * Matrix.CreateTranslation(160.4, 120.7);

        [Theory]
        [MemberData(nameof(Targets))]
        public void An_Animated_Rotation_Stretches_The_Settled_Batch_Or_Goes_To_Slug(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8, 32));

            var hardware = target is Target.HardwareGl or Target.HardwareAngle;
            var context = output.Context;
            var glyphMasks = 0;
            var atlasEntries = 0;
            long allocated = 0;

            for (var frame = 0; frame < 24; frame++)
            {
                context.Transform = Rotation(5 + frame * 1.7);

                // Allocation is measured once the guard is engaged and the stretched batch built.
                var before = GC.GetAllocatedBytesForCurrentThread();

                Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, Brushes.Black,
                    TextRenderingMode.Antialias, out var drawnBySlug));

                if (frame > TransformChurnGuard.Threshold)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }

                Assert.Equal(hardware && frame >= TransformChurnGuard.Threshold, drawnBySlug);

                if (frame == TransformChurnGuard.Threshold)
                {
                    glyphMasks = typeface.MaskCache.Count;
                    atlasEntries = typeface.MaskAtlas.Count;
                }
            }

            // Only the frames before the guard engaged built sprite sets; the animation added
            // nothing to the glyph storage after its first stretched frame.
            Assert.Equal(TransformChurnGuard.Threshold, run.TransformedSprites.Count);
            Assert.Equal(glyphMasks, typeface.MaskCache.Count);
            Assert.Equal(atlasEntries, typeface.MaskAtlas.Count);
            Assert.Equal(hardware, run.SlugRunArtifact is not null);

            if (!hardware)
            {
                Assert.True(allocated == 0, $"stretched frames allocated {allocated} bytes");
            }

            Assert.True(output.HasInk(), "the last animation frame drew nothing");
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Stretched_Frame_At_The_Settled_Transform_Equals_The_Settled_Frame(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8.37, 32.61), advanceScale: 0.85);

            var settled = Rotation(23);
            var context = output.Context;

            context.Transform = settled;
            context.DrawGlyphRun(Brushes.Black, run);

            var expected = output.ReadAndClear();

            context.Transform = settled;

            Assert.True(MaskGlyphRunRenderer.TryDrawSettledStretched(context, run, Colors.Black),
                "the run has no settled batch");

            var actual = output.ReadAndClear();

            Assert.True(Array.Exists(expected, b => b != 0));
            TransformedAtlasTests.AssertEqual(expected, actual, target.ToString());
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void The_First_Static_Frame_After_An_Animation_Equals_A_Fresh_Draw(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8.37, 32.61));
            using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8.37, 32.61));

            var context = output.Context;
            var final = Rotation(41.3);

            for (var frame = 0; frame < 12; frame++)
            {
                context.Transform = Rotation(10 + frame * 2.9);
                context.DrawGlyphRun(Brushes.Black, animated);
            }

            // The transform stops at its final value: the last animation frame stretches, the
            // next one repeats the transform and rasterizes.
            context.Transform = final;
            context.DrawGlyphRun(Brushes.Black, animated);
            output.ReadAndClear();

            context.Transform = final;
            context.DrawGlyphRun(Brushes.Black, animated);

            var settled = output.ReadAndClear();

            context.Transform = final;
            context.DrawGlyphRun(Brushes.Black, fresh);

            var expected = output.ReadAndClear();

            Assert.True(Array.Exists(expected, b => b != 0));
            TransformedAtlasTests.AssertEqual(expected, settled, target.ToString());
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void An_Upright_Zoom_Stretches_The_Settled_Run_Mask_Except_On_Hardware_Gpus(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8, 32));

            var hardware = target is Target.HardwareGl or Target.HardwareAngle;
            var context = output.Context;
            var glyphMasks = 0;
            var runMasks = 0;
            long allocated = 0;

            for (var frame = 0; frame < 24; frame++)
            {
                context.Transform = Matrix.CreateScale(1 + frame * 0.05, 1 + frame * 0.05) *
                    Matrix.CreateTranslation(10.25, 12.5);

                var before = GC.GetAllocatedBytesForCurrentThread();

                context.DrawGlyphRun(Brushes.Black, run);

                if (frame > TransformChurnGuard.Threshold)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }

                if (frame == TransformChurnGuard.Threshold)
                {
                    glyphMasks = typeface.MaskCache.Count;
                    runMasks = run.RunMasks.Count;
                }
            }

            Assert.True(output.HasInk(), "the last zoom frame drew nothing");

            if (hardware)
            {
                // A hardware GPU keeps rasterizing every scale.
                Assert.True(typeface.MaskCache.Count > glyphMasks);
                return;
            }

            Assert.Equal(glyphMasks, typeface.MaskCache.Count);
            Assert.Equal(runMasks, run.RunMasks.Count);
            Assert.True(allocated == 0, $"stretched zoom frames allocated {allocated} bytes");

            // Settling on the final scale rasterizes it exactly as a fresh run would.
            using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8, 32));

            output.ReadAndClear();
            context.DrawGlyphRun(Brushes.Black, run);

            var settled = output.ReadAndClear();

            context.DrawGlyphRun(Brushes.Black, fresh);
            TransformedAtlasTests.AssertEqual(output.ReadAndClear(), settled, target.ToString());
        }

        /// <summary>A drawing context on a CPU surface or one of the test GPU contexts.</summary>
        private sealed class TestTarget : IDisposable
        {
            private readonly GpuContext? _gpu;
            private readonly SKSurface _surface;

            private TestTarget(GpuContext? gpu, SKSurface surface)
            {
                _gpu = gpu;
                _surface = surface;
                Context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                {
                    Surface = surface,
                    GrContext = gpu?.GrContext,
                    Dpi = new Vector(96, 96),
                });
            }

            public DrawingContextImpl Context { get; }

            public static TestTarget Create(Target target)
            {
                if (target == Target.Raster)
                {
                    var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    var raster = SKSurface.Create(info);

                    raster.Canvas.Clear(SKColors.Transparent);

                    return new TestTarget(null, raster);
                }

                var gpu = TransformedAtlasTests.CreateGpu(target == Target.HardwareAngle ? Backend.Angle : Backend.NativeGl,
                    software: target == Target.SoftwareGl);
                var surface = SKSurface.Create(gpu.GrContext, true,
                    new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));

                if (surface is null)
                {
                    gpu.Dispose();
                    Assert.Skip("GPU surface creation failed.");
                }

                surface.Canvas.Clear(SKColors.Transparent);

                return new TestTarget(gpu, surface);
            }

            public byte[] ReadAndClear()
            {
                var pixels = Read();

                _surface.Canvas.Clear(SKColors.Transparent);

                return pixels;
            }

            public bool HasInk() => Array.Exists(Read(), b => b != 0);

            private unsafe byte[] Read()
            {
                _gpu?.GrContext.Flush();

                var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                fixed (byte* p = pixels)
                {
                    Assert.True(_surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }

                return pixels;
            }

            public void Dispose()
            {
                Context.Dispose();
                _surface.Dispose();
                _gpu?.GrContext.Flush();
                _gpu?.Dispose();
            }
        }
    }
}
