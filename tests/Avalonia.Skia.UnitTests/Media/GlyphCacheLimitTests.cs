using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The global glyph cache limit as drawing meets it. These tests change the process-wide
    /// limit and measure process-wide bytes, so they run alone.
    /// </summary>
    [Collection(nameof(GlyphCacheLimitTests))]
    [CollectionDefinition(nameof(GlyphCacheLimitTests), DisableParallelization = true)]
    public class GlyphCacheLimitTests
    {
        private const int Width = 900;
        private const int Height = 700;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
            "Pack my box with five dozen liquor jugs; sphinx of black",
            "quartz, judge my vow! Numbers such as 1234567890 appear.",
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
        public void A_Zoom_Revisit_On_A_Hardware_Gpu_Rasterizes_No_Glyph_When_Its_Masks_Fit_The_Limit(GpuBackend backend)
        {
            using var limit = LimitScope.Set(GlyphCacheBudget.DefaultLimitBytes);
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var runs = CreateParagraph(typeface, 10, 28);

            // One frame per scale, as a zoom gesture draws.
            void Sweep()
            {
                for (var step = 0; step < 120; step++)
                {
                    using var context = TransformedAtlasTests.CreateContext(gpu, surface);
                    var scale = 1 + step * 0.01;

                    context.Transform = Matrix.CreateScale(scale, scale);

                    foreach (var run in runs)
                    {
                        context.DrawGlyphRun(Brushes.Black, run);
                    }

                    context.FlushGlyphBatch();
                }
            }

            try
            {
                Sweep();

                // More than a typeface kept on its own before the limit was global.
                Assert.True(typeface.MaskCache.TotalCost > 8 * 1024 * 1024,
                    $"the sweep made only {typeface.MaskCache.TotalCost} bytes of masks");

                var misses = GlyphRasterDiagnostics.MaskCacheMissesOnThread;

                Sweep();

                Assert.Equal(0, GlyphRasterDiagnostics.MaskCacheMissesOnThread - misses);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Fact]
        public void Static_Text_Holds_Only_Its_Working_Set()
        {
            using var limit = LimitScope.Set(GlyphCacheBudget.DefaultLimitBytes);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var runs = CreateParagraph(typeface, 12, 14);
            var budget = GlyphCacheBudget.Shared;

            void Frame()
            {
                using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                {
                    Surface = surface,
                    Dpi = new Vector(96, 96),
                });

                foreach (var run in runs)
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }
            }

            try
            {
                DropEarlierFrames();

                var before = budget.UsedBytes;

                Frame();
                Frame();

                var held = budget.UsedBytes;

                for (var i = 0; i < 20; i++)
                {
                    Frame();
                }

                Assert.Equal(held, budget.UsedBytes);
                Assert.True(held - before < budget.LimitBytes / 16,
                    $"a static paragraph holds {held - before} bytes");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Frame_Whose_Pinned_Content_Exceeds_The_Limit_Draws_It_Without_Thrashing(GpuBackend backend)
        {
            using var limit = LimitScope.Set(GlyphCacheBudget.DefaultLimitBytes);
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var runs = CreateGlyphSweep(typeface, 20);
            var budget = GlyphCacheBudget.Shared;

            void Frame()
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);

                foreach (var run in runs)
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }

                context.FlushGlyphBatch();
            }

            try
            {
                DropEarlierFrames();

                var before = budget.UsedBytes;

                Frame();
                Frame();

                // A limit below the frame's working set, within a quarter of it.
                var workingSet = budget.UsedBytes;

                budget.SetLimit(workingSet * 9 / 10);

                var misses = GlyphRasterDiagnostics.AtlasMissesOnThread;

                for (var i = 0; i < 5; i++)
                {
                    Frame();
                }

                Assert.Equal(0, GlyphRasterDiagnostics.AtlasMissesOnThread - misses);
                Assert.True(workingSet > before, "the frames held nothing");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        /// <summary>Trims everything earlier tests left that is not pinned.</summary>
        private static void DropEarlierFrames()
        {
            var budget = GlyphCacheBudget.Shared;
            var limit = budget.LimitBytes;

            budget.SetLimit(1);

            using (budget.BeginFrame())
            {
            }

            budget.SetLimit(limit);
        }

        private static ManagedGlyphRunImpl[] CreateParagraph(GlyphTypeface typeface, int count, double em)
        {
            var runs = new ManagedGlyphRunImpl[count];

            for (var i = 0; i < count; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], em,
                    new Point(4.3, em + i * Math.Round(em * 1.35)));
            }

            return runs;
        }

        /// <summary>Every glyph of <paramref name="typeface"/> once, in rows of 60.</summary>
        private static ManagedGlyphRunImpl[] CreateGlyphSweep(GlyphTypeface typeface, double em)
        {
            var runs = new List<ManagedGlyphRunImpl>();
            var glyphCount = Math.Min((int)typeface.GlyphCount, 2400);

            for (var start = 1; start < glyphCount; start += 60)
            {
                var infos = new List<GlyphInfo>();

                for (var glyph = start; glyph < Math.Min(start + 60, glyphCount); glyph++)
                {
                    infos.Add(new GlyphInfo((ushort)glyph, glyph - start, em * 0.7));
                }

                var row = runs.Count;

                runs.Add(new ManagedGlyphRunImpl(typeface, em, infos,
                    new Point(2 + row % 2 * 0.5, em + row % 25 * (em + 4))));
            }

            return runs.ToArray();
        }

        private static void DisposeAll(IEnumerable<ManagedGlyphRunImpl> runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }

        /// <summary>Sets the process-wide limit for a test and puts the previous one back.</summary>
        private sealed class LimitScope : IDisposable
        {
            private readonly long _previous;

            private LimitScope(long previous) => _previous = previous;

            public static LimitScope Set(long limit)
            {
                var budget = GlyphCacheBudget.Shared;
                var scope = new LimitScope(budget.LimitBytes);

                budget.SetLimit(limit);

                return scope;
            }

            public void Dispose() => GlyphCacheBudget.Shared.SetLimit(_previous);
        }
    }
}
