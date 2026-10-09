using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A hardware GPU context places subpixel run masks in a shared atlas and draws the runs of
    /// a frame with one call per page and colour through the per-channel blender. The frame must
    /// hold exactly the pixels of drawing every run from its own image, in order, whatever the
    /// runs are interleaved with.
    /// </summary>
    public class LcdAtlasBatchTests
    {
        private const int Width = 520;
        private const int Height = 360;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
            "Pack my box with five dozen liquor jugs; sphinx of black",
            "quartz, judge my vow! Numbers such as 1234567890 appear.",
        };

        public static IEnumerable<object[]> Backends()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
        }

        [Theory]
        [MemberData(nameof(Backends))]
        public void A_Warm_Lcd_Paragraph_Of_One_Colour_Is_One_Draw(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 30, 13, new Point(6.3, 4));

            try
            {
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), Mode.Batched, out _);
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), Mode.Batched, out var draws);

                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(Backends))]
        public void A_Batched_Lcd_Frame_Draws_The_Pixels_Of_Each_Run_Drawn_From_Its_Own_Image(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var own = new Scene(typeface);
            using var single = new Scene(typeface);
            using var batched = new Scene(typeface);

            var expected = Render(gpu, own.Draw, Mode.OwnImages, out _);

            TransformedAtlasTests.AssertEqual(expected, Render(gpu, single.Draw, Mode.AtlasOneByOne, out _),
                "atlas entries drawn one by one");
            TransformedAtlasTests.AssertEqual(expected, Render(gpu, batched.Draw, Mode.Batched, out var cold),
                "cold batched frame");
            TransformedAtlasTests.AssertEqual(expected, Render(gpu, batched.Draw, Mode.Batched, out var warm),
                "warm batched frame");
            TransformedAtlasTests.AssertEqual(expected, Render(gpu, own.Draw, Mode.OwnImages, out var unbatched),
                "warm frame from own images");

            Assert.True(warm < Scene.LcdRunCount,
                $"the warm batched frame issued {warm} atlas draws for {Scene.LcdRunCount} subpixel runs");
            Assert.True(cold == warm, $"the cold frame issued {cold} atlas draws, the warm one {warm}");
            Assert.True(unbatched < warm, "drawing from own images issued as many atlas draws as batching");
        }

        [Theory]
        [MemberData(nameof(Backends))]
        public void Runs_Whose_Pages_The_Atlas_Drops_Compose_Again_And_Draw_The_Same_Pixels(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var reference = new Scene(typeface);
            using var own = new Scene(typeface);
            using var evicting = new Scene(typeface);
            var referenceParagraph = CreateParagraph(typeface, 20, 15, new Point(250.4, 3));
            var ownParagraph = CreateParagraph(typeface, 20, 15, new Point(250.4, 3));
            var evictingParagraph = CreateParagraph(typeface, 20, 15, new Point(250.4, 3));

            try
            {
                // The smallest budget holds one page, and a page of 64 rows holds a fraction of
                // a frame's masks. Frames alternate between two copies of the scene, so each
                // frame drops the pages the other copy's frame placed. The atlas counts frames of
                // a budget of its own, which only this test advances.
                var budget = new GlyphCacheBudget(GlyphCacheBudget.DefaultLimitBytes);
                var atlas = new LcdRunAtlas(0, 64, budget);
                var scenes = new[] { (own, ownParagraph), (evicting, evictingParagraph) };
                var expected = Render(gpu, context =>
                {
                    reference.Draw(context);
                    DrawAll(context, referenceParagraph, Brushes.Black);
                }, Mode.OwnImages, out _);

                for (var frame = 0; frame < 4; frame++)
                {
                    var (scene, paragraph) = scenes[frame % 2];
                    byte[] actual;

                    using (budget.BeginFrame())
                    {
                        actual = Render(gpu, context =>
                        {
                            scene.Draw(context);
                            DrawAll(context, paragraph, Brushes.Black);
                        }, Mode.Batched, out _, atlas);
                    }

                    TransformedAtlasTests.AssertEqual(expected, actual, $"frame {frame}");
                }

                Assert.True(atlas.Evictions > 0, "the atlas dropped no page");
            }
            finally
            {
                DisposeAll(referenceParagraph);
                DisposeAll(ownParagraph);
                DisposeAll(evictingParagraph);
            }
        }

        [Theory]
        [MemberData(nameof(Backends))]
        public void Disposing_Every_Run_On_A_Page_Releases_The_Page(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var atlas = new LcdRunAtlas(16L * 1024 * 1024);
            var runs = CreateParagraph(typeface, 12, 13, new Point(6.3, 4));

            try
            {
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), Mode.Batched, out _, atlas);

                Assert.NotEmpty(atlas.GetPages());
            }
            finally
            {
                DisposeAll(runs);
            }

            Assert.Empty(atlas.GetPages());
            Assert.Equal(0, atlas.AllocatedBytes);
        }

        [Theory]
        [MemberData(nameof(Backends))]
        public void An_Lcd_Batch_Is_Drawn_Before_It_Outgrows_One_Atlas_Draw(GpuBackend backend)
        {
            // The grayscale batch's bound: Skia's GPU atlas op sizes its vertex data in a 32-bit int
            // at 64 bytes per sprite, so one draw of 2^25 sprites overflows it.
            const int maxSpritesPerAtlasDraw = 16384;
            const int columns = 26;

            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // One small glyph per run, spaced so no two masks overlap: nothing but the batch's size
            // makes it draw before the session ends.
            var runs = new ManagedGlyphRunImpl[maxSpritesPerAtlasDraw * 3 / 2];

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, "il.:|!"[i % 6].ToString(), 9,
                    new Point(4.25 + i % columns * 20, 12 + i / columns * 20));
            }

            try
            {
                var atlas = new LcdRunAtlas(256L * 1024 * 1024);
                var expected = Render(gpu, context => DrawAll(context, runs, Brushes.Black), Mode.AtlasOneByOne,
                    out _, atlas);
                var drawnWhileAppending = 0;

                var actual = Render(gpu, context =>
                {
                    var before = DrawingContextImpl.AtlasDrawsOnThread;

                    DrawAll(context, runs, Brushes.Black);
                    drawnWhileAppending = DrawingContextImpl.AtlasDrawsOnThread - before;
                }, Mode.Batched, out var draws, atlas);

                // Entries on another page would draw the batch before it grows this far.
                Assert.Single(atlas.GetPages());
                Assert.True(drawnWhileAppending >= runs.Length / maxSpritesPerAtlasDraw,
                    $"{runs.Length} subpixel runs took {drawnWhileAppending} atlas draws before the session ended");
                Assert.True(draws >= (runs.Length + maxSpritesPerAtlasDraw - 1) / maxSpritesPerAtlasDraw,
                    $"{runs.Length} subpixel runs took {draws} atlas draws");
                TransformedAtlasTests.AssertEqual(expected, actual, "batched subpixel runs");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        private enum Mode
        {
            OwnImages,
            AtlasOneByOne,
            Batched,
        }

        /// <summary>
        /// Subpixel paragraphs in several colours, opaque and translucent, at fractional origins;
        /// runs whose masks overlap; and runs that draw grayscale (rotated, inside a layer),
        /// interleaved with fills, a clip and an opacity.
        /// </summary>
        private sealed class Scene : IDisposable
        {
            /// <summary>The runs drawn with subpixel masks.</summary>
            public const int LcdRunCount = 15;

            private readonly List<ManagedGlyphRunImpl> _runs = new();
            private readonly ManagedGlyphRunImpl[] _paragraph;
            private readonly ManagedGlyphRunImpl[] _alternating;
            private readonly ManagedGlyphRunImpl _overlapping;
            private readonly ManagedGlyphRunImpl _overlapped;
            private readonly ManagedGlyphRunImpl _clipped;
            private readonly ManagedGlyphRunImpl _rotated;
            private readonly ManagedGlyphRunImpl _faded;
            private readonly ManagedGlyphRunImpl _layered;
            private readonly ManagedGlyphRunImpl _translucent;

            public Scene(GlyphTypeface typeface)
            {
                _paragraph = Add(CreateParagraph(typeface, 6, 13, new Point(6.3, 2)));
                _alternating = Add(CreateParagraph(typeface, 4, 12, new Point(40.71, 112.4)));

                // Masks of neighbouring lines closer than their ink overlap.
                _overlapping = Add(WideRunMaskTests.CreateRun(typeface, s_lines[1], 16, new Point(10.25, 196.5)));
                _overlapped = Add(WideRunMaskTests.CreateRun(typeface, s_lines[2], 16, new Point(14.75, 204.5)));
                _clipped = Add(WideRunMaskTests.CreateRun(typeface, s_lines[2], 16, new Point(120.5, 236.3)));
                _rotated = Add(WideRunMaskTests.CreateRun(typeface, s_lines[3], 13, new Point(0, 0)));
                _faded = Add(WideRunMaskTests.CreateRun(typeface, s_lines[0], 14, new Point(10.2, 262.6)));
                _layered = Add(WideRunMaskTests.CreateRun(typeface, s_lines[1], 14, new Point(10.6, 287.1)));
                _translucent = Add(WideRunMaskTests.CreateRun(typeface, s_lines[4], 12, new Point(160.3, 330.4)));
            }

            private ManagedGlyphRunImpl Add(ManagedGlyphRunImpl run)
            {
                _runs.Add(run);
                return run;
            }

            private ManagedGlyphRunImpl[] Add(ManagedGlyphRunImpl[] runs)
            {
                _runs.AddRange(runs);
                return runs;
            }

            public void Draw(DrawingContextImpl context)
            {
                var red = new ImmutableSolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x10));
                var blue = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90));

                context.Clear(Color.FromRgb(0xF4, 0xF0, 0xE6));

                DrawAll(context, _paragraph, Brushes.Black);

                // A translucent fill over the paragraph's last lines lands above them.
                context.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x80, 0)), null,
                    new RoundedRect(new Rect(30, 50, 200, 40)));

                for (var i = 0; i < _alternating.Length; i++)
                {
                    context.DrawGlyphRun(i % 2 == 0 ? red : blue, _alternating[i]);
                }

                context.DrawGlyphRun(Brushes.Black, _overlapping);
                context.DrawGlyphRun(Brushes.Black, _overlapped);

                context.PushClip(new Rect(150, 220, 160, 22));
                context.DrawGlyphRun(Brushes.Black, _clipped);
                context.PopClip();

                var transform = context.Transform;

                context.Transform = Matrix.CreateRotation(Math.PI * 12 / 180) * Matrix.CreateTranslation(330.3, 140.7);
                context.DrawGlyphRun(Brushes.Black, _rotated);
                context.Transform = transform;

                context.PushOpacity(0.5, null);
                context.DrawGlyphRun(Brushes.Black, _faded);
                context.PopOpacity();

                context.PushLayer(new Rect(0, 270, 300, 30));
                context.DrawGlyphRun(blue, _layered);
                context.PopLayer();

                context.DrawGlyphRun(new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x10, 0x10)), _translucent);

                context.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0, 0x80, 0x40)), null,
                    new Rect(250, 300, 120, 40));
            }

            public void Dispose() => DisposeAll(_runs.ToArray());
        }

        private static ManagedGlyphRunImpl[] CreateParagraph(GlyphTypeface typeface, int count, double em, Point origin)
        {
            var runs = new ManagedGlyphRunImpl[count];

            for (var i = 0; i < count; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], em,
                    new Point(origin.X, origin.Y + em + i * Math.Round(em * 1.35)));
            }

            return runs;
        }

        private static void DrawAll(DrawingContextImpl context, ManagedGlyphRunImpl[] runs, IBrush brush)
        {
            foreach (var run in runs)
            {
                context.DrawGlyphRun(brush, run);
            }
        }

        private static void DisposeAll(ManagedGlyphRunImpl[] runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }

        /// <summary>
        /// Renders a frame in one drawing session on an untouched display-bound GPU surface with
        /// horizontal RGB stripes and reads it back, reporting the atlas draws the session issued.
        /// </summary>
        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw, Mode mode, out int atlasDraws,
            LcdRunAtlas? atlas = null)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info, 0, GRSurfaceOrigin.TopLeft,
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal), false);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var before = DrawingContextImpl.AtlasDrawsOnThread;

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = true,
                   }))
            {
                context.UsesLcdRunAtlas = mode != Mode.OwnImages;
                context.BatchesGlyphAtlasDraws = mode == Mode.Batched;

                if (atlas is not null)
                {
                    context.LcdAtlas = atlas;
                }

                surface!.Canvas.Clear(SKColors.Transparent);
                draw(context);
            }

            atlasDraws = DrawingContextImpl.AtlasDrawsOnThread - before;

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
