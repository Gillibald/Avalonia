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
    /// A transform that changes every frame. On every context the run draws the batch of its
    /// last static frame under the change of transform, allocating and rasterizing nothing.
    /// Upright zoom gestures stretch the last static run mask on the CPU and software GPUs.
    /// The first frame that repeats its transform rasterizes again, at that transform.
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
        public void An_Animated_Rotation_Stretches_The_Settled_Batch(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8, 32));

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
                    TextRenderingMode.Antialias));

                if (frame > TransformChurnGuard.Threshold)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }

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
            Assert.Null(run.SlugRunArtifact);
            Assert.True(allocated == 0, $"stretched frames allocated {allocated} bytes");
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

        public static IEnumerable<object[]> StretchedRotations()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                foreach (var delta in new[] { 3.0, 17.0, 30.0 })
                {
                    yield return new object[] { target, delta };
                }
            }
        }

        [Theory]
        [MemberData(nameof(StretchedRotations))]
        public void A_Stretched_Rotation_Keeps_The_Ink_Of_The_Settled_Frame(Target target, double delta)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8.37, 32.61));

            var settledTransform = Rotation(20);
            var current = Rotation(20 + delta);
            var context = output.Context;

            context.Transform = settledTransform;
            context.DrawGlyphRun(Brushes.Black, run);

            var settled = Ink(output.ReadAndClear());

            context.Transform = current;

            Assert.True(MaskGlyphRunRenderer.TryDrawSettledStretched(context, run, Colors.Black),
                "the run has no settled batch");

            var stretched = output.ReadAndClear();
            var reference = ResampleSettledFrame(typeface, run, settledTransform, current, Colors.Black);

            // Bilinear resampling under a rotation moves ink only by how far the rotated pixel
            // grid's tent weights deviate from summing to one; the reference evaluates exactly
            // that in double precision. Each pixel either output touches adds at most half a
            // level of output rounding plus one level for the 8-bit subtexel precision of the
            // sampler weights (a weight error of 2^-9 per axis on a 255-level step).
            var touched = 0;
            var expected = 0.0;

            for (var i = 0; i < reference.Length; i++)
            {
                expected += reference[i];

                if (reference[i] > 0 || stretched[i * 4 + 3] != 0)
                {
                    touched++;
                }
            }

            var actual = Ink(stretched);
            var tolerance = Math.Abs(expected - settled) + touched * 1.5;

            TestContext.Current.TestOutputHelper?.WriteLine(FormattableString.Invariant(
                $"{target} {delta} deg: settled {settled}, stretched {actual}, resampled {expected:F0}, tolerance {tolerance:F0}"));

            Assert.True(Math.Abs(actual - settled) <= tolerance, FormattableString.Invariant(
                $"{target} {delta} deg: stretched ink {actual} differs from the settled {settled} by more than {tolerance:F0} levels (bilinear resampling alone gives {expected:F0})"));
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
        public void A_Zoom_Past_The_Stretch_Band_Rasterizes_Once_Per_Crossing_Like_A_Fresh_Draw(Target target)
        {
            // The settled batch stretches while the zoom since it settled stays within a factor
            // of 1.2 either way; past that, one frame rasterizes at its transform and settles.
            const double band = 1.2;

            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8.37, 32.61));

            static Matrix ZoomRotation(double scale)
                => Matrix.CreateScale(scale, scale) * Matrix.CreateRotation(Math.PI * 15 / 180) *
                   Matrix.CreateTranslation(60.4, 40.7);

            // A sawtooth: up to twice the size, then back to exactly the first frame's transform,
            // whose sprite set is still cached, and up again.
            var scales = new List<double>();

            for (var k = 0; k <= 34; k++)
            {
                scales.Add(1 + 0.03 * k);
            }

            for (var k = 0; k <= 10; k++)
            {
                scales.Add(1 + 0.03 * k);
            }

            // The frames before the guard engages rasterize and settle; after that a frame
            // settles only when its zoom leaves the band around the last settled zoom.
            var expected = new List<int>();
            var settledScale = scales[TransformChurnGuard.Threshold - 1];

            for (var frame = TransformChurnGuard.Threshold; frame < scales.Count; frame++)
            {
                var delta = scales[frame] / settledScale;

                if (delta > band || delta < 1 / band)
                {
                    expected.Add(frame);
                    settledScale = scales[frame];
                }
            }

            Assert.True(expected.Count >= 4, $"the sawtooth crosses the band only {expected.Count} times");

            var context = output.Context;
            var crossings = new List<int>();

            for (var frame = 0; frame < scales.Count; frame++)
            {
                var previous = run.TransformedSprites.Settled;

                context.Transform = ZoomRotation(scales[frame]);
                context.DrawGlyphRun(Brushes.Black, run);

                var drawn = output.ReadAndClear();

                if (frame < TransformChurnGuard.Threshold || ReferenceEquals(previous, run.TransformedSprites.Settled))
                {
                    continue;
                }

                crossings.Add(frame);

                // The frame that settles draws exactly what a run drawn once at its transform draws.
                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8.37, 32.61));

                context.Transform = ZoomRotation(scales[frame]);
                context.DrawGlyphRun(Brushes.Black, fresh);

                TransformedAtlasTests.AssertEqual(output.ReadAndClear(), drawn, $"{target} frame {frame}");
            }

            Assert.Equal(expected, crossings);
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

        private static long Ink(byte[] rgba)
        {
            long ink = 0;

            for (var i = 3; i < rgba.Length; i += 4)
            {
                ink += rgba[i];
            }

            return ink;
        }

        /// <summary>
        /// The settled frame's glyph masks, coverage-corrected for <paramref name="color"/>,
        /// drawn under the change from <paramref name="settled"/> to <paramref name="current"/>
        /// as a GPU draws a textured quad per glyph: a pixel whose centre maps inside a glyph's
        /// rectangle samples it bilinearly, zero outside the mask, and glyphs blend source-over
        /// in run order. Returns the alpha per pixel in levels.
        /// </summary>
        private static double[] ResampleSettledFrame(GlyphTypeface typeface, ManagedGlyphRunImpl run,
            Matrix settled, Matrix current, Color color)
        {
            var table = MaskGamma.GetTable(color.R, color.G, color.B);
            var toCurrent = settled.Invert() * current;
            var toSettled = toCurrent.Invert();
            var alpha = new double[Width * Height];

            foreach (var (mask, x, y) in TransformedGlyphRunTests.GlyphMasksAtPens(typeface, run, settled))
            {
                if (mask.IsEmpty)
                {
                    continue;
                }

                var left = x + mask.Left;
                var top = y + mask.Top;
                var bounds = new Rect(left, top, mask.Width, mask.Height).TransformToAABB(toCurrent);

                for (var py = Math.Max(0, (int)bounds.Top - 1); py < Math.Min(Height, (int)bounds.Bottom + 2); py++)
                {
                    for (var px = Math.Max(0, (int)bounds.Left - 1); px < Math.Min(Width, (int)bounds.Right + 2); px++)
                    {
                        var source = new Point(px + 0.5, py + 0.5).Transform(toSettled);
                        var u = source.X - left;
                        var v = source.Y - top;

                        if (u < 0 || v < 0 || u >= mask.Width || v >= mask.Height)
                        {
                            continue;
                        }

                        var a = SampleBilinear(mask, table, u - 0.5, v - 0.5) / 255;
                        ref var pixel = ref alpha[py * Width + px];

                        pixel = a * 255 + pixel * (1 - a);
                    }
                }
            }

            return alpha;
        }

        private static double SampleBilinear(GlyphMask mask, byte[] table, double u, double v)
        {
            var x0 = (int)Math.Floor(u);
            var y0 = (int)Math.Floor(v);
            var tx = u - x0;
            var ty = v - y0;

            double At(int column, int row)
                => column < 0 || row < 0 || column >= mask.Width || row >= mask.Height
                    ? 0
                    : table[mask.Alpha[row * mask.Width + column]];

            return (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) +
                   (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty;
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
