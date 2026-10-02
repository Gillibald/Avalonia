using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A GPU context draws upright and static transformed text from the typeface's glyph atlas
    /// and merges the atlas draws of consecutive runs into one call per page and colour. The
    /// merged frame must hold exactly the pixels of drawing every run on its own, in order,
    /// whatever the runs are interleaved with.
    /// </summary>
    public class GlyphAtlasBatchTests
    {
        private const int Width = 520;
        private const int Height = 360;

        // The most sprites one atlas draw may hand to Skia. Skia's GPU atlas op sizes its vertex
        // data in a 32-bit int at 64 bytes per sprite, so one draw of 2^25 sprites overflows it and
        // writes out of bounds. A draw stays at the 65536 vertices one 16-bit indexed part holds.
        private const int MaxSpritesPerAtlasDraw = 16384;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
            "Pack my box with five dozen liquor jugs; sphinx of black",
            "quartz, judge my vow! Numbers such as 1234567890 appear.",
        };

        public static IEnumerable<object[]> Contexts()
        {
            yield return new object[] { GpuBackend.NativeGl, false };
            yield return new object[] { GpuBackend.Angle, false };
            yield return new object[] { GpuBackend.NativeGl, true };
            yield return new object[] { GpuBackend.Metal, false };
        }

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl, false };
            yield return new object[] { GpuBackend.Angle, false };
            yield return new object[] { GpuBackend.Metal, false };
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void A_Batched_Frame_Draws_The_Pixels_Of_Its_Runs_Drawn_One_By_One(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var scene = new Scene(typeface);

            var unbatched = Render(gpu, context => scene.Draw(context), batched: false, out var unbatchedDraws);
            var batched = Render(gpu, context => scene.Draw(context), batched: true, out var batchedDraws);
            var warm = Render(gpu, context => scene.Draw(context), batched: true, out _);

            TransformedAtlasTests.AssertEqual(unbatched, batched, "batched frame");
            TransformedAtlasTests.AssertEqual(unbatched, warm, "warm batched frame");

            Assert.True(batchedDraws < unbatchedDraws,
                $"the batched frame issued {batchedDraws} atlas draws, the unbatched one {unbatchedDraws}");
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Warm_Paragraph_Of_One_Colour_Is_One_Atlas_Draw(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 30, 13, new Point(6.3, 4));

            try
            {
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out _);
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out var draws);

                Assert.Equal(1, draws);

                foreach (var run in runs)
                {
                    Assert.Equal(0, run.RunMasks.Count);
                }
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Lines_Alternating_Typefaces_And_Colours_Draw_One_Atlas_Call_Per_Page_And_Colour(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var inter);

            var noto = LoadAsset("NotoSans-Italic.ttf");
            var brushes = new IBrush[] { Brushes.Black, new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90)) };

            // Thirty lines of a paragraph that never touch one another, the typeface changing
            // every line and the colour every other line: two pages, two colours.
            var runs = Enumerable.Range(0, 30)
                .Select(i => WideRunMaskTests.CreateRun(i % 2 == 0 ? inter : noto, s_lines[i % s_lines.Length], 9,
                    new Point(6.3, 12 + i * 11.6)))
                .ToArray();

            void Draw(DrawingContextImpl context)
            {
                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(brushes[i / 2 % 2], runs[i]);
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                Render(gpu, Draw, batched: true, out _);

                var batched = Render(gpu, Draw, batched: true, out var draws);

                TransformedAtlasTests.AssertEqual(expected, batched, "alternating lines");
                Assert.Equal(4, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Overlapping_Runs_Of_Other_Pages_And_Colours_Keep_Their_Order(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var inter);

            var noto = LoadAsset("NotoSans-Italic.ttf");
            var red = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xD0, 0x20, 0x10));

            // Each run covers the one before it: none of them may be drawn out of order.
            var first = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(10, 60));
            var second = WideRunMaskTests.CreateRun(noto, "Overlapping words", 30, new Point(14, 66));
            var third = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(18, 72));

            void Draw(DrawingContextImpl context)
            {
                context.DrawGlyphRun(Brushes.Black, first);
                context.DrawGlyphRun(red, second);
                context.DrawGlyphRun(Brushes.Black, third);
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                Render(gpu, Draw, batched: true, out _);

                TransformedAtlasTests.AssertEqual(expected, Render(gpu, Draw, batched: true, out var draws),
                    "overlapping runs");
                Assert.Equal(3, draws);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                third.Dispose();
            }
        }

        private static GlyphTypeface LoadAsset(string name)
        {
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory!.FullName, "Avalonia.RenderTests",
                "Assets", name));

            Assert.True(SfntFace.TryLoad(new System.IO.MemoryStream(bytes), out var face));

            return new GlyphTypeface(face);
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Warm_Unchanged_Frame_Submits_No_New_Atlas_Geometry(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 30, 13, new Point(6.3, 4));

            try
            {
                var expected = Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: false, out _);

                for (var frame = 0; frame < 3; frame++)
                {
                    Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out _);
                }

                var before = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                var warm = Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out var draws);

                Assert.Equal(1, draws);
                Assert.Equal(0, DrawingContextImpl.AtlasGeometrySubmittedOnThread - before);
                TransformedAtlasTests.AssertEqual(expected, warm, "warm frame");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Paragraph_Scrolled_By_Whole_Pixels_Reuses_Its_Atlas_Geometry(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 30, 13, new Point(6.3, 4));

            void Draw(DrawingContextImpl context, int offset)
            {
                context.Transform = Matrix.CreateTranslation(3, -offset);
                DrawAll(context, runs, Brushes.Black);
            }

            try
            {
                for (var frame = 0; frame < 3; frame++)
                {
                    Render(gpu, context => Draw(context, frame * 7), batched: true, out _);
                }

                var before = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                var scrolled = Render(gpu, context => Draw(context, 40), batched: true, out _);
                var submitted = DrawingContextImpl.AtlasGeometrySubmittedOnThread - before;
                var expected = Render(gpu, context => Draw(context, 40), batched: false, out _);

                Assert.Equal(0, submitted);
                TransformedAtlasTests.AssertEqual(expected, scrolled, "scrolled frame");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Warm_Frames_Draw_The_Pixels_Of_Their_Runs_Drawn_One_By_One_As_The_Batch_Changes(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 8, 13, new Point(6.3, 4));
            using var moving = WideRunMaskTests.CreateRun(typeface, s_lines[3], 13, new Point(20, 200));

            // New glyphs on the paragraph's page: a later version of the page, grown or not.
            using var extra = WideRunMaskTests.CreateRun(typeface, "QXZ#@%&*+=<>~|{}[]$", 21, new Point(10, 300));

            var frames = new (string Label, Action<DrawingContextImpl> Draw)[]
            {
                ("first", context => DrawAll(context, runs, Brushes.Black)),
                ("second", context => DrawAll(context, runs, Brushes.Black)),
                ("warm", context => DrawAll(context, runs, Brushes.Black)),
                ("run added", context =>
                {
                    DrawAll(context, runs, Brushes.Black);
                    context.DrawGlyphRun(Brushes.Black, moving);
                }),
                ("run added again", context =>
                {
                    DrawAll(context, runs, Brushes.Black);
                    context.DrawGlyphRun(Brushes.Black, moving);
                }),
                ("run moved", context =>
                {
                    DrawAll(context, runs, Brushes.Black);
                    context.Transform = Matrix.CreateTranslation(5, 9);
                    context.DrawGlyphRun(Brushes.Black, moving);
                }),
                ("run removed", context => DrawAll(context, runs.AsSpan(1).ToArray(), Brushes.Black)),
                ("run removed again", context => DrawAll(context, runs.AsSpan(1).ToArray(), Brushes.Black)),
                ("page written", context =>
                {
                    DrawAll(context, runs.AsSpan(1).ToArray(), Brushes.Black);
                    context.DrawGlyphRun(Brushes.Black, extra);
                }),
                ("recoloured", context => DrawAll(context, runs.AsSpan(1).ToArray(),
                    new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x10, 0x10)))),
                ("recoloured again", context => DrawAll(context, runs.AsSpan(1).ToArray(),
                    new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x10, 0x10)))),
                ("reordered", context => DrawAll(context, runs.AsEnumerable().Reverse().ToArray(), Brushes.Black)),
                ("reordered again", context => DrawAll(context, runs.AsEnumerable().Reverse().ToArray(), Brushes.Black)),
            };

            try
            {
                foreach (var (label, draw) in frames)
                {
                    var batched = Render(gpu, draw, batched: true, out _);
                    var unbatched = Render(gpu, draw, batched: false, out _);

                    TransformedAtlasTests.AssertEqual(unbatched, batched, label);
                }
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Cold_Paragraph_Uploads_Its_Atlas_Page_Once(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 30, 13, new Point(6.3, 4));

            try
            {
                var before = DrawingContextImpl.PageImagesCreatedOnThread;

                Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out _);

                var pages = typeface.MaskAtlas.GetPages().Length;

                Assert.Equal(1, pages);
                Assert.Equal(pages, DrawingContextImpl.PageImagesCreatedOnThread - before);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Upright_Atlas_Text_Equals_Its_Corrected_Glyph_Masks_Drawn_One_By_One(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Squeezed advances make neighbouring glyphs overlap.
            using var run = WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2),
                advanceScale: 0.8);

            var masks = UprightGlyphMasks(typeface, run);

            foreach (var color in s_tints)
            {
                var drawn = Render(gpu, context => context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run),
                    batched: true, out _);

                Assert.Equal(0, run.RunMasks.Count);
                Assert.True(run.TransformedSprites.Count > 0, "the run drew no sprites");

                // Each glyph mask through the colour's coverage table, modulated by the colour
                // and drawn over the glyphs before it: the raster compose's arithmetic.
                var table = MaskGamma.GetTable(color.R, color.G, color.B);
                var corrected = Render(gpu, context =>
                {
                    var canvas = context.Canvas;

                    using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A) };

                    foreach (var (mask, x, y) in masks)
                    {
                        if (mask.IsEmpty)
                        {
                            continue;
                        }

                        var alpha = new byte[mask.Width * mask.Height];

                        for (var i = 0; i < alpha.Length; i++)
                        {
                            alpha[i] = table[mask.Alpha[i]];
                        }

                        using var image = TransformedAtlasTests.CreateAlphaImage(alpha, mask.Width, mask.Height);

                        canvas.DrawImage(image, x + mask.Left, y + mask.Top, new SKSamplingOptions(), paint);
                    }
                }, batched: true, out _);

                if (color.A == 255)
                {
                    TransformedAtlasTests.AssertEqual(corrected, drawn, color.ToString());
                    continue;
                }

                // An image draw and an atlas draw modulate by a translucent paint in different
                // shaders, which round a level apart.
                for (var i = 0; i < drawn.Length; i++)
                {
                    Assert.True(Math.Abs(drawn[i] - corrected[i]) <= 1,
                        $"{color}: pixel ({i / 4 % Width}, {i / 4 / Width}) channel {i % 4} differs by " +
                        $"{Math.Abs(drawn[i] - corrected[i])}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Upright_Atlas_Text_Differs_From_Its_Run_Mask_Only_Where_Glyphs_Overlap(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR, fjord; 0123", 15, new Point(9.37, 30.2),
                advanceScale: 0.8);

            var masks = UprightGlyphMasks(typeface, run);

            // Per pixel: the coverage of every glyph that inks it, in draw order.
            var coverages = new List<byte>?[Width * Height];
            var summed = new byte[Width * Height];

            foreach (var (mask, x, y) in masks)
            {
                for (var row = 0; row < mask.Height; row++)
                {
                    for (var column = 0; column < mask.Width; column++)
                    {
                        var value = mask.Alpha[row * mask.Width + column];

                        if (value != 0)
                        {
                            (coverages[(y + mask.Top + row) * Width + x + mask.Left + column] ??= new List<byte>())
                                .Add(value);
                        }
                    }
                }

                RunMaskComposer.ComposeAlpha(mask, x, y, summed, Width, Height);
            }

            var overlapping = 0;

            foreach (var list in coverages)
            {
                if (list is { Count: > 1 })
                {
                    overlapping++;
                }
            }

            Assert.True(overlapping > 10, $"the run overlaps glyphs at only {overlapping} pixels");

            foreach (var color in s_tints)
            {
                if (color.A != 255)
                {
                    continue;
                }

                var table = MaskGamma.GetTable(color.R, color.G, color.B);
                var drawn = Render(gpu, context => context.DrawGlyphRun(new ImmutableSolidColorBrush(color), run),
                    batched: true, out _);

                // The run mask: coverage summed per pixel, corrected once by the alpha mask draw.
                var composed = Render(gpu, context =>
                {
                    using var image = TransformedAtlasTests.CreateAlphaImage(summed, Width, Height);

                    var bounds = new Rect(0, 0, Width, Height);

                    ((IAlphaGlyphMaskContext)context).DrawAlphaMask(image, bounds, bounds,
                        TransformedAtlasTests.ToArgb(color));
                }, batched: true, out _);

                var bound = TransformedAtlasTests.DeriveOverlapBound(table);

                for (var i = 0; i < Width * Height; i++)
                {
                    var difference = Math.Abs(drawn[i * 4 + 3] - composed[i * 4 + 3]);

                    if (coverages[i] is not { Count: > 1 } list)
                    {
                        Assert.True(difference == 0,
                            $"{color}: pixel ({i % Width}, {i / Width}) without overlap differs by {difference}");
                        continue;
                    }

                    // T[a] over T[b] against the run mask's T[a + b], plus a level of blend rounding.
                    var over = 0;
                    var sum = 0;

                    foreach (var coverage in list)
                    {
                        var corrected = table[coverage];

                        over = corrected + over - (corrected * over + 127) / 255;
                        sum += coverage;
                    }

                    var allowed = Math.Abs(over - table[Math.Min(255, sum)]) + 1;

                    Assert.True(difference <= allowed && difference <= bound,
                        $"{color}: overlap pixel ({i % Width}, {i / Width}) differs by {difference}, allowed {allowed}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Warm_Frames_Cycling_Through_Foreground_Colours_Allocate_Nothing(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 10, 13, new Point(6.3, 4));

            // Foregrounds in three luminance buckets, the atlas pages and batches of each.
            var brushes = new IBrush[]
            {
                Brushes.Black,
                new ImmutableSolidColorBrush(Colors.DarkGreen),
                new ImmutableSolidColorBrush(Colors.DarkOrange),
                new ImmutableSolidColorBrush(Colors.DarkBlue),
            };

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);

            void Frame(int frame)
            {
                DrawAll(context, runs, brushes[frame % brushes.Length]);
                context.FlushGlyphBatch();
            }

            try
            {
                for (var frame = 0; frame < brushes.Length * 2; frame++)
                {
                    Frame(frame);
                }

                var before = GC.GetAllocatedBytesForCurrentThread();

                for (var frame = 0; frame < brushes.Length * 10; frame++)
                {
                    Frame(frame);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                gpu.GrContext.Flush();

                Assert.True(allocated == 0, $"40 warm frames cycling colours allocated {allocated} bytes");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void An_Upright_Zoom_On_A_Hardware_Gpu_Adds_Nothing_To_The_Atlas_While_It_Animates(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 5, 13, new Point(6.3, 4));

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);
            using var context = TransformedAtlasTests.CreateContext(gpu, surface);

            try
            {
                var entries = 0;

                for (var frame = 0; frame < 30; frame++)
                {
                    context.Transform = Matrix.CreateScale(1 + frame * 0.04, 1 + frame * 0.04);
                    DrawAll(context, runs, Brushes.Black);
                    context.FlushGlyphBatch();

                    // Frames before the guard recognizes the gesture lay out sprites as static
                    // text does; the animating frames after it must not fill the atlas with a
                    // set of glyph masks per scale.
                    if (frame == TransformChurnGuard.Threshold - 1)
                    {
                        entries = typeface.MaskAtlas.Count;
                    }
                }

                Assert.Equal(entries, typeface.MaskAtlas.Count);

                // The frame that holds still draws from the atlas again.
                DrawAll(context, runs, Brushes.Black);
                context.FlushGlyphBatch();

                Assert.True(typeface.MaskAtlas.Count > entries, "the settling frame added no atlas entries");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Context_Kept_Across_Frames_Draws_Its_Glyph_Batch_Before_It_Outgrows_One_Atlas_Draw(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 5, 13, new Point(6.3, 4));
            var brush = new ImmutableSolidColorBrush(Color.FromArgb(0x10, 0x10, 0x20, 0x40));

            // Whole-pixel steps keep every run in the pending batch, and nothing between the frames
            // draws it: a context kept across frames whose surface is flushed by its owner.
            void DrawFrames(DrawingContextImpl context)
            {
                for (var frame = 0; frame < 300; frame++)
                {
                    context.Transform = Matrix.CreateTranslation(frame * 7 % 300, frame * 3 % 200);
                    DrawAll(context, runs, brush);
                }
            }

            try
            {
                var geometry = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                var expected = Render(gpu, DrawFrames, batched: false, out _);
                var sprites = DrawingContextImpl.AtlasGeometrySubmittedOnThread - geometry;
                var drawnWhileAppending = 0;

                var actual = Render(gpu, context =>
                {
                    var before = DrawingContextImpl.AtlasDrawsOnThread;

                    DrawFrames(context);
                    drawnWhileAppending = DrawingContextImpl.AtlasDrawsOnThread - before;
                }, batched: true, out var draws);

                Assert.True(sprites > 3 * MaxSpritesPerAtlasDraw, $"the frames drew only {sprites} sprites");

                // No batch holds more than one draw's sprites, so all but the last are drawn while
                // the runs are still being appended.
                Assert.True(drawnWhileAppending >= sprites / MaxSpritesPerAtlasDraw,
                    $"{sprites} sprites took {drawnWhileAppending} atlas draws before the session ended");
                Assert.True(draws >= (sprites + MaxSpritesPerAtlasDraw - 1) / MaxSpritesPerAtlasDraw,
                    $"{sprites} sprites took {draws} atlas draws");
                TransformedAtlasTests.AssertEqual(expected, actual, "frames batched in one session");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Run_Of_More_Sprites_Than_One_Atlas_Draw_Draws_The_Pixels_Of_Its_Stretches_Drawn_One_By_One(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = CreateGridRun(typeface, MaxSpritesPerAtlasDraw * 3 / 2, 12, new Point(4.3, 2));
            var stretches = CreateStretches(typeface, run, MaxSpritesPerAtlasDraw / 4);
            var brush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x10, 0x20, 0x40));

            try
            {
                var geometry = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                var batched = Render(gpu, context => context.DrawGlyphRun(brush, run), batched: true,
                    out var batchedDraws);
                var sprites = DrawingContextImpl.AtlasGeometrySubmittedOnThread - geometry;
                var unbatched = Render(gpu, context => context.DrawGlyphRun(brush, run), batched: false,
                    out var unbatchedDraws);
                var expected = Render(gpu, context => DrawAll(context, stretches, brush), batched: false, out _);
                var minimum = (sprites + MaxSpritesPerAtlasDraw - 1) / MaxSpritesPerAtlasDraw;

                Assert.True(sprites > MaxSpritesPerAtlasDraw, $"the run drew only {sprites} sprites");
                Assert.True(batchedDraws >= minimum, $"{sprites} batched sprites took {batchedDraws} atlas draws");
                Assert.True(unbatchedDraws >= minimum,
                    $"{sprites} unbatched sprites took {unbatchedDraws} atlas draws");
                TransformedAtlasTests.AssertEqual(expected, batched, "batched run");
                TransformedAtlasTests.AssertEqual(expected, unbatched, "unbatched run");
            }
            finally
            {
                DisposeAll(stretches);
            }
        }

        [Fact]
        public void Upright_Text_On_A_Software_Gpu_Keeps_Its_Run_Mask()
        {
            using var gpu = TransformedAtlasTests.CreateGpu(GpuBackend.NativeGl, software: true);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, s_lines[0], 15, new Point(9.37, 30.2));

            Render(gpu, context => context.DrawGlyphRun(Brushes.Black, run), batched: true, out _);

            Assert.Equal(1, run.RunMasks.Count);
            Assert.Equal(0, run.TransformedSprites.Count);
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Pending_Sprites_Draw_Before_The_Surface_Is_Read(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, s_lines[1], 15, new Point(9, 30));

            using var target = new SurfaceRenderTarget(new SurfaceRenderTarget.CreateInfo
            {
                Width = Width,
                Height = Height,
                Dpi = new Vector(96, 96),
                GrContext = gpu.GrContext,
                Format = Avalonia.Platform.PixelFormat.Rgba8888,
                DisableManualFbo = true,
            });

            using var context = (DrawingContextImpl)target.CreateDrawingContext();

            context.Clear(Colors.Transparent);
            context.DrawGlyphRun(Brushes.Black, run);

            // The context is still drawing: the snapshot must hold the run.
            using var image = target.SnapshotImage();

            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(image.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }
            }

            Assert.Contains(pixels, b => b != 0);
        }

        /// <summary>
        /// Upright runs in two colours and at fractional origins, static rotated runs, and runs
        /// that keep their run masks, interleaved with fills, clips, opacities and a layer.
        /// </summary>
        private sealed class Scene : IDisposable
        {
            private readonly List<ManagedGlyphRunImpl> _runs = new();
            private readonly ManagedGlyphRunImpl[] _paragraph;
            private readonly ManagedGlyphRunImpl[] _blue;
            private readonly ManagedGlyphRunImpl _clipped;
            private readonly ManagedGlyphRunImpl _rotated;
            private readonly ManagedGlyphRunImpl _rotatedTwin;
            private readonly ManagedGlyphRunImpl _faded;
            private readonly ManagedGlyphRunImpl _layered;
            private readonly ManagedGlyphRunImpl _overlapping;
            private readonly ManagedGlyphRunImpl _translucent;
            private readonly ManagedGlyphRunImpl[] _tail;

            public Scene(GlyphTypeface typeface)
            {
                _paragraph = Add(CreateParagraph(typeface, 5, 14, new Point(6.3, 2)));
                _blue = Add(CreateParagraph(typeface, 3, 12, new Point(40.71, 70.4)));
                _clipped = Add(WideRunMaskTests.CreateRun(typeface, s_lines[2], 16, new Point(120.5, 150.3)));
                _rotated = Add(WideRunMaskTests.CreateRun(typeface, s_lines[3], 13, new Point(0, 0)));
                _rotatedTwin = Add(WideRunMaskTests.CreateRun(typeface, s_lines[4], 13, new Point(0, 16)));
                _faded = Add(WideRunMaskTests.CreateRun(typeface, s_lines[0], 14, new Point(10.2, 250.6)));
                _layered = Add(WideRunMaskTests.CreateRun(typeface, s_lines[1], 14, new Point(10.6, 275.1)));
                _overlapping = Add(WideRunMaskTests.CreateRun(typeface, "Wavy AVATAR", 20, new Point(300, 250),
                    advanceScale: 0.7));
                _translucent = Add(WideRunMaskTests.CreateRun(typeface, s_lines[2], 12, new Point(260, 290.4)));
                _tail = Add(CreateParagraph(typeface, 2, 13, new Point(12.4, 300)));
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
                var blue = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90));

                context.Clear(Colors.White);

                DrawAll(context, _paragraph, Brushes.Black);

                // A translucent fill over the paragraph's last lines lands above them.
                context.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x80, 0)), null,
                    new RoundedRect(new Rect(30, 50, 200, 40)));

                DrawAll(context, _blue, blue);

                context.PushClip(new Rect(150, 130, 160, 30));
                context.DrawGlyphRun(Brushes.Black, _clipped);
                context.PopClip();

                var transform = context.Transform;

                context.Transform = Matrix.CreateRotation(Math.PI * 12 / 180) * Matrix.CreateTranslation(330.3, 160.7);
                context.DrawGlyphRun(Brushes.Black, _rotated);
                context.DrawGlyphRun(Brushes.Black, _rotatedTwin);
                context.Transform = transform;

                context.PushOpacity(0.5, null);
                context.DrawGlyphRun(Brushes.Black, _faded);
                context.PopOpacity();

                context.PushLayer(new Rect(0, 260, 300, 30));
                context.DrawGlyphRun(blue, _layered);
                context.PopLayer();

                context.DrawGlyphRun(Brushes.Black, _overlapping);
                context.DrawGlyphRun(new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x10, 0x10)), _translucent);

                context.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0, 0x80, 0x40)), null,
                    new Rect(250, 290, 120, 40));

                DrawAll(context, _tail, Brushes.Black);
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

        /// <summary>
        /// One run of <paramref name="count"/> glyphs laid out in a grid over the surface by their
        /// offsets alone, wrapping back to the top left so later glyphs draw over earlier ones.
        /// </summary>
        private static ManagedGlyphRunImpl CreateGridRun(GlyphTypeface typeface, int count, double em, Point origin)
        {
            const string characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            const int columns = 56;
            const int rows = 30;

            var infos = new List<GlyphInfo>(count);

            for (var i = 0; i < count; i++)
            {
                var glyph = typeface.CharacterToGlyphMap[characters[i % characters.Length]];
                var cell = i % (columns * rows);

                infos.Add(new GlyphInfo(glyph, i, 0, new Vector(cell % columns * 9, em + cell / columns * 11)));
            }

            return new ManagedGlyphRunImpl(typeface, em, infos, origin);
        }

        /// <summary>The run's glyphs as consecutive runs of at most <paramref name="length"/> glyphs each.</summary>
        private static ManagedGlyphRunImpl[] CreateStretches(GlyphTypeface typeface, ManagedGlyphRunImpl run, int length)
        {
            var stretches = new ManagedGlyphRunImpl[(run.GlyphCount + length - 1) / length];

            for (var i = 0; i < stretches.Length; i++)
            {
                var start = i * length;
                var count = Math.Min(length, run.GlyphCount - start);

                stretches[i] = new ManagedGlyphRunImpl(typeface, run.FontRenderingEmSize,
                    run.GlyphIndices.Slice(start, count), run.GlyphPositions.Slice(start * 2, count * 2),
                    run.BaselineOrigin, run.BrushBounds);
            }

            return stretches;
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

        private static readonly Color[] s_tints =
        {
            Colors.Black,
            Color.FromRgb(0x20, 0x40, 0x90),
            Color.FromRgb(0xF0, 0xE0, 0x30),
            Color.FromArgb(0x80, 0x10, 0x30, 0x60),
        };

        /// <summary>
        /// The run's upright glyph masks with their device pens, as the renderer lays them out
        /// for a draw under the identity transform.
        /// </summary>
        private static List<(GlyphMask Mask, int X, int Y)> UprightGlyphMasks(GlyphTypeface typeface,
            ManagedGlyphRunImpl run)
        {
            // The renderer's resolution of unspecified hinting through the gasp table.
            var em = run.FontRenderingEmSize;
            var gasp = typeface.Gasp;
            var gridFit = !gasp.IsBelowHintingFloor(em);
            var penSnap = gridFit && (gasp.WantsFullGridFit(em) ||
                                      (gasp.WantsBytecodeGridFit(em) && typeface.HasTrueTypeHinting));
            var scaleQ = GlyphMaskKey.QuantizeScale((float)em);

            void Snap(float x, out int pixel, out byte phase)
            {
                if (penSnap)
                {
                    pixel = (int)MathF.Round(x);
                    phase = 0;
                }
                else
                {
                    GlyphMaskKey.SnapPen(x, out pixel, out phase);
                }
            }

            Snap((float)run.BaselineOrigin.X, out var originX, out var originPhase);

            var originY = (int)Math.Round(run.BaselineOrigin.Y);
            var originFraction = originPhase * (1f / GlyphMaskKey.PhaseCount);
            var masks = new List<(GlyphMask Mask, int X, int Y)>();
            var scratch = new GlyphPathBuilder();
            var positions = run.GlyphPositions;

            for (var i = 0; i < run.GlyphCount; i++)
            {
                Snap(originFraction + positions[i * 2], out var penX, out var phase);

                var mask = GlyphMasks.Build(typeface, scratch, new GlyphMaskKey(run.GlyphIndices[i], scaleQ, phase,
                    GlyphMaskMode.Antialiased, gridFit, penSnap));

                masks.Add((mask, originX + penX, originY + (int)MathF.Round(positions[i * 2 + 1])));
            }

            return masks;
        }

        /// <summary>
        /// Renders a frame in one drawing session on an untouched GPU surface and reads it back,
        /// reporting the atlas draws the session issued.
        /// </summary>
        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw, bool batched, out int atlasDraws)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            var before = DrawingContextImpl.AtlasDrawsOnThread;

            using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
            {
                context.BatchesGlyphAtlasDraws = batched;
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
