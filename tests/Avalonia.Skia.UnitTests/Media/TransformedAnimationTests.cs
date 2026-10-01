using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A transform that changes every frame. CPU surfaces and hardware GPUs rasterize every
    /// animated frame into transient buffers, drawing exactly what a static frame at that
    /// transform draws while caching nothing; software GPUs draw the batch of the last static
    /// frame under the change of transform. Upright zoom gestures stretch the last static run
    /// mask on software GPUs, and on CPU surfaces within a 1.2x band around it. The first frame
    /// that repeats its transform rasterizes and caches again, at that transform.
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

        /// <summary>The contexts that rasterize animated frames: CPU surfaces and hardware GPUs.</summary>
        public static IEnumerable<object[]> RasterizingTargets()
        {
            yield return new object[] { Target.Raster };
            yield return new object[] { Target.HardwareGl };
            yield return new object[] { Target.HardwareAngle };
        }

        /// <summary>The contexts that stretch the settled batch while animating: software GPUs.</summary>
        public static IEnumerable<object[]> StretchingTargets()
        {
            yield return new object[] { Target.SoftwareGl };
        }

        public static IEnumerable<object[]> RasterizedAnimations()
        {
            foreach (var target in RasterizingTargets())
            {
                yield return new[] { target[0], false };
                yield return new[] { target[0], true };
            }
        }

        private static Matrix Rotation(double degrees)
            => Matrix.CreateRotation(Math.PI * degrees / 180) * Matrix.CreateTranslation(160.4, 120.7);

        private static Matrix ZoomRotation(double scale)
            => Matrix.CreateScale(scale, scale) * Matrix.CreateRotation(Math.PI * 15 / 180) *
               Matrix.CreateTranslation(60.4, 40.7);

        [Theory]
        [MemberData(nameof(RasterizedAnimations))]
        public void An_Animated_Frame_Draws_What_A_Run_Drawn_Once_At_Its_Transform_Draws(Target target, bool zoom)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            var context = output.Context;

            Matrix At(int frame) => zoom ? ZoomRotation(1 + frame * 0.07) : Rotation(5 + frame * 1.7);

            for (var frame = 0; frame < 10; frame++)
            {
                context.Transform = At(frame);
                context.DrawGlyphRun(Brushes.Black, animated);

                var drawn = output.ReadAndClear();

                if (frame < TransformChurnGuard.Threshold)
                {
                    continue;
                }

                // A run drawn once rasterizes at its transform and caches the result.
                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

                context.Transform = At(frame);
                context.DrawGlyphRun(Brushes.Black, fresh);

                var expected = output.ReadAndClear();

                Assert.True(Array.Exists(expected, b => b != 0));
                TransformedAtlasTests.AssertEqual(expected, drawn, $"{target} frame {frame}");
            }

            Assert.Equal(TransformChurnGuard.Threshold, animated.TransformedSprites.Count);
        }

        [Theory]
        [MemberData(nameof(RasterizingTargets))]
        public void Runs_Animated_Together_Draw_What_Runs_Drawn_Once_Draw(Target target)
        {
            // The runs of a frame share its transform and many of their glyph masks; each must
            // still draw exactly its own glyphs, here two lines of different text and size.
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var first = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));
            using var second = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv 9876543210", 18,
                new Point(8.37, 60.2));
            using var third = WideRunMaskTests.CreateRun(typeface, "fonts and hamburgers", 21, new Point(30.1, 90.9));

            var context = output.Context;

            for (var frame = 0; frame < 8; frame++)
            {
                context.Transform = Rotation(5 + frame * 1.7);
                context.DrawGlyphRun(Brushes.Black, first);
                context.DrawGlyphRun(Brushes.Black, second);
                context.DrawGlyphRun(Brushes.Black, third);

                var drawn = output.ReadAndClear();

                if (frame < TransformChurnGuard.Threshold)
                {
                    continue;
                }

                using var freshFirst = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));
                using var freshSecond = WideRunMaskTests.CreateRun(typeface, "Hamburgefonstiv 9876543210", 18,
                    new Point(8.37, 60.2));
                using var freshThird = WideRunMaskTests.CreateRun(typeface, "fonts and hamburgers", 21,
                    new Point(30.1, 90.9));

                context.Transform = Rotation(5 + frame * 1.7);
                context.DrawGlyphRun(Brushes.Black, freshFirst);
                context.DrawGlyphRun(Brushes.Black, freshSecond);
                context.DrawGlyphRun(Brushes.Black, freshThird);

                TransformedAtlasTests.AssertEqual(output.ReadAndClear(), drawn, $"{target} frame {frame}");
            }
        }

        [Theory]
        [MemberData(nameof(RasterizingTargets))]
        public void An_Animated_Rotation_Rasterizes_Without_Caching_Or_Allocating(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 24, new Point(8, 32));

            var context = output.Context;
            var glyphMasks = 0;
            var atlasEntries = 0;
            var atlasBytes = 0L;
            var frames = 0;
            long allocated = 0;

            // Two passes over the same angles: the scratch buffers grow to the largest frame
            // during the first, and the second is measured. Only the first pass's opening frames
            // are cached, so every later frame rasterizes.
            for (var frame = 0; frame < 80; frame++)
            {
                context.Transform = Rotation(5 + frame % 40 * 1.7);

                var before = GC.GetAllocatedBytesForCurrentThread();

                Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(context, run, Brushes.Black,
                    TextRenderingMode.Antialias));

                if (frame >= 40 + TransformChurnGuard.Threshold)
                {
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    frames++;
                }

                if (frame == TransformChurnGuard.Threshold)
                {
                    glyphMasks = typeface.MaskCache.Count;
                    atlasEntries = typeface.MaskAtlas.Count;
                    atlasBytes = typeface.MaskAtlas.AllocatedBytes;
                }
            }

            // Only the frames before the guard engaged built sprite sets and stored glyph masks.
            Assert.Equal(TransformChurnGuard.Threshold, run.TransformedSprites.Count);
            Assert.Equal(glyphMasks, typeface.MaskCache.Count);
            Assert.Equal(atlasEntries, typeface.MaskAtlas.Count);
            Assert.Equal(atlasBytes, typeface.MaskAtlas.AllocatedBytes);
            Assert.True(output.HasInk(), "the last animation frame drew nothing");

            // A CPU surface blends from pooled buffers. A GPU hands each run's coverage to the
            // backend as one transient image, whose wrapper is the only allocation left.
            var perFrame = allocated / frames;

            TestContext.Current.TestOutputHelper?.WriteLine($"{target}: {perFrame} bytes per animated frame");

            Assert.True(target == Target.Raster ? perFrame == 0 : perFrame <= MaxTransientImageBytes,
                $"animated frames allocated {perFrame} bytes each");
        }

        public static IEnumerable<object[]> Obstacles()
        {
            yield return new object[] { "opacity" };
            yield return new object[] { "layer" };
            yield return new object[] { "rounded clip" };
        }

        [Theory]
        [MemberData(nameof(Obstacles))]
        public void An_Animated_Frame_The_Surface_Cannot_Take_Directly_Rasterizes_Through_The_Backend(string obstacle)
        {
            using var output = TestTarget.Create(Target.Raster);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            var context = output.Context;
            var glyphMasks = 0;
            var maxDifference = 0;

            void Draw(ManagedGlyphRunImpl run, Matrix transform)
            {
                switch (obstacle)
                {
                    case "opacity":
                        context.PushOpacity(0.5, null);
                        break;
                    case "layer":
                        context.PushLayer(new Rect(0, 0, Width, Height));
                        break;
                    default:
                        context.PushClip(new RoundedRect(new Rect(0, 0, Width, Height), 4));
                        break;
                }

                context.Transform = transform;
                context.DrawGlyphRun(Brushes.Black, run);
                context.Transform = Matrix.Identity;

                switch (obstacle)
                {
                    case "opacity":
                        context.PopOpacity();
                        break;
                    case "layer":
                        context.PopLayer();
                        break;
                    default:
                        context.PopClip();
                        break;
                }
            }

            for (var frame = 0; frame < 10; frame++)
            {
                Draw(animated, Rotation(5 + frame * 1.7));

                var drawn = output.ReadAndClear();

                if (frame < TransformChurnGuard.Threshold)
                {
                    glyphMasks = typeface.MaskCache.Count;
                    continue;
                }

                Assert.Equal(glyphMasks, typeface.MaskCache.Count);

                // The static frame draws per-glyph pre-tinted bitmaps, the animated one a
                // transient coverage image tinted by the backend. Both blend the same corrected
                // coverage; only an opacity, which the backend folds into its tint, rounds
                // differently, by up to a level.
                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

                Draw(fresh, Rotation(5 + frame * 1.7));

                var expected = output.ReadAndClear();

                Assert.True(Array.Exists(expected, b => b != 0));

                for (var i = 0; i < expected.Length; i++)
                {
                    maxDifference = Math.Max(maxDifference, Math.Abs(expected[i] - drawn[i]));
                }

                glyphMasks = typeface.MaskCache.Count;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{obstacle}: largest difference {maxDifference} levels");

            Assert.Equal(TransformChurnGuard.Threshold, animated.TransformedSprites.Count);
            Assert.True(maxDifference <= 1, $"{obstacle}: animated frames differ from static ones by {maxDifference} levels");
        }

        /// <summary>The managed allocation of one transient image on a GPU context.</summary>
        private const long MaxTransientImageBytes = 256;

        [Theory]
        [MemberData(nameof(StretchingTargets))]
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
            Assert.True(allocated == 0, $"stretched frames allocated {allocated} bytes");
            Assert.True(output.HasInk(), "the last animation frame drew nothing");
        }

        [Theory]
        [MemberData(nameof(StretchingTargets))]
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
            foreach (var target in StretchingTargets())
            {
                foreach (var delta in new[] { 3.0, 17.0, 30.0 })
                {
                    yield return new object[] { target[0], delta };
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

            // The transform stops at its final value: the last animation frame still counts as
            // animating, the next one repeats the transform and rasterizes into the caches.
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
        [MemberData(nameof(StretchingTargets))]
        public void A_Zoom_Past_The_Stretch_Band_Rasterizes_Once_Per_Crossing_Like_A_Fresh_Draw(Target target)
        {
            // The settled batch stretches while the zoom since it settled stays within a factor
            // of 1.2 either way; past that, one frame rasterizes at its transform and settles.
            const double band = 1.2;

            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8.37, 32.61));

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

        [Fact]
        public void An_Upright_Zoom_On_A_Cpu_Surface_Rasterizes_Once_Per_Crossing_Of_The_Stretch_Band()
        {
            // The settled run mask stretches while the zoom since it settled stays within the
            // band either way; past it, one frame rasterizes at its scale and settles.
            const double band = MaskGlyphRunRenderer.MaxStretchScale;

            using var output = TestTarget.Create(Target.Raster);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8.37, 32.61));

            static Matrix Zoom(double scale) => Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(10.25, 12.5);

            var scales = new List<double>();

            for (var k = 0; k <= 40; k++)
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

            Assert.True(expected.Count >= 3, $"the zoom crosses the band only {expected.Count} times");

            var context = output.Context;
            var crossings = new List<int>();

            for (var frame = 0; frame < scales.Count; frame++)
            {
                var previous = run.SettledUpright;
                var glyphMasks = typeface.MaskCache.Count;

                context.Transform = Zoom(scales[frame]);
                context.DrawGlyphRun(Brushes.Black, run);

                var drawn = output.ReadAndClear();

                Assert.True(Array.Exists(drawn, b => b != 0), $"frame {frame} drew nothing");

                if (frame < TransformChurnGuard.Threshold)
                {
                    continue;
                }

                if (Equals(previous, run.SettledUpright))
                {
                    // A stretched frame rasterizes nothing.
                    Assert.Equal(glyphMasks, typeface.MaskCache.Count);
                    continue;
                }

                crossings.Add(frame);

                // The frame that settles draws exactly what a run drawn once at its scale draws.
                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8.37, 32.61));

                context.Transform = Zoom(scales[frame]);
                context.DrawGlyphRun(Brushes.Black, fresh);

                TransformedAtlasTests.AssertEqual(output.ReadAndClear(), drawn, $"frame {frame}");
            }

            Assert.Equal(expected, crossings);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void An_Upright_Zoom_Stretches_The_Settled_Run_Mask_Without_Bound_Only_On_Software_Gpus(Target target)
        {
            using var output = TestTarget.Create(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, Text, 14, new Point(8, 32));

            var rasterizing = target != Target.SoftwareGl;
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

            if (rasterizing)
            {
                // A hardware GPU keeps rasterizing every scale, a CPU surface every time the zoom
                // leaves the stretch band.
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
            private readonly GpuTestContext? _gpu;
            private readonly SKSurface _surface;

            private TestTarget(GpuTestContext? gpu, SKSurface surface)
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

                var gpu = TransformedAtlasTests.CreateGpu(target == Target.HardwareAngle ? GpuBackend.Angle : GpuBackend.NativeGl,
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
                // The surface is read while the context is still drawing.
                Context.FlushGlyphBatch();
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
