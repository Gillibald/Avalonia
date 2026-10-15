using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// On a hardware GPU the atlas is the only storage of the glyph masks an upright zoom lays
    /// out for its band crossings: a crossing rasterizes into a transient buffer, places the
    /// coverage in the atlas and keeps nothing in the glyph mask cache, as transformed text does.
    /// Static frames and the settle frame after the zoom keep their masks in the cache.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class CrossingAtlasStorageTests
    {
        private const int Width = 520;
        private const int Height = 360;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
        };

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
            yield return new object[] { GpuBackend.Vulkan };
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Crossing_Frames_Add_No_Glyph_Masks_To_The_Mask_Cache(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = CreateSurface(gpu);
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            var runs = CreateParagraph(typeface, 3);
            var budget = MotionRasterBudget.BudgetOverride;

            // No frame affords rasterizing sharp, so every moving frame stretches a crossing.
            MotionRasterBudget.BudgetOverride = 0;

            try
            {
                static double Scale(int frame) => 1 + frame * 0.04;

                var scales = Enumerable.Range(0, 30).Select(Scale).ToList();
                var crossingFrames = TransformedAtlasTests.GridCrossingFrames(scales, runs[0], 0);
                var atlas = TransformedAtlasTests.AtlasOf(gpu, typeface);
                var crossings = 0;

                for (var frame = 0; frame < scales.Count; frame++)
                {
                    var masks = typeface.MaskCache.Count;
                    var entries = atlas.Count;

                    Draw(context, runs, scales[frame]);

                    if (frame < TransformChurnGuard.Threshold)
                    {
                        continue;
                    }

                    Assert.True(typeface.MaskCache.Count == masks,
                        $"zoom frame {frame} added {typeface.MaskCache.Count - masks} glyph masks to the cache");

                    if (crossingFrames.Contains(frame))
                    {
                        crossings++;

                        Assert.True(atlas.Count > entries, $"crossing frame {frame} placed nothing in the atlas");
                    }
                }

                Assert.True(crossings >= 3, $"the zoom crossed the band {crossings} times");

                // The frame that holds still grid-fits its glyphs into the mask cache, as static text does.
                var settled = typeface.MaskCache.Count;

                Draw(context, runs, scales[^1]);

                Assert.True(typeface.MaskCache.Count > settled, "the settling frame cached no glyph masks");
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Crossing_Laid_Out_Again_Takes_Its_Glyph_Masks_From_The_Atlas(GpuBackend backend)
        {
            // A long zoom drops the sprite sets of crossings it moved away from, but not their
            // atlas entries. Coming back to one lays its sprites out again from those entries,
            // rasterizing nothing, even when the glyph mask cache holds none of its masks.
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = CreateSurface(gpu);
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            var runs = new[] { CreateDistinctGlyphRun(typeface) };
            var budget = MotionRasterBudget.BudgetOverride;

            MotionRasterBudget.BudgetOverride = 0;

            try
            {
                var revisit = ZoomAwayFromACrossing(context, runs, typeface);
                var builds = GlyphRasterDiagnostics.SpriteSetBuildsOnThread;
                var rasterized = GlyphRasterDiagnostics.GlyphRasterizationsOnThread;

                Draw(context, runs, revisit);

                Assert.Equal(1, GlyphRasterDiagnostics.SpriteSetBuildsOnThread - builds);
                Assert.Equal(0, GlyphRasterDiagnostics.GlyphRasterizationsOnThread - rasterized);
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Crossing_Laid_Out_From_The_Atlas_Counts_As_A_Cache_Hit_For_The_Motion_Estimate(
            GpuBackend backend)
        {
            // The motion budget weighs an upright zoom's estimate by the share of mask pixels that
            // recent unhinted builds had to rasterize. A crossing whose masks the atlas still
            // holds rasterizes none, so its build lowers that share. The run repeats no glyph,
            // so a build that rasterized its masks would raise the share instead.
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = CreateSurface(gpu);
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            var runs = new[] { CreateDistinctGlyphRun(typeface) };
            var budget = MotionRasterBudget.BudgetOverride;

            MotionRasterBudget.BudgetOverride = 0;

            try
            {
                var revisit = ZoomAwayFromACrossing(context, runs, typeface);
                var before = typeface.MaskCache.RecentMissShare(gridFit: false);

                Draw(context, runs, revisit);

                var after = typeface.MaskCache.RecentMissShare(gridFit: false);

                Assert.True(after < before, $"the miss share went from {before:R} to {after:R}");
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;
                DisposeAll(runs);
            }
        }

        [Fact]
        public void Transient_Upright_Glyph_Masks_Equal_The_Cached_Ones()
        {
            // A crossing rasterizes its upright masks into transient buffers; the atlas must
            // receive exactly the coverage and placement the cached build makes, simulated or not.
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var scratch = new GlyphPathBuilder();
            var simulations = new[] { ((ushort)0, false), ((ushort)40, false), ((ushort)0, true), ((ushort)24, true) };
            var compared = 0;

            foreach (var size in new[] { 9.0f, 13.5f, 17.25f, 31f })
            {
                foreach (var c in "Typography 1234567890 Wjgq@%&")
                {
                    var glyph = typeface.CharacterToGlyphMap[c];

                    for (byte phase = 0; phase < GlyphMaskKey.PhaseCount; phase++)
                    {
                        foreach (var mode in new[] { GlyphMaskMode.Antialiased, GlyphMaskMode.Aliased })
                        {
                            foreach (var (embolden, oblique) in simulations)
                            {
                                var key = new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), phase, mode,
                                    GridFit: false, Strong: false, EmboldenQ: embolden, Oblique: oblique);

                                compared += AssertTransientEqualsCached(typeface, scratch, key) ? 1 : 0;
                            }
                        }
                    }
                }
            }

            Assert.True(compared > 1000, $"{compared} masks compared");
        }

        private static bool AssertTransientEqualsCached(GlyphTypeface typeface, GlyphPathBuilder scratch,
            GlyphMaskKey key)
        {
            var cached = GlyphMasks.Build(typeface, scratch, key);
            var transient = GlyphMasks.BuildUprightTransient(typeface, scratch, key, out var rented);

            try
            {
                Assert.Equal(cached.IsEmpty, transient.IsEmpty);

                if (cached.IsEmpty)
                {
                    return false;
                }

                Assert.Equal((cached.Left, cached.Top, cached.Width, cached.Height, cached.PenOffset),
                    (transient.Left, transient.Top, transient.Width, transient.Height, transient.PenOffset));
                Assert.True(cached.Alpha.AsSpan().SequenceEqual(transient.Alpha.AsSpan(0, cached.Width * cached.Height)),
                    $"{key} differs");

                return true;
            }
            finally
            {
                if (rented is not null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Zooms from 1 to 5, far enough that the crossings six grid steps below the top are
        /// dropped, empties the glyph mask cache, and returns a scale whose crossing was dropped.
        /// </summary>
        private static double ZoomAwayFromACrossing(DrawingContextImpl context, ManagedGlyphRunImpl[] runs,
            GlyphTypeface typeface)
        {
            for (var step = 0; step <= 400; step++)
            {
                Draw(context, runs, 1 + 0.01 * step);
            }

            typeface.MaskCache.Clear();

            return MaskGlyphRunRenderer.CrossingScale(5, 1) / Math.Pow(MaskGlyphRunRenderer.MaxStretchScale, 6) * 0.99;
        }

        private static SKSurface CreateSurface(GpuTestContext gpu)
            => SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));

        private static void Draw(DrawingContextImpl context, ManagedGlyphRunImpl[] runs, double scale)
        {
            context.Transform = Matrix.CreateScale(scale, scale);

            foreach (var run in runs)
            {
                context.DrawGlyphRun(Brushes.Black, run);
            }

            context.FlushGlyphBatch();
        }

        private static ManagedGlyphRunImpl[] CreateParagraph(GlyphTypeface typeface, int count)
        {
            var runs = new ManagedGlyphRunImpl[count];

            for (var i = 0; i < count; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], 13,
                    new Point(6.3, 17 + i * 18));
            }

            return runs;
        }

        /// <summary>A run in which every glyph is a different one, so no two of its masks are the same.</summary>
        private static ManagedGlyphRunImpl CreateDistinctGlyphRun(GlyphTypeface typeface)
            => WideRunMaskTests.CreateRun(typeface, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJ", 13, new Point(6.3, 17));

        private static void DisposeAll(ManagedGlyphRunImpl[] runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }
    }
}
