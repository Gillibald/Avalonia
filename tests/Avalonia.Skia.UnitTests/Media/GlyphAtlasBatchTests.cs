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

        // The most runs of other batches a run joining a pending batch is tested against for
        // overlap before every pending batch is drawn.
        private const int MaxPendingRuns = 128;

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
            // every line and the colour every other line: two pages, both colours opaque.
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
                Assert.Equal(2, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_List_Of_Rows_In_Sixteen_Typefaces_Draws_One_Atlas_Call_Per_Typeface(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out _);

            // Every typeface has an atlas of its own, so each one is a page and a pending batch.
            var typefaces = Enumerable.Range(0, 16).Select(i => LoadAsset(s_listFonts[i % s_listFonts.Length])).ToArray();
            var runs = CreateListRuns(typefaces, 48);

            void Draw(DrawingContextImpl context) => DrawAll(context, runs, Brushes.Black);

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                Render(gpu, Draw, batched: true, out _);

                var batched = Render(gpu, Draw, batched: true, out var draws);

                TransformedAtlasTests.AssertEqual(expected, batched, "list rows");
                Assert.Equal(typefaces.Length, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void List_Rows_In_Opaque_Colours_Of_One_Luminance_Bucket_Draw_One_Atlas_Call_Per_Typeface(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out _);

            var typefaces = Enumerable.Range(0, 6).Select(i => LoadAsset(s_listFonts[i % s_listFonts.Length])).ToArray();
            var runs = CreateListRuns(typefaces, 48);

            // Four colours of the darkest luminance bucket, so each typeface's runs sample one
            // page whatever their colour; each typeface draws in every colour.
            var brushes = new IBrush[]
            {
                Brushes.Black,
                new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x00, 0xC0)),
                new ImmutableSolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                new ImmutableSolidColorBrush(Color.FromRgb(0x40, 0x00, 0x10)),
            };

            void Draw(DrawingContextImpl context)
            {
                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(brushes[i / typefaces.Length % brushes.Length], runs[i]);
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);
                var cold = Render(gpu, Draw, batched: true, out _);
                var warm = Render(gpu, Draw, batched: true, out var draws);

                Assert.Single(typefaces[0].MaskAtlas.GetPages());
                TransformedAtlasTests.AssertEqual(expected, cold, "list rows in four colours");
                TransformedAtlasTests.AssertEqual(expected, warm, "warm list rows in four colours");
                Assert.Equal(typefaces.Length, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Code_Tokens_In_Opaque_Colours_Of_Several_Luminance_Buckets_Draw_One_Atlas_Call(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            Assert.True(s_syntaxColours.Select(c => MaskGamma.GetBucket(c.R, c.G, c.B)).Distinct().Count() >= 4,
                "the syntax colours do not span four luminance buckets");

            var (runs, colours) = CreateCodeTokens(typeface, 24);
            var brushes = colours.Select(c => (IBrush)new ImmutableSolidColorBrush(s_syntaxColours[c])).ToArray();

            void Draw(DrawingContextImpl context)
            {
                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(brushes[i], runs[i]);
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);
                var cold = Render(gpu, Draw, batched: true, out _);
                var warm = Render(gpu, Draw, batched: true, out var draws);

                TransformedAtlasTests.AssertEqual(expected, cold, "code tokens");
                TransformedAtlasTests.AssertEqual(expected, warm, "warm code tokens");
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Text_In_Colours_Of_Several_Buckets_Equals_Its_Corrected_Glyph_Masks_Drawn_One_By_One(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            var (runs, colours) = CreateCodeTokens(typeface, 12);

            try
            {
                var drawn = Render(gpu, context =>
                {
                    for (var i = 0; i < runs.Length; i++)
                    {
                        context.DrawGlyphRun(new ImmutableSolidColorBrush(s_syntaxColours[colours[i]]), runs[i]);
                    }
                }, batched: true, out _);

                foreach (var run in runs)
                {
                    Assert.Equal(0, run.RunMasks.Count);
                }

                // Every token's glyph masks through the coverage table of its own colour,
                // modulated by that colour, drawn in token order.
                var corrected = Render(gpu, context =>
                {
                    var canvas = context.Canvas;

                    for (var i = 0; i < runs.Length; i++)
                    {
                        var color = s_syntaxColours[colours[i]];
                        var table = MaskGamma.GetTable(color.R, color.G, color.B);

                        using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A) };

                        foreach (var (mask, x, y) in UprightGlyphMasks(typeface, runs[i]))
                        {
                            if (mask.IsEmpty)
                            {
                                continue;
                            }

                            var alpha = new byte[mask.Width * mask.Height];

                            for (var p = 0; p < alpha.Length; p++)
                            {
                                alpha[p] = table[mask.Alpha[p]];
                            }

                            using var image = TransformedAtlasTests.CreateAlphaImage(alpha, mask.Width, mask.Height);

                            canvas.DrawImage(image, x + mask.Left, y + mask.Top, new SKSamplingOptions(), paint);
                        }
                    }
                }, batched: true, out _);

                TransformedAtlasTests.AssertEqual(corrected, drawn, "tokens in colours of several buckets");
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Thousands_Of_Runs_Of_One_Typeface_And_Colour_Are_One_Atlas_Draw(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateCellRuns(typeface, 2000);

            try
            {
                var expected = Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: false, out _);

                Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true, out _);

                var batched = Render(gpu, context => DrawAll(context, runs, Brushes.Black), batched: true,
                    out var draws);

                TransformedAtlasTests.AssertEqual(expected, batched, "runs of one colour");
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Batches_Of_Other_Colours_Are_Drawn_Before_Their_Runs_Outnumber_The_Pending_Run_Limit(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateCellRuns(typeface, 2000);

            // An opaque and a translucent colour: runs of one page in both cannot share a batch.
            var brushes = new IBrush[]
            {
                Brushes.Black, new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x20, 0x40, 0x90)),
            };

            // Every run joins the batch of its colour while the other colour's batch is pending,
            // so each run is tested against the runs of the other batch.
            void Draw(DrawingContextImpl context)
            {
                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(brushes[i % 2], runs[i]);
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                Render(gpu, Draw, batched: true, out _);

                var drawnWhileAppending = 0;
                var batched = Render(gpu, context =>
                {
                    var before = DrawingContextImpl.AtlasDrawsOnThread;

                    Draw(context);
                    drawnWhileAppending = DrawingContextImpl.AtlasDrawsOnThread - before;
                }, batched: true, out var draws);

                // A run may join its batch only while fewer than the limit of other runs are
                // pending, so the two batches are drawn at least once per twice the limit of runs.
                var flushes = (runs.Length + 2 * MaxPendingRuns - 1) / (2 * MaxPendingRuns);

                TransformedAtlasTests.AssertEqual(expected, batched, "runs alternating colours");
                Assert.True(drawnWhileAppending >= 2 * (flushes - 1),
                    $"{runs.Length} runs took {drawnWhileAppending} atlas draws before the session ended");
                Assert.True(draws >= 2 * flushes, $"{runs.Length} runs took {draws} atlas draws");
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

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Overlapping_Runs_In_Colours_Of_One_Page_Keep_Their_Order(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var inter);

            // One luminance bucket, so one page: two opaque colours and a translucent one.
            var blue = new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x00, 0xC0));
            var translucent = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x10, 0x10));

            // Each run covers the one before it: none of them may be drawn out of order.
            var first = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(10, 60));
            var second = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(14, 66));
            var third = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(18, 72));
            var fourth = WideRunMaskTests.CreateRun(inter, "Overlapping words", 30, new Point(22, 78));

            void Draw(DrawingContextImpl context)
            {
                context.DrawGlyphRun(Brushes.Black, first);
                context.DrawGlyphRun(blue, second);
                context.DrawGlyphRun(translucent, third);
                context.DrawGlyphRun(Brushes.Black, fourth);
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                TransformedAtlasTests.AssertEqual(expected, Render(gpu, Draw, batched: true, out _),
                    "overlapping runs");
                TransformedAtlasTests.AssertEqual(expected, Render(gpu, Draw, batched: true, out var draws),
                    "warm overlapping runs");

                // The two opaque runs before the translucent one draw in one call.
                Assert.Equal(3, draws);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                third.Dispose();
                fourth.Dispose();
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Run_Whose_Glyphs_Alternate_Between_Two_Pages_Draws_One_Atlas_Call_Per_Page(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var firstPage = CreatePlacedRun(typeface, new Point(10, 20), FirstPageGlyphs());
            using var run = CreateAlternatingPageRun(typeface, new Point(10, 60));

            Render(gpu, context => context.DrawGlyphRun(Brushes.Black, firstPage), batched: true, out _);
            FillFirstAtlasPage(typeface);

            void Draw(DrawingContextImpl context) => context.DrawGlyphRun(Brushes.Black, run);

            var expected = Render(gpu, Draw, batched: false, out _);

            Assert.Equal(2, typeface.MaskAtlas.GetPages().Length);

            var cold = Render(gpu, Draw, batched: true, out _);
            var warm = Render(gpu, Draw, batched: true, out var draws);

            TransformedAtlasTests.AssertEqual(expected, cold, "run on two pages");
            TransformedAtlasTests.AssertEqual(expected, warm, "warm run on two pages");
            Assert.Equal(2, draws);
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Parts_Of_One_Run_On_Two_Pages_Do_Not_Flush_Each_Other(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var firstPage = CreatePlacedRun(typeface, new Point(10, 20), FirstPageGlyphs());

            Render(gpu, context => context.DrawGlyphRun(Brushes.Black, firstPage), batched: true, out _);
            FillFirstAtlasPage(typeface);

            // Lines that never touch one another, each run's parts on the two pages overlapping
            // in their bounds but in no glyph.
            var runs = Enumerable.Range(0, 12)
                .Select(i => CreateAlternatingPageRun(typeface, new Point(10, 50 + i * 24)))
                .ToArray();

            void Draw(DrawingContextImpl context) => DrawAll(context, runs, Brushes.Black);

            try
            {
                var expected = Render(gpu, Draw, batched: false, out _);

                Assert.Equal(2, typeface.MaskAtlas.GetPages().Length);

                var cold = Render(gpu, Draw, batched: true, out _);
                var pageChanges = DrawingContextImpl.GetFlushesOnThread(GlyphBatchFlushReason.PageChange);
                var warm = Render(gpu, Draw, batched: true, out var draws);

                pageChanges = DrawingContextImpl.GetFlushesOnThread(GlyphBatchFlushReason.PageChange) - pageChanges;

                TransformedAtlasTests.AssertEqual(expected, cold, "lines on two pages");
                TransformedAtlasTests.AssertEqual(expected, warm, "warm lines on two pages");
                Assert.Equal(0, pageChanges);
                Assert.Equal(2, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Run_Grouped_By_Page_Keeps_The_Order_Of_Overlapping_Glyphs(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var firstPage = CreatePlacedRun(typeface, new Point(10, 20), FirstPageGlyphs());

            var background = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xE0, 0xD0));
            var brushes = new IBrush[]
            {
                new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90)),
                new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x60, 0x10, 0x30)),
            };

            // An entry holds coverage corrected for its colour's luminance, so the upper-case
            // letters go on the first page in both colours.
            foreach (var brush in brushes)
            {
                Render(gpu, context => context.DrawGlyphRun(brush, firstPage), batched: true, out _);
            }

            FillFirstAtlasPage(typeface);

            // Glyphs of the two pages alternate. In the first stretch each glyph covers the one
            // before it and the one before that, so no glyph may be drawn ahead of a glyph of
            // the other page; in the second the glyphs stand apart, and a glyph that covers one
            // of the other page follows.
            var glyphs = new List<(char Character, int X, int Y)>();

            for (var i = 0; i < 12; i++)
            {
                glyphs.Add(((char)(i % 2 == 0 ? 'A' + i / 2 : 'a' + i / 2), i * 4, 0));
            }

            for (var i = 0; i < 8; i++)
            {
                glyphs.Add(((char)(i % 2 == 0 ? 'H' + i / 2 : 'h' + i / 2), 80 + i * 20, 0));
            }

            glyphs.Add(('L', 145, 2));
            glyphs.Add(('m', 230, 0));
            glyphs.Add(('M', 234, -3));

            var origin = new Point(12, 70);
            var placed = glyphs.ToArray();

            using var run = CreatePlacedRun(typeface, origin, placed);

            var singles = placed.Select(g => CreatePlacedRun(typeface, origin, g)).ToArray();

            try
            {
                foreach (var brush in brushes)
                {
                    void Background(DrawingContextImpl context) =>
                        context.DrawRectangle(background, null, new RoundedRect(new Rect(0, 0, Width, Height)));

                    // The run drawn one glyph at a time, in its order.
                    var expected = Render(gpu, context =>
                    {
                        Background(context);
                        DrawAll(context, singles, brush);
                    }, batched: false, out _);

                    void Draw(DrawingContextImpl context)
                    {
                        Background(context);
                        context.DrawGlyphRun(brush, run);
                    }

                    var unbatched = Render(gpu, Draw, batched: false, out _);
                    var batched = Render(gpu, Draw, batched: true, out _);

                    Assert.Equal(2, typeface.MaskAtlas.GetPages().Length);
                    TransformedAtlasTests.AssertEqual(expected, unbatched, $"{brush} unbatched");
                    TransformedAtlasTests.AssertEqual(expected, batched, $"{brush} batched");
                }
            }
            finally
            {
                DisposeAll(singles);
            }
        }

        /// <summary>The upper-case letters that <see cref="CreateAlternatingPageRun"/> takes from the first page.</summary>
        private static (char Character, int X, int Y)[] FirstPageGlyphs()
            => Enumerable.Range(0, 16).Select(i => ((char)('A' + i), i * 16, 0)).ToArray();

        /// <summary>
        /// A run alternating between upper-case letters, whose masks the caller placed on the
        /// first page, and lower-case ones, which land on the second: in each block two upper-case
        /// letters, then two lower-case ones set between and after them. Each page's letters of
        /// a block span those of the other page, so the parts of the run on either page overlap
        /// in their bounds, while no two glyphs overlap.
        /// </summary>
        private static ManagedGlyphRunImpl CreateAlternatingPageRun(GlyphTypeface typeface, Point origin)
        {
            var glyphs = new List<(char Character, int X, int Y)>();

            for (var block = 0; block < 6; block++)
            {
                var x = block * 64;

                glyphs.Add(((char)('A' + 2 * block), x, 0));
                glyphs.Add(((char)('A' + 2 * block + 1), x + 32, 0));
                glyphs.Add(((char)('a' + 2 * block), x + 16, 0));
                glyphs.Add(((char)('a' + 2 * block + 1), x + 48, 0));
            }

            return CreatePlacedRun(typeface, origin, glyphs.ToArray());
        }

        /// <summary>
        /// A 12 px run of the given letters at whole-pixel offsets from a whole-pixel origin, so a
        /// letter has the same glyph mask, and atlas entry, wherever a run places it.
        /// </summary>
        private static ManagedGlyphRunImpl CreatePlacedRun(GlyphTypeface typeface, Point origin,
            params (char Character, int X, int Y)[] glyphs)
        {
            var infos = glyphs
                .Select((g, i) => new GlyphInfo(typeface.CharacterToGlyphMap[g.Character], i, 0, new Vector(g.X, g.Y)))
                .ToList();

            return new ManagedGlyphRunImpl(typeface, 12, infos, origin);
        }

        /// <summary>
        /// Fills the typeface's only atlas page to its last row and column with empty entries, so
        /// glyph masks placed from now on land on a second page while those placed before stay
        /// on the first.
        /// </summary>
        private static void FillFirstAtlasPage(GlyphTypeface typeface)
        {
            var atlas = typeface.MaskAtlas;
            var page = Assert.Single(atlas.GetPages());
            var tick = atlas.Tick();
            var filler = 0;

            // A scale no run draws at, so the entries never meet a glyph's key.
            void Add(int width, int height)
            {
                var key = new GlyphMaskKey((ushort)(ushort.MaxValue - filler++), 1, 0, GlyphMaskMode.Antialiased);

                Assert.True(atlas.TryAdd(key, new GlyphMask(new byte[width * height], width, height, 0, 0), tick,
                    out var slot));
                Assert.Same(page, slot.Page);
            }

            // An entry sized to a shelf's remaining width and its height may still land on an
            // earlier shelf of a similar height, so repeat until every shelf is full.
            while (page.Shelves.FirstOrDefault(s => s.X < GlyphMaskAtlas.PageWidth - 1) is { Height: > 0 } shelf)
            {
                Add(GlyphMaskAtlas.PageWidth - shelf.X - 1, shelf.Height - 1);
            }

            var rows = GlyphMaskAtlas.MaxPageHeight - page.UsedHeight;

            if (rows > 1)
            {
                Add(GlyphMaskAtlas.PageWidth - 2, rows - 1);
            }

            Assert.True(page.UsedHeight >= GlyphMaskAtlas.MaxPageHeight - 1, $"the page fills {page.UsedHeight} rows");
            Assert.Single(atlas.GetPages());
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
        public void Text_That_Stops_And_Starts_Scrolling_Draws_The_Pixels_Of_Its_Runs_Drawn_One_By_One(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 12, 13, new Point(6.3, 4));

            // Two opaque colours of one luminance bucket share a batch, coloured per vertex.
            IBrush[] brushes =
            {
                new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
                new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x40, 0xA0)),
            };

            void Draw(DrawingContextImpl context, int offset, bool mixed)
            {
                context.Transform = Matrix.CreateTranslation(3, -offset);

                for (var i = 0; i < runs.Length; i++)
                {
                    context.DrawGlyphRun(mixed ? brushes[i % 2] : Brushes.Black, runs[i]);
                }
            }

            try
            {
                foreach (var mixed in new[] { false, true })
                {
                    foreach (var offset in new[] { 0, 0, 0, 7, 14, 14, 14, 21, 0, 0 })
                    {
                        var batched = Render(gpu, context => Draw(context, offset, mixed), batched: true, out _);
                        var expected = Render(gpu, context => Draw(context, offset, mixed), batched: false, out _);

                        TransformedAtlasTests.AssertEqual(expected, batched,
                            $"{(mixed ? "two colours" : "one colour")}, offset {offset}");
                    }
                }
            }
            finally
            {
                DisposeAll(runs);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Run_Drawn_Again_And_Again_As_Its_Inputs_Change_Draws_The_Pixels_Of_A_Fresh_Run(
            GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var run = WideRunMaskTests.CreateRun(typeface, s_lines[1], 13, new Point(6.3, 30));

            var blue = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90));
            var shifted = Matrix.CreateTranslation(10, 20);
            var steps = new (Matrix Transform, TextRenderingMode Rendering, TextHintingMode Hinting, IBrush Brush)[]
            {
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateTranslation(10, 27), TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateTranslation(13, -4), TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateTranslation(13, -4), TextRenderingMode.Antialias, TextHintingMode.Strong, Brushes.Black),
                (Matrix.CreateTranslation(15.3, -4), TextRenderingMode.Antialias, TextHintingMode.Strong, Brushes.Black),
                (Matrix.CreateTranslation(10.4, 20), TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateTranslation(10.4, 20), TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.None, Brushes.Black),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.None, Brushes.Black),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Strong, Brushes.Black),
                (shifted, TextRenderingMode.Alias, TextHintingMode.Unspecified, Brushes.Black),
                (shifted, TextRenderingMode.Alias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateScale(1.25, 1.25) * shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (Matrix.CreateScale(1.25, 1.25) * shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, blue),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, blue),
                (shifted, TextRenderingMode.Antialias, TextHintingMode.Unspecified, Brushes.Black),
            };

            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];

                void Draw(DrawingContextImpl context, ManagedGlyphRunImpl drawn)
                {
                    context.Transform = step.Transform;
                    Assert.True(MaskGlyphRunRenderer.TryDraw(context, drawn, step.Brush, step.Rendering, step.Hinting));
                }

                var again = Render(gpu, context => Draw(context, run), batched: true, out _);

                using var fresh = WideRunMaskTests.CreateRun(typeface, s_lines[1], 13, new Point(6.3, 30));

                var expected = Render(gpu, context => Draw(context, fresh), batched: true, out _);

                TransformedAtlasTests.AssertEqual(expected, again, $"step {i}");
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
        public void Pending_Batches_Are_Counted_By_The_Reason_They_Were_Drawn(GpuBackend backend, bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var inter);

            var noto = LoadAsset("NotoSans-Italic.ttf");
            var paragraph = CreateParagraph(inter, 3, 13, new Point(6.3, 4));
            var first = WideRunMaskTests.CreateRun(inter, "Overlapping words", 20, new Point(10, 160));
            var second = WideRunMaskTests.CreateRun(noto, "Overlapping words", 20, new Point(14, 166));
            var clipped = WideRunMaskTests.CreateRun(inter, s_lines[2], 14, new Point(20, 230));
            var last = WideRunMaskTests.CreateRun(inter, s_lines[3], 14, new Point(20, 300));

            // Each step leaves one batch pending for the next to draw.
            void Draw(DrawingContextImpl context)
            {
                DrawAll(context, paragraph, Brushes.Black);
                context.DrawRectangle(Brushes.Orange, null, new RoundedRect(new Rect(300, 10, 40, 40)));
                context.DrawGlyphRun(Brushes.Black, first);
                context.DrawGlyphRun(Brushes.Black, second);
                // A clip with fractional edges draws the batches pending around it.
                context.PushClip(new Rect(0, 200.5, 400, 60));
                context.DrawGlyphRun(Brushes.Black, clipped);
                context.PopClip();
                context.DrawGlyphRun(Brushes.Black, last);
            }

            var reasons = Enum.GetValues<GlyphBatchFlushReason>();

            (long[] Batches, long[] Flushes, long Runs, long Drawn, long PageBytes) Read() => (
                reasons.Select(DrawingContextImpl.GetBatchesFlushedOnThread).ToArray(),
                reasons.Select(DrawingContextImpl.GetFlushesOnThread).ToArray(),
                DrawingContextImpl.BatchedRunsOnThread, DrawingContextImpl.BatchesDrawnOnThread,
                DrawingContextImpl.PageImageBytesOnThread);

            try
            {
                var cold = Read();

                Render(gpu, Draw, batched: true, out _);

                var warm = Read();

                // Two typefaces, a page each, every page uploaded whole at least once.
                Assert.True(warm.PageBytes - cold.PageBytes >= 2L * GlyphMaskAtlas.PageWidth,
                    $"the cold frame uploaded {warm.PageBytes - cold.PageBytes} page bytes");

                DrawingContextImpl.TakeMaxRunsPerBatchOnThread();
                Render(gpu, Draw, batched: true, out _);

                var after = Read();
                var batches = new Dictionary<GlyphBatchFlushReason, long>();

                for (var i = 0; i < reasons.Length; i++)
                {
                    Assert.Equal(after.Batches[i] - warm.Batches[i], after.Flushes[i] - warm.Flushes[i]);

                    if (after.Batches[i] != warm.Batches[i])
                    {
                        batches[reasons[i]] = after.Batches[i] - warm.Batches[i];
                    }
                }

                Assert.Equal(new Dictionary<GlyphBatchFlushReason, long>
                {
                    [GlyphBatchFlushReason.CanvasOperation] = 1,
                    [GlyphBatchFlushReason.PageChange] = 1,
                    [GlyphBatchFlushReason.Clip] = 2,
                    [GlyphBatchFlushReason.EndOfSession] = 1,
                }, batches);
                Assert.Equal(5, after.Drawn - warm.Drawn);
                Assert.Equal(7, after.Runs - warm.Runs);
                Assert.Equal(3, DrawingContextImpl.TakeMaxRunsPerBatchOnThread());
                Assert.Equal(0, after.PageBytes - warm.PageBytes);
            }
            finally
            {
                DisposeAll(paragraph);
                first.Dispose();
                second.Dispose();
                clipped.Dispose();
                last.Dispose();
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Phase_Timers_Split_Cold_From_Warm_Draws_And_Record_Nothing_While_Disabled(GpuBackend backend,
            bool software)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, software);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var runs = CreateParagraph(typeface, 4, 13, new Point(6.3, 4));
            var phases = Enum.GetValues<GlyphTimerPhase>();

            long[] Counts() => phases.Select(GlyphPhaseTimers.GetCountOnThread).ToArray();

            long Delta(long[] before, long[] after, GlyphTimerPhase phase) =>
                after[(int)phase] - before[(int)phase];

            void Draw(DrawingContextImpl context) => DrawAll(context, runs, Brushes.Black);

            var wasEnabled = GlyphPhaseTimers.Enabled;

            try
            {
                GlyphPhaseTimers.Enabled = true;

                var batches = DrawingContextImpl.BatchesDrawnOnThread;
                var cold = Counts();

                Render(gpu, Draw, batched: true, out _);

                var warm = Counts();

                Assert.Equal(runs.Length, Delta(cold, warm, GlyphTimerPhase.GlyphRun));
                Assert.Equal(runs.Length, Delta(cold, warm, GlyphTimerPhase.MaskRunDraw));
                Assert.Equal(runs.Length, Delta(cold, warm, GlyphTimerPhase.SpriteSetBuild));
                Assert.True(Delta(cold, warm, GlyphTimerPhase.Rasterize) > 0);
                Assert.True(Delta(cold, warm, GlyphTimerPhase.AtlasWrite) > 0);
                Assert.Equal(1, Delta(cold, warm, GlyphTimerPhase.PageRewrap));
                Assert.Equal(DrawingContextImpl.BatchesDrawnOnThread - batches,
                    Delta(cold, warm, GlyphTimerPhase.BatchDraw));
                Assert.Equal(1, Delta(cold, warm, GlyphTimerPhase.NativeDrawFresh));

                Render(gpu, Draw, batched: true, out _);

                var after = Counts();

                Assert.Equal(runs.Length, Delta(warm, after, GlyphTimerPhase.GlyphRun));
                Assert.Equal(0, Delta(warm, after, GlyphTimerPhase.SpriteSetBuild));
                Assert.Equal(0, Delta(warm, after, GlyphTimerPhase.Rasterize));
                Assert.Equal(0, Delta(warm, after, GlyphTimerPhase.PageRewrap));
                Assert.Equal(0, Delta(warm, after, GlyphTimerPhase.NativeDrawFresh));
                Assert.Equal(Delta(warm, after, GlyphTimerPhase.BatchDraw), Delta(warm, after, GlyphTimerPhase.NativeDraw));
                Assert.Equal(0, Delta(warm, after, GlyphTimerPhase.BackendDrawText));

                GlyphPhaseTimers.Enabled = false;
                Render(gpu, Draw, batched: true, out _);

                Assert.Equal(after, Counts());
            }
            finally
            {
                GlyphPhaseTimers.Enabled = wasEnabled;
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

        private static readonly string[] s_listFonts =
        {
            "Inter-Regular.ttf", "NotoSans-Italic.ttf", "Manrope-Light.ttf", "Inter-Bold.ttf",
        };

        /// <summary>
        /// <paramref name="count"/> short runs laid out as the rows of a two-column list, none
        /// touching another, the typeface changing from each run to the next.
        /// </summary>
        private static ManagedGlyphRunImpl[] CreateListRuns(GlyphTypeface[] typefaces, int count)
        {
            var runs = new ManagedGlyphRunImpl[count];

            Assert.True(12 + (count - 1) / 2 * 14 < Height, $"{count} rows do not fit the surface");

            for (var i = 0; i < count; i++)
            {
                var line = s_lines[i % s_lines.Length];

                runs[i] = WideRunMaskTests.CreateRun(typefaces[i % typefaces.Length], line.Substring(0, 28), 9,
                    new Point(6.3 + i % 2 * 250, 12 + i / 2 * 14));
            }

            return runs;
        }

        private static readonly string[] s_codeLines =
        {
            "// Lays out the rows that fit the viewport.",
            "public int Measure(double width, int count = 42)",
            "{",
            "    var label = \"rows\" + count.ToString();",
            "    if (width > 0.5 && count != 7) return 128;",
            "    for (var i = 0; i < 16; i++) Log(\"row\", i);",
            "    return label.Length * 3;",
            "}",
        };

        private static readonly string[] s_keywords = { "public", "int", "double", "var", "if", "return", "for" };

        // Identifiers and punctuation, keywords, numbers, strings and comments: syntax colours
        // of five different luminance buckets.
        private static readonly Color[] s_syntaxColours =
        {
            Color.FromRgb(0x00, 0x00, 0x00),
            Color.FromRgb(0x20, 0x40, 0x90),
            Color.FromRgb(0x00, 0x80, 0x00),
            Color.FromRgb(0xD0, 0x70, 0x00),
            Color.FromRgb(0x60, 0x90, 0x60),
        };

        /// <summary>
        /// <paramref name="lineCount"/> lines of code laid out as one run per token, the tokens
        /// of a line abutting one another, and the index into <see cref="s_syntaxColours"/> of
        /// each token's colour.
        /// </summary>
        private static (ManagedGlyphRunImpl[] Runs, int[] Colours) CreateCodeTokens(GlyphTypeface typeface,
            int lineCount)
        {
            const double em = 12;

            var scale = em / typeface.Metrics.DesignEmHeight;
            var runs = new List<ManagedGlyphRunImpl>();
            var colours = new List<int>();

            Assert.True(12 + (lineCount - 1) * 14 < Height, $"{lineCount} lines do not fit the surface");

            double Advance(string text)
            {
                var advance = 0.0;

                foreach (var c in text)
                {
                    typeface.TryGetGlyphMetrics(typeface.CharacterToGlyphMap[c], out var metrics);
                    advance += metrics.AdvanceWidth * scale;
                }

                return advance;
            }

            for (var line = 0; line < lineCount; line++)
            {
                var text = s_codeLines[line % s_codeLines.Length];
                var x = 6.3;
                var y = 12 + line * 14;

                foreach (var (token, colour) in Tokenize(text))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        runs.Add(WideRunMaskTests.CreateRun(typeface, token, em, new Point(x, y)));
                        colours.Add(colour);
                    }

                    x += Advance(token);
                }
            }

            return (runs.ToArray(), colours.ToArray());
        }

        private static IEnumerable<(string Token, int Colour)> Tokenize(string line)
        {
            var i = 0;

            while (i < line.Length)
            {
                var start = i;
                var c = line[i];

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    yield return (line.Substring(i), 4);
                    yield break;
                }

                if (c == '"')
                {
                    i = line.IndexOf('"', i + 1) + 1;
                    yield return (line.Substring(start, i - start), 3);
                    continue;
                }

                if (char.IsLetter(c))
                {
                    while (i < line.Length && char.IsLetterOrDigit(line[i]))
                    {
                        i++;
                    }

                    var word = line.Substring(start, i - start);

                    yield return (word, Array.IndexOf(s_keywords, word) >= 0 ? 1 : 0);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                    {
                        i++;
                    }

                    yield return (line.Substring(start, i - start), 2);
                    continue;
                }

                var whitespace = char.IsWhiteSpace(c);

                while (i < line.Length && char.IsWhiteSpace(line[i]) == whitespace && !char.IsLetterOrDigit(line[i]) &&
                       line[i] != '"' && !(line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/'))
                {
                    i++;
                }

                yield return (line.Substring(start, i - start), 0);
            }
        }

        /// <summary>
        /// <paramref name="count"/> runs of one small glyph each, one per cell of a grid over the
        /// surface, far enough apart that no two of them overlap.
        /// </summary>
        private static ManagedGlyphRunImpl[] CreateCellRuns(GlyphTypeface typeface, int count)
        {
            const int columns = 52;

            var runs = new ManagedGlyphRunImpl[count];

            Assert.True(8 + (count - 1) / columns * 9 < Height, $"{count} cells do not fit the surface");

            for (var i = 0; i < count; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, "o", 7,
                    new Point(2.3 + i % columns * 10, 8 + i / columns * 9));
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
