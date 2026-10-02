using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Static transformed text keeps no run-sized bitmap on any context: what it retains is the
    /// shared glyph storage (the glyph mask cache on raster contexts, the atlas on GPU ones)
    /// plus each run's sprite arrays. Retained memory is measured process-wide, so these
    /// tests run alone.
    /// </summary>
    [Collection(nameof(TransformedMemoryTests))]
    [CollectionDefinition(nameof(TransformedMemoryTests), DisableParallelization = true)]
    public class TransformedMemoryTests
    {
        private static readonly string[] s_lines =
        {
            "Static rotated paragraphs are the common case",
            "for transformed text: labels on a tilted card,",
            "a chart axis title, a watermark across a page.",
            "Each line is one glyph run, and none of them",
            "should cost a bitmap the size of its bounding box.",
            "The glyph masks are shared across the lines;",
            "only the placements belong to each run.",
            "0123456789 AVATAR fjord Hamburgefonstiv",
        };

        private static readonly Matrix s_rotation = Matrix.CreateRotation(Math.PI * 15 / 180) *
            Matrix.CreateTranslation(60.3, 20.6);

        public enum Target
        {
            Raster,
            PlainContext,
            HardwareGl,
            SoftwareGl,
        }

        public static IEnumerable<object[]> Targets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target };
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Static_Transformed_Text_Draws_No_Run_Sized_Bitmap(Target target)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var runs = CreateParagraph(typeface);
            var largestGlyph = 0;

            try
            {
                if (target == Target.PlainContext)
                {
                    // A context without the transformed services draws through its bitmap blit;
                    // every bitmap it is handed must be a glyph, not a run.
                    var info = new SKImageInfo(900, 700, SKColorType.Bgra8888, SKAlphaType.Premul);

                    using var surface = SKSurface.Create(info);
                    using var inner = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                    {
                        Surface = surface,
                        Dpi = new Vector(96, 96),
                    });

                    var plain = new TransformedGlyphRunTests.DeviceMaskContext(900, 700, int.MaxValue, inner);

                    foreach (var run in runs)
                    {
                        plain.Transform = s_rotation;
                        Assert.True(MaskGlyphRunRenderer.TryDrawTransformed(plain, run, Brushes.Black,
                            TextRenderingMode.Antialias));

                        largestGlyph = Math.Max(largestGlyph, LargestGlyph(typeface, run));
                    }

                    Assert.NotEmpty(plain.Destinations);

                    foreach (var destination in plain.Destinations)
                    {
                        Assert.True(destination.Width * destination.Height <= largestGlyph,
                            $"a {destination.Width} x {destination.Height} bitmap is larger than any glyph ({largestGlyph} px)");
                    }

                    return;
                }

                using var output = Output.Create(target);

                foreach (var run in runs)
                {
                    output.Context.Transform = s_rotation;
                    output.Context.DrawGlyphRun(Brushes.Black, run);

                    Assert.True(run.TransformedSprites.TryGet(TransformedAtlasTests.SpriteKey(run, s_rotation),
                        out var sprites));

                    // Raster: the glyph masks are blitted, no bitmap per sprite either. GPU:
                    // every batch samples an atlas page, none has an image of its own.
                    Assert.Null(sprites.FallbackImages);

                    foreach (var batch in sprites.Batches ?? Array.Empty<GlyphAtlasBatch>())
                    {
                        Assert.NotNull(batch.Page);
                    }
                }
            }
            finally
            {
                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }

        [Theory]
        [InlineData(Target.Raster)]
        [InlineData(Target.HardwareGl)]
        [InlineData(Target.SoftwareGl)]
        public void Static_Transformed_Text_Retains_Only_Glyph_Storage_And_Sprite_Arrays(Target target)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var output = Output.Create(target);
            using var warmup = WideRunMaskTests.CreateRun(typeface, "warm the pools up", 14, new Point(0, 20));

            // Pools, thread statics and the Skia paint cache settle on a run this test does not
            // measure.
            output.Context.Transform = s_rotation;
            output.Context.DrawGlyphRun(Brushes.Black, warmup);

            var runs = CreateParagraph(typeface);

            try
            {
                var cacheBefore = typeface.MaskCache.TotalCost;
                var atlasBefore = typeface.MaskAtlas.AllocatedBytes;
                var entriesBefore = typeface.MaskCache.Count + typeface.MaskAtlas.Count;
                var retainedBefore = LiveHeapBytes();

                foreach (var run in runs)
                {
                    output.Context.Transform = s_rotation;
                    output.Context.DrawGlyphRun(Brushes.Black, run);
                }

                output.Flush();

                var retained = LiveHeapBytes() - retainedBefore;
                long sprites = 0;
                long bounds = 0;

                foreach (var run in runs)
                {
                    sprites += run.TransformedSprites.ByteCost;

                    var box = run.Bounds.TransformToAABB(s_rotation);

                    bounds += (long)Math.Ceiling(box.Width) * (long)Math.Ceiling(box.Height);
                }

                var storage = typeface.MaskCache.TotalCost - cacheBefore +
                              typeface.MaskAtlas.AllocatedBytes - atlasBefore + sprites;

                // Beyond the payloads, the caches keep an entry object and a dictionary slot per
                // glyph mask, and each run keeps its sprite set and batch objects. None of it
                // grows with a run's pixel area, which a run mask would: the runs' bounding
                // boxes hold the number of pixels below, at one to four bytes each.
                var entries = typeface.MaskCache.Count + typeface.MaskAtlas.Count - entriesBefore;
                var bookkeeping = entries * 256L + runs.Length * 2048L;

                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{target}: retained {retained} B, glyph cache +{typeface.MaskCache.TotalCost - cacheBefore} B, " +
                    $"atlas +{typeface.MaskAtlas.AllocatedBytes - atlasBefore} B, sprites {sprites} B, " +
                    $"{entries} entries, run bounding boxes {bounds} px");

                Assert.True(retained <= storage + bookkeeping,
                    $"{retained} bytes retained for {storage} bytes of glyph storage and sprites " +
                    $"(bookkeeping allowance {bookkeeping}, run bounding boxes {bounds} px)");

                GC.KeepAlive(runs);
            }
            finally
            {
                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }

        /// <summary>
        /// The bytes of live objects on the managed heap: its size after a compacting full
        /// collection, less the free gaps the collection left. <see cref="GC.GetTotalMemory"/>
        /// counts more than the survivors on some hosts (on macOS ARM64 it reports about twice
        /// the heap's growth), which would charge the draws for memory they do not keep.
        /// </summary>
        private static long LiveHeapBytes()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);

            return info.HeapSizeBytes - info.FragmentedBytes;
        }

        private static ManagedGlyphRunImpl[] CreateParagraph(GlyphTypeface typeface)
        {
            var runs = new ManagedGlyphRunImpl[s_lines.Length];

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i], 14, new Point(8.37, 24.61 + i * 19));
            }

            return runs;
        }

        private static int LargestGlyph(GlyphTypeface typeface, ManagedGlyphRunImpl run)
        {
            var largest = 0;

            foreach (var (mask, _, _) in TransformedGlyphRunTests.GlyphMasksAtPens(typeface, run, s_rotation))
            {
                largest = Math.Max(largest, mask.Width * mask.Height);
            }

            return largest;
        }

        private sealed class Output : IDisposable
        {
            private readonly GpuTestContext? _gpu;
            private readonly SKSurface _surface;

            private Output(GpuTestContext? gpu, SKSurface surface)
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

            public static Output Create(Target target)
            {
                var info = new SKImageInfo(900, 700, SKColorType.Rgba8888, SKAlphaType.Premul);

                if (target == Target.Raster)
                {
                    return new Output(null, SKSurface.Create(info.WithColorType(SKColorType.Bgra8888)));
                }

                var gpu = TransformedAtlasTests.CreateGpu(GpuBackend.NativeGl, target == Target.SoftwareGl);
                var surface = SKSurface.Create(gpu.GrContext, true, info);

                if (surface is null)
                {
                    gpu.Dispose();
                    Assert.Skip("GPU surface creation failed.");
                }

                return new Output(gpu, surface);
            }

            public void Flush() => _gpu?.GrContext.Flush();

            public void Dispose()
            {
                Context.Dispose();
                _gpu?.GrContext.Flush();
                _surface.Dispose();
                _gpu?.Dispose();
            }
        }
    }
}
