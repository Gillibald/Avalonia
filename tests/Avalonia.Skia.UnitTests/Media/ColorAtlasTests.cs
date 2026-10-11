using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Colour glyph content of managed runs on hardware GPU contexts drawn from colour atlas pages:
    /// each glyph's share of the run mask is a premultiplied RGBA entry, the runs of a frame draw
    /// from their pages in as few calls as A8 text, and the pixels are the run masks' wherever the
    /// glyphs of a run share no ink.
    /// </summary>
    public class ColorAtlasTests
    {
        private const int Width = 560;
        private const int Height = 300;
        private const double EmSize = 32;
        private const double Advance = 40;
        private const int RunCount = 12;

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x10));

        public enum Content
        {
            V1Radial,
            V1Sentinel,
            V0,
            Strike,
            SegoeUiEmoji,
        }

        public static IEnumerable<object[]> Contexts()
        {
            yield return new object[] { GpuBackend.NativeGl, true };
            yield return new object[] { GpuBackend.Angle, true };
            yield return new object[] { GpuBackend.Vulkan, true };
        }

        public static IEnumerable<object[]> ContextsAndContent()
        {
            foreach (var context in Contexts())
            {
                foreach (Content content in Enum.GetValues(typeof(Content)))
                {
                    yield return new[] { context[0], context[1], content };
                }
            }
        }

        [Theory]
        [MemberData(nameof(ContextsAndContent))]
        public void Upright_Colour_Runs_Draw_From_Colour_Atlas_Pages_In_One_Draw_Per_Frame(GpuBackend backend,
            bool updatable, Content content)
        {
            using var gpu = CreateGpu(backend, updatable);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(content, out var glyphs);
            var runs = CreateRuns(typeface, glyphs);

            try
            {
                var first = Measure(() => Render(gpu, runs), out var firstPixels);

                Assert.Equal(1, first.AtlasDraws);
                Assert.Single(ColorAtlasOf(gpu, typeface).GetPages());

                var second = Measure(() => Render(gpu, runs), out var secondPixels);

                Assert.Equal(new Counts(1, 0, 0, 0), second);
                TransformedAtlasTests.AssertEqual(firstPixels, secondPixels, "second frame");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        /// <summary>
        /// Every entry holds the bytes the run mask holds for that glyph, and both draws sample one
        /// texel per pixel at whole-pixel offsets, so on every context the atlas draws exactly what
        /// the run masks draw.
        /// </summary>
        [Theory]
        [MemberData(nameof(ContextsAndContent))]
        public void The_Colour_Atlas_Draws_The_Bytes_Of_The_Run_Masks(GpuBackend backend, bool updatable,
            Content content)
        {
            using var gpu = CreateGpu(backend, updatable);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(content, out var glyphs);
            var runs = CreateRuns(typeface, glyphs);

            try
            {
                byte[] runMasks;

                using (SwitchColorAtlasOff())
                {
                    runMasks = Render(gpu, runs);
                }

                var atlas = Measure(() => Render(gpu, runs), out var atlasPixels);

                Assert.True(atlas.AtlasDraws > 0, "the runs did not draw from the colour atlas");
                TransformedAtlasTests.AssertEqual(runMasks, atlasPixels, $"{content} on {backend}");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        /// <summary>
        /// Colour runs and A8 text batch on their pages and stay pending across each other, so the
        /// order of overlapping runs and of the opacity around them must come out as drawing every
        /// run on its own does.
        /// </summary>
        [Theory]
        [MemberData(nameof(Contexts))]
        public void Colour_And_Text_Runs_Interleaved_And_Overlapping_Draw_The_Pixels_Of_Each_Run_Drawn_Alone(
            GpuBackend backend, bool updatable)
        {
            using var gpu = CreateGpu(backend, updatable);
            using var scope = CreateEnvironment();
            var colour = CreateTypeface(Content.SegoeUiEmoji, out var emoji);
            var inter = GlyphAtlasBatchTests.LoadAsset("Inter-Regular.ttf");
            var map = inter.CharacterToGlyphMap;
            var words = "Colour".Select(c => map[c]).ToArray();
            var runs = new List<(GlyphRun Run, IBrush Brush, double Opacity)>();

            for (var line = 0; line < 4; line++)
            {
                var y = 60 + line * 56;

                // Text, an emoji run over its right half, text over the emoji, a faded emoji run.
                runs.Add((CreateGlyphRun(inter, words, new Point(10, y), 18), Brushes.Black, 1));
                runs.Add((CreateGlyphRun(colour, emoji, new Point(40, y + 6)), s_text, 1));
                runs.Add((CreateGlyphRun(inter, words, new Point(70, y + 4), 18), Brushes.Navy, 1));
                runs.Add((CreateGlyphRun(colour, emoji, new Point(260, y), 26), s_text, 0.5));
                runs.Add((CreateGlyphRun(inter, words, new Point(300, y - 8), 22), Brushes.DarkGreen, 0.8));
            }

            try
            {
                byte[] Draw(bool batched) => RenderOn(gpu, (impl, context) =>
                {
                    impl.BatchesGlyphAtlasDraws = batched;

                    foreach (var (run, brush, opacity) in runs)
                    {
                        using (context.PushOpacity(opacity))
                        {
                            context.DrawGlyphRun(brush, run);
                        }
                    }
                });

                var alone = Draw(batched: false);
                var batched = Draw(batched: true);

                Assert.NotEmpty(ColorAtlasOf(gpu, colour).GetPages());
                TransformedAtlasTests.AssertEqual(alone, batched, $"batched frame on {backend}");
            }
            finally
            {
                foreach (var (run, _, _) in runs)
                {
                    run.Dispose();
                }
            }
        }

        /// <summary>
        /// A run mask composes overlapping glyphs in 8 bits before blending them onto the target,
        /// where sprites would blend one after the other, so a run whose glyphs share ink keeps its
        /// run mask while the other runs of the frame draw from the atlas.
        /// </summary>
        [Theory]
        [MemberData(nameof(Contexts))]
        public void A_Run_Whose_Glyphs_Share_Ink_Keeps_Its_Run_Mask(GpuBackend backend, bool updatable)
        {
            using var gpu = CreateGpu(backend, updatable);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Content.SegoeUiEmoji, out var glyphs);
            var runs = new[]
            {
                CreateGlyphRun(typeface, glyphs, new Point(12, 60)),
                CreateGlyphRun(typeface, glyphs, new Point(12, 140), advance: 14),
                CreateGlyphRun(typeface, glyphs, new Point(12, 220)),
            };

            try
            {
                byte[] runMasks;

                using (SwitchColorAtlasOff())
                {
                    runMasks = Render(gpu, runs);
                }

                var counts = Measure(() => Render(gpu, runs), out var pixels);

                Assert.Equal(1, counts.AtlasDraws);
                TransformedAtlasTests.AssertEqual(runMasks, pixels, "frame with an overlapping run");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Colour_Pages_Are_Charged_To_The_Budget_And_Eviction_Releases_Them(GpuBackend backend,
            bool updatable)
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
            var colorAtlas = new GlyphMaskAtlas(budget, format: GlyphAtlasFormat.Rgba);

            using var gpu = CreateGpu(backend, updatable, colorAtlas);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Content.SegoeUiEmoji, out var glyphs);

            typeface.CacheBudget = budget;

            var atlas = colorAtlas;
            var runs = CreateRuns(typeface, glyphs);

            try
            {
                var before = Render(gpu, runs);
                var page = Assert.Single(atlas.GetPages());

                Assert.Equal((long)page.RowBytes * page.Height, atlas.AllocatedBytes);
                Assert.True(budget.BytesOf(GlyphCachePoolKind.Atlas) >= atlas.AllocatedBytes);

                var charged = budget.BytesOf(GlyphCachePoolKind.Atlas);

                Assert.Equal(atlas.AllocatedBytes, ((IGlyphCachePool)atlas).EvictOldest(long.MaxValue, long.MaxValue));
                Assert.True(page.IsEvicted);
                Assert.Equal(charged - page.Pixels.Length, budget.BytesOf(GlyphCachePoolKind.Atlas));

                // The runs place their glyphs again and draw the same pixels.
                var again = Measure(() => Render(gpu, runs), out var after);

                Assert.True(again.Placements > 0, "nothing was placed again after the eviction");
                Assert.Single(atlas.GetPages());
                TransformedAtlasTests.AssertEqual(before, after, "frame after the eviction");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Disposing_The_Typeface_Releases_Its_Colour_Atlas_Entries(GpuBackend backend, bool updatable)
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            // The shared colour atlas, as the backends register it: disposal retires the
            // typeface's entries there.
            using var gpu = CreateGpu(backend, updatable, GlyphMaskAtlas.SharedColor);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(Content.SegoeUiEmoji, out var glyphs);

            typeface.CacheBudget = budget;

            var owner = typeface.MaskOwnerId;
            var runs = CreateRuns(typeface, glyphs);

            try
            {
                Render(gpu, runs);

                Assert.Contains(GlyphMaskAtlas.SharedColor.GetPages(), p => p.Keys.Any(k => k.Owner == owner));
            }
            finally
            {
                DisposeAll(runs);
            }

            typeface.Dispose();

            Assert.DoesNotContain(GlyphMaskAtlas.SharedColor.GetPages(), p => p.Keys.Any(k => k.Owner == owner));
            Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
        }

        public static IEnumerable<object[]> ContextsWithoutUpdatableTextures()
        {
            foreach (var backend in new[] { GpuBackend.NativeGl, GpuBackend.Angle, GpuBackend.Vulkan })
            {
                foreach (var content in new[] { Content.V1Radial, Content.V0, Content.SegoeUiEmoji })
                {
                    yield return new object[] { backend, content };
                }
            }
        }

        /// <summary>
        /// A context that cannot update part of a page in place would upload a whole colour page,
        /// up to 8 MB, whenever a glyph is placed, so it keeps drawing colour runs from run masks
        /// and makes no colour atlas pages.
        /// </summary>
        [Theory]
        [MemberData(nameof(ContextsWithoutUpdatableTextures))]
        public void A_Context_Without_Updatable_Textures_Draws_Colour_Runs_From_Run_Masks(GpuBackend backend,
            Content content)
        {
            var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);

            using var gpu = CreateGpu(backend, false);
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(content, out var glyphs);

            typeface.CacheBudget = budget;

            var runs = CreateRuns(typeface, glyphs);

            try
            {
                byte[] runMasks;

                using (SwitchColorAtlasOff())
                {
                    runMasks = Render(gpu, runs);
                }

                var counts = Measure(() => Render(gpu, runs), out var pixels);

                Assert.Equal(0, counts.AtlasDraws);
                Assert.Equal(0, budget.BytesOf(GlyphCachePoolKind.Atlas));
                TransformedAtlasTests.AssertEqual(runMasks, pixels, $"{content} on {backend}");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        internal static IDisposable SwitchColorAtlasOff() => new ColorAtlasSwitch();

        private sealed class ColorAtlasSwitch : IDisposable
        {
            private readonly bool _previous = ColorGlyphRunSplitter.UseColorAtlas;

            public ColorAtlasSwitch() => ColorGlyphRunSplitter.UseColorAtlas = false;

            public void Dispose() => ColorGlyphRunSplitter.UseColorAtlas = _previous;
        }

        private readonly record struct Counts(long AtlasDraws, long Placements, long Rasterizations, long VectorDraws);

        private static Counts Measure(Func<byte[]> render, out byte[] pixels)
        {
            var draws = DrawingContextImpl.AtlasDrawsOnThread;
            var placements = GlyphRasterDiagnostics.AtlasPlacementsOnThread;
            var rasterizations = GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread;
            var vectorDraws = GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread;

            pixels = render();

            return new Counts(DrawingContextImpl.AtlasDrawsOnThread - draws,
                GlyphRasterDiagnostics.AtlasPlacementsOnThread - placements,
                GlyphRasterDiagnostics.ColorMaskRasterizationsOnThread - rasterizations,
                GlyphRasterDiagnostics.ColorGlyphVectorDrawsOnThread - vectorDraws);
        }

        internal static GpuTestContext CreateGpu(GpuBackend backend, bool updatable, GlyphMaskAtlas? colorAtlas = null)
        {
            if (updatable)
            {
                var registered = TransformedAtlasTests.CreateGpu(backend, false, colorAtlas: colorAtlas);

                Assert.SkipWhen(SkiaUpdatableTextures.Get(registered.GrContext) is null,
                    $"{backend} has no updatable textures");

                return registered;
            }

            var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            return gpu!;
        }

        internal static GlyphMaskAtlas ColorAtlasOf(GpuTestContext gpu, GlyphTypeface typeface)
            => SkiaUpdatableTextures.Get(gpu.GrContext)!.ColorAtlas;

        internal static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            return scope;
        }

        /// <summary>
        /// A typeface with colour content of the given kind and a run of it: colour glyphs between
        /// outline glyphs of the same typeface, or four different emoji.
        /// </summary>
        internal static GlyphTypeface CreateTypeface(Content content, out ushort[] glyphs)
        {
            switch (content)
            {
                case Content.V1Radial:
                    return ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.V1Radial, out glyphs);
                case Content.SegoeUiEmoji:
                    return ColorGlyphMaskTests.CreateTypeface(ColorGlyphMaskTests.Font.SegoeUiEmoji, out glyphs);
                case Content.V1Sentinel:
                {
                    var typeface = ColorGlyphMaskTests.CreateV1Typeface(ColorGlyphMaskTests.BuildColrV1Solid,
                        out var glyph, paletteEntry: 0xFFFF);

                    glyphs = new[] { glyph, typeface.CharacterToGlyphMap['A'], glyph };

                    return typeface;
                }
                case Content.V0:
                {
                    var typeface = ColorGlyphV1SplitTests.CreateV0Typeface(out var glyph);

                    glyphs = new[] { glyph, typeface.CharacterToGlyphMap['A'], glyph };

                    return typeface;
                }
                default:
                {
                    var typeface = BitmapGlyphRenderingTests.CreateBitmapTypeface(out var bitmapGlyph, out var plain);

                    glyphs = new[] { plain, bitmapGlyph, plain, bitmapGlyph };

                    return typeface;
                }
            }
        }

        internal static GlyphRun CreateGlyphRun(GlyphTypeface typeface, ushort[] glyphs, Point origin,
            double emSize = EmSize, double advance = Advance)
        {
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < glyphs.Length; i++)
            {
                infos.Add(new GlyphInfo(glyphs[i], i, emSize == EmSize ? advance : emSize * 0.7));
            }

            var run = new GlyphRun(typeface, emSize, default, infos, origin);

            Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);

            return run;
        }

        /// <summary>Runs laid out in three columns at whole-pixel and quarter-pixel pens.</summary>
        private static GlyphRun[] CreateRuns(GlyphTypeface typeface, ushort[] glyphs)
        {
            var runs = new GlyphRun[RunCount];

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = CreateGlyphRun(typeface, glyphs,
                    new Point(12 + i % 3 * 182 + (i % 4) * 0.25, 50 + i / 3 * 64));
            }

            return runs;
        }

        private static void DisposeAll(IEnumerable<GlyphRun> runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }

        private static byte[] Render(GpuTestContext gpu, GlyphRun[] runs)
            => RenderOn(gpu, (_, context) =>
            {
                foreach (var run in runs)
                {
                    context.DrawGlyphRun(s_text, run);
                }
            });

        /// <summary>
        /// Renders on a GPU surface over a background of coloured bands, so every blend of the
        /// glyphs shows in the pixels, and reads it back as RGBA.
        /// </summary>
        internal static byte[] RenderOn(GpuTestContext gpu, Action<DrawingContextImpl, DrawingContext> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            surface!.Canvas.Clear(new SKColor(0xF4, 0xEE, 0xE0));

            using (var band = new SKPaint { Color = new SKColor(0x30, 0x60, 0xA0, 0xC0) })
            {
                for (var x = 0; x < Width; x += 46)
                {
                    surface.Canvas.DrawRect(x, 0, 17, Height, band);
                }
            }

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                draw(impl, context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }
            }

            return pixels;
        }
    }
}
