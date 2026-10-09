using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;
using static Avalonia.Skia.UnitTests.Media.GlyphAtlasClipBatchTests;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// State changes between glyph runs batched on a GPU context. A change that cannot alter the
    /// pixels of the runs already pending leaves their batches pending, so text interleaved with
    /// it still draws in few atlas calls; every other change draws the pending runs first. The
    /// frame must hold exactly the pixels of drawing every run on its own.
    /// </summary>
    public class GlyphAtlasStateBatchTests
    {
        private static readonly Color s_background = Color.FromRgb(0xF4, 0xF0, 0xE6);

        public static IEnumerable<object[]> Targets() => HardwareTargets();

        public static IEnumerable<object[]> Contexts() => HardwareContexts();

        [Theory]
        [MemberData(nameof(Targets))]
        public void Opacity_Without_A_Layer_Leaves_The_Pending_Runs_Alone(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);

            // Every other row inside an opacity scope, which only scales the colour of the runs
            // drawn inside it.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    if (i % 2 == 1)
                    {
                        context.PushOpacity(0.5, null);
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                        context.PopOpacity();
                    }
                    else
                    {
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                    }
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Layer);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Layer) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in and out of opacity");
                Assert.Equal(0, flushed);

                // One batch for the opaque rows and one for the translucent ones at most; the
                // subpixel batch blends one colour per draw.
                if (!subpixel)
                {
                    Assert.InRange(draws, 1, 2);
                }
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Contexts))]
        public void Runs_In_Translucent_Colours_Of_One_Page_Draw_As_One_Atlas_Call(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);
            var brushes = new IBrush[]
            {
                new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x20, 0x40, 0x90)),
                new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x30, 0x30, 0x30)),
                Brushes.Black,
            };

            // Rows in two translucent colours and an opaque one scaled by an opacity, then the
            // first row again in the second colour, two pixels lower and three to the right, over
            // the first: it must land above it.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    if (i % 3 == 2)
                    {
                        context.PushOpacity(0.6, null);
                        context.DrawGlyphRun(brushes[2], rows[i]);
                        context.PopOpacity();
                    }
                    else
                    {
                        context.DrawGlyphRun(brushes[i % 3], rows[i]);
                    }
                }

                context.Transform = Matrix.CreateTranslation(3, 2);
                context.DrawGlyphRun(brushes[1], rows[0]);
                context.Transform = Matrix.Identity;
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel: false, out _);
                var cold = Render(gpu, Draw, batched: true, subpixel: false, out var coldDraws);
                var warm = Render(gpu, Draw, batched: true, subpixel: false, out var warmDraws);

                TransformedAtlasTests.AssertEqual(expected, cold, "rows in translucent colours");
                TransformedAtlasTests.AssertEqual(expected, warm, "warm rows in translucent colours");
                Assert.Equal(1, coldDraws);
                Assert.Equal(1, warmDraws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Fills_Beside_The_Pending_Runs_Leave_Them_Pending(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x80, 0));
            var green = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x90, 0x40));
            var pen = new ImmutablePen(Brushes.SteelBlue, 2);

            // Rows of text with a fill, a stroked ellipse and a line drawn before each, all of
            // them right of where any row's text ends, inside a viewport clip that holds them all.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.PushClip(new Rect(0, 0, Width, Height));

                for (var i = 0; i < rows.Length; i++)
                {
                    var top = i * RowHeight;

                    context.DrawRectangle(orange, null, new RoundedRect(new Rect(Width - 40, top + 4, 30, 16)));
                    context.DrawEllipse(green, pen, new Rect(Width - 80, top + 6.5, 12, 12));
                    context.DrawLine(pen, new Point(Width - 110, top + 5), new Point(Width - 95, top + 20));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                }

                context.PopClip();
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation) -
                              before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows with fills beside them");
                Assert.Equal(0, flushed);
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Fills_That_Reach_The_Pending_Runs_Draw_Them_First(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x80, 0));
            var thick = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x20, 0x60, 0xC0)), 14);

            // After each row, a fill that reaches it: over its words, left of it but with a
            // stroke wide enough to cover its first glyph, or beside it in local coordinates
            // but over it once scaled.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    var top = i * RowHeight;

                    context.DrawGlyphRun(Brushes.Black, rows[i]);

                    switch (i % 3)
                    {
                        case 0:
                            context.DrawRectangle(orange, null, new RoundedRect(new Rect(30, top + 8, 60, 10)));
                            break;
                        case 1:
                            context.DrawRectangle(null, thick, new RoundedRect(new Rect(0, top + 6, 4, 12)));
                            break;
                        default:
                            context.Transform = Matrix.CreateScale(2, 2);
                            context.DrawRectangle(orange, null,
                                new RoundedRect(new Rect(100, (top + 10) / 2.0, 20, 5)));
                            context.Transform = Matrix.Identity;
                            break;
                    }
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation) -
                              before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows with fills over them");
                Assert.Equal(12, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Fills_Under_A_Clip_That_Would_Cut_The_Pending_Runs_Leave_Them_Pending(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 6);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x80, 0));

            // Each row fills a marker beside its text under its band's clip, then draws its text
            // under the clip again. Applying the band's clip to the canvas would cut the rows still
            // pending above it, so the fill draws under the clip applied for it alone. Setting the
            // transform, as the compositor does per visual, lets the clips be recorded instead of
            // applied.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    context.Transform = Matrix.Identity;
                    context.PushClip(RowRect(i));
                    context.DrawRectangle(orange, null,
                        new RoundedRect(new Rect(Width - 40, i * RowHeight + 4, 30, 16)));
                    context.PopClip();
                    context.PushClip(RowRect(i));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation) -
                              before;

                TransformedAtlasTests.AssertEqual(expected, actual, "clipped rows with fills beside them");
                Assert.Equal(0, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Rows_Clipped_To_Pixel_Rectangles_Without_Corner_Radii_Draw_As_One_Atlas_Call(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x80, 0));

            // A control's own clips arrive as rounded rectangles, most of them without radii.
            // Every row clips to its band, cut short on the right, and the first row fills a
            // marker that the clip cuts as well.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    context.Transform = Matrix.Identity;
                    context.PushClip(new RoundedRect(new Rect(0, i * RowHeight, 200, RowHeight)));

                    if (i == 0)
                    {
                        context.DrawRectangle(orange, null,
                            new RoundedRect(new Rect(190, i * RowHeight + 4.5, 30, 16)));
                    }

                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in rounded clips without radii");
                Assert.Equal(0, flushed);
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Rounded_Clips_With_Radii_Or_Fractional_Edges_Draw_The_Pending_Runs_First(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 6);

            // Antialiased edges a pixel-aligned rectangle does not have: corner radii, or edges
            // between pixels.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    var band = RowRect(i);

                    context.Transform = Matrix.Identity;
                    context.PushClip(i % 2 == 0
                        ? new RoundedRect(new Rect(band.X + 12.5, band.Y + 2.25, 300.5, band.Height - 4.5))
                        : new RoundedRect(band.Deflate(new Thickness(12, 2)), 6));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in antialiased clips");

                // Every pop draws its own row.
                Assert.Equal(rows.Length, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Rows_In_Rounded_Clips_Clear_Of_Their_Corners_Draw_As_One_Atlas_Call(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);

            // Each row in a card clipped to a rounded rectangle with edges between pixels, as a
            // control with rounded corners clips its content. The text keeps clear of the corners
            // and edges.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    context.Transform = Matrix.Identity;
                    context.PushClip(new RoundedRect(new Rect(1.5, i * RowHeight - 4.25, Width - 3, 36.5), 6));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var clips = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in rounded clips");
                Assert.Equal(0, DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - clips);

                if (!subpixel)
                {
                    Assert.Equal(1, draws);
                }
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Shadows_Fills_And_Images_Of_Rounded_Cards_Leave_The_Pending_Runs_Alone(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var image = CreateImage();
            var rows = CreateRows(typeface, 12);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x80, 0));
            var shadow = BoxShadows.Parse("0 2 6 0 #40000000");

            // Each row is a card: a shadowed rectangle on the right before the card's rounded clip,
            // then a fill and an image under the clip that its right edge cuts, then the text. None
            // of them reaches the rows drawn before, but applying a card's clip would cut those.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    context.Transform = Matrix.Identity;
                    context.DrawRectangle(null, null, new RoundedRect(new Rect(Width - 100, i * RowHeight + 6, 60, 10), 3),
                        shadow);
                    context.PushClip(new RoundedRect(new Rect(1.5, i * RowHeight - 4.25, Width - 3, 36.5), 6));
                    context.DrawRectangle(orange, null, new RoundedRect(new Rect(Width - 12, i * RowHeight + 4, 30, 16)));
                    context.DrawBitmap(image, 1, new Rect(0, 0, 16, 16), new Rect(Width - 40, i * RowHeight + 5.5, 16, 16));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var operations = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, "rounded cards with shadows, fills and images");
                Assert.Equal(0,
                    DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation) - operations);

                if (!subpixel)
                {
                    Assert.Equal(1, draws);
                }
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        private static ImmutableBitmap CreateImage()
        {
            using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));

            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)(x * 16), (byte)(y * 16), 0x80));
                }
            }

            return new ImmutableBitmap(SKImage.FromBitmap(bitmap));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Rows_Reaching_Into_The_Corners_Of_Rounded_Clips_Draw_The_Pending_Runs_First(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 6);

            // The clips hug the text, so their corners round off its first and last glyphs.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                for (var i = 0; i < rows.Length; i++)
                {
                    context.Transform = Matrix.Identity;
                    context.PushClip(new RoundedRect(new Rect(10, i * RowHeight + 6, 180, 18), 8));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows cut by rounded corners");

                // Every row draws under its clip before the clip is popped.
                Assert.Equal(rows.Length, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Rounded_Clips_Pushed_After_A_Restore_Leave_The_Pending_Runs_Alone(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 8);

            // A layer before the rows restores the canvas, after which only the canvas knows the
            // transform; the compositor sets it again only when it changes.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.PushLayer(new Rect(0, 0, Width, 40));
                context.PopLayer();

                for (var i = 0; i < rows.Length; i++)
                {
                    context.PushClip(new RoundedRect(new Rect(1.5, i * RowHeight - 4.25, Width - 3, 36.5), 6));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in rounded clips after a restore");
                Assert.Equal(0, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Opacity_Through_A_Layer_Draws_The_Pending_Runs_First(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 6);

            // A layer composites what is drawn inside it at the opacity, so runs pending outside
            // it must reach the surface before it opens and those inside it before it closes.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.RenderOptions = context.RenderOptions with { RequiresFullOpacityHandling = true };

                for (var i = 0; i < rows.Length; i++)
                {
                    if (i % 2 == 1)
                    {
                        context.PushOpacity(0.5, null);
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                        context.PopOpacity();
                    }
                    else
                    {
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                    }
                }
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Layer);
                var actual = Render(gpu, Draw, batched: true, subpixel, out _);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Layer) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "rows in and out of opacity layers");

                // Each of the three layers draws the run pending before it opens and its own run
                // before it closes.
                Assert.Equal(6, flushed);
            }
            finally
            {
                DisposeAll(rows);
            }
        }
    }
}
