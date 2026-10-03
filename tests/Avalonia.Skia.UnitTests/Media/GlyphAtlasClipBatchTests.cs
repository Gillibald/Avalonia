using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Glyph runs batched on a GPU context under clips. A run inside a pixel-aligned rectangle
    /// clip, or cut by one, stays pending across the clip's push and pop, so rows of text that
    /// each clip to their bounds draw in one atlas call; every other clip draws the pending runs
    /// first. The frame must hold exactly the pixels of drawing every run on its own under its
    /// clips, whatever the runs are interleaved with.
    /// </summary>
    public class GlyphAtlasClipBatchTests
    {
        private const int Width = 520;
        private const int Height = 360;
        private const int RowHeight = 26;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
            "Pack my box with five dozen liquor jugs; sphinx of black",
            "quartz, judge my vow! Numbers such as 1234567890 appear.",
        };

        private static readonly Color s_background = Color.FromRgb(0xF4, 0xF0, 0xE6);

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
        }

        public static IEnumerable<object[]> HardwareTargets()
        {
            foreach (var backend in new[] { GpuBackend.NativeGl, GpuBackend.Angle, GpuBackend.Metal })
            {
                yield return new object[] { backend, false };
                yield return new object[] { backend, true };
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Rows_Each_Clipped_To_A_Pixel_Rectangle_Around_Them_Draw_As_One_Atlas_Call(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);

            // A list: a viewport clip around rows that each clip to their own bounds.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.PushClip(new Rect(0, 0, Width, Height));

                for (var i = 0; i < rows.Length; i++)
                {
                    context.PushClip(RowRect(i));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                }

                context.PopClip();
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, "batched rows");
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Runs_Cut_By_Pixel_Rectangle_Clips_Draw_As_One_Atlas_Call_With_The_Pixels_Of_Their_Clipped_Draws(
            GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 10);

            Rect[] clips =
            {
                // Through the tops of the glyphs, the first glyph, the descenders, and the right
                // part of the run.
                new(0, 8, Width, RowHeight),
                new(14, RowHeight, Width, RowHeight),
                new(0, 2 * RowHeight, Width, 14),
                new(0, 3 * RowHeight, 100, RowHeight),
                // Two clips, each cutting one end of the run.
                new(50, 4 * RowHeight, Width, RowHeight),
                // Past the end of the run, and a few pixels inside one glyph.
                new(Width - 5, 5 * RowHeight, 5, RowHeight),
                new(20, 6 * RowHeight + 5, 7, 7),
            };

            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);

                // A viewport that cuts the first and the last row.
                context.PushClip(new Rect(0, 12, Width, rows.Length * RowHeight - 20));

                for (var i = 0; i < rows.Length; i++)
                {
                    context.PushClip(i < clips.Length ? clips[i] : RowRect(i));

                    if (i == 4)
                    {
                        context.PushClip(new Rect(0, 4 * RowHeight, 200, RowHeight));
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                        context.PopClip();
                    }
                    else
                    {
                        context.DrawGlyphRun(Brushes.Black, rows[i]);
                    }

                    context.PopClip();
                }

                context.PopClip();
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, "batched clipped rows");
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Fractional_And_Rounded_Clips_Draw_The_Pending_Runs_First(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 5);

            // Each clip contains the run drawn inside it, and still draws the run pending before
            // it when pushed and the run inside it when popped.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.DrawGlyphRun(Brushes.Black, rows[0]);

                context.PushClip(new Rect(0.5, RowHeight + 0.25, Width - 1, RowHeight - 0.5));
                context.DrawGlyphRun(Brushes.Black, rows[1]);
                context.PopClip();

                context.DrawGlyphRun(Brushes.Black, rows[2]);

                context.PushClip(new RoundedRect(RowRect(3), 6));
                context.DrawGlyphRun(Brushes.Black, rows[3]);
                context.PopClip();

                context.DrawGlyphRun(Brushes.Black, rows[4]);
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.Clip) - before;

                TransformedAtlasTests.AssertEqual(expected, actual, "batched frame");
                Assert.Equal(4, flushed);
                Assert.Equal(5, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Fills_And_Transforms_Between_Clipped_Runs_Keep_Their_Order(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 9);
            var orange = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x80, 0));
            var green = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0x80, 0x40));
            var blue = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x40, 0x90));

            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.PushClip(new Rect(0, 0, Width, Height));

                // A fill over a clipped run, drawn after its clip is popped, lands above it.
                context.PushClip(RowRect(0));
                context.DrawGlyphRun(Brushes.Black, rows[0]);
                context.PopClip();
                context.DrawRectangle(orange, null, new RoundedRect(new Rect(30, 4, 120, 30)));

                // A fill inside a clip is cut by it, and the run after it lands above it.
                context.PushClip(RowRect(1));
                context.DrawRectangle(green, null, new RoundedRect(new Rect(20, RowHeight - 10, 200, 46)));
                context.DrawGlyphRun(Brushes.Black, rows[1]);
                context.PopClip();

                // A run moved by a transform set inside its clip. Popping the clip restores the
                // transform it was pushed under, which the fill after it is drawn with.
                context.Transform = Matrix.CreateTranslation(4, 0);
                context.PushClip(new Rect(0, 2 * RowHeight, Width - 8, RowHeight));
                context.Transform = Matrix.CreateTranslation(9, 0);
                context.DrawGlyphRun(blue, rows[2]);
                context.PopClip();
                context.DrawRectangle(orange, null, new RoundedRect(new Rect(0, 2 * RowHeight + 8, 60, 10)));
                context.Transform = Matrix.Identity;

                // A clip pushed under a scale, pixel-aligned on the device, around an upright run.
                context.Transform = Matrix.CreateScale(2, 2);
                context.PushClip(new Rect(0, 3 * RowHeight / 2.0, Width / 2.0, RowHeight / 2.0 - 3));
                context.Transform = Matrix.Identity;
                context.DrawGlyphRun(Brushes.Black, rows[3]);
                context.PopClip();
                context.Transform = Matrix.Identity;

                // Overlapping runs of two colours in clips of their own keep their order.
                context.PushClip(RowRect(4));
                context.DrawGlyphRun(Brushes.Black, rows[4]);
                context.PopClip();
                context.Transform = Matrix.CreateTranslation(3, 2);
                context.PushClip(new Rect(0, 4 * RowHeight, Width, RowHeight));
                context.DrawGlyphRun(blue, rows[4]);
                context.PopClip();
                context.Transform = Matrix.Identity;

                // A rounded clip inside a rectangle clip, with runs on both sides of it.
                context.PushClip(new Rect(0, 5 * RowHeight, Width, 2 * RowHeight));
                context.DrawGlyphRun(Brushes.Black, rows[5]);
                context.PushClip(new RoundedRect(new Rect(10, 6 * RowHeight, 300, RowHeight), 8));
                context.DrawGlyphRun(blue, rows[6]);
                context.PopClip();
                context.DrawEllipse(green, null, new Rect(100, 5 * RowHeight, 80, 2 * RowHeight));
                context.PopClip();

                // An opacity between clipped runs.
                context.PushClip(RowRect(7));
                context.PushOpacity(0.5, null);
                context.DrawGlyphRun(Brushes.Black, rows[7]);
                context.PopOpacity();
                context.PopClip();

                context.PushClip(RowRect(8));
                context.DrawGlyphRun(Brushes.Black, rows[8]);
                context.PopClip();
                context.DrawRectangle(orange, null, new RoundedRect(new Rect(200, 8 * RowHeight + 5, 50, 12)));

                context.PopClip();
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                TransformedAtlasTests.AssertEqual(expected, Render(gpu, Draw, batched: true, subpixel, out _),
                    "batched frame");
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Transparent_Item_Backgrounds_Between_Clipped_Rows_Leave_The_Pending_Runs_Alone(GpuBackend backend,
            bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 12);
            var clear = new ImmutableSolidColorBrush(Colors.Transparent);

            // List items: each clips to its bounds and fills a transparent background before the
            // clipped text in it, as a templated item container does.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.PushClip(new Rect(0, 0, Width, Height));

                for (var i = 0; i < rows.Length; i++)
                {
                    context.PushClip(RowRect(i));
                    context.DrawRectangle(i % 2 == 0 ? clear : Brushes.Transparent, null,
                        new RoundedRect(RowRect(i), i % 3 == 0 ? 4 : 0));
                    context.PushClip(RowRect(i).Deflate(new Thickness(4, 1)));
                    context.DrawGlyphRun(Brushes.Black, rows[i]);
                    context.PopClip();
                    context.PopClip();
                }

                context.PopClip();
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, "batched rows");
                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareTargets))]
        public void Only_Fills_That_Draw_Nothing_Leave_The_Pending_Runs_Alone(GpuBackend backend, bool subpixel)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            var rows = CreateRows(typeface, 4);
            var faint = new ImmutableSolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            var hidden = new ImmutableSolidColorBrush(Colors.Black, 0);

            // A transparent fill with a stroke, an almost transparent fill and a fill of a brush
            // at zero opacity over each row: only the last one draws nothing.
            void Draw(DrawingContextImpl context)
            {
                context.Clear(s_background);
                context.DrawGlyphRun(Brushes.Black, rows[0]);
                context.DrawRectangle(Brushes.Transparent, new ImmutablePen(Brushes.Red, 2),
                    new RoundedRect(RowRect(0).Deflate(4)));
                context.DrawGlyphRun(Brushes.Black, rows[1]);
                context.DrawRectangle(faint, null, new RoundedRect(RowRect(1)));
                context.DrawGlyphRun(Brushes.Black, rows[2]);
                context.DrawRectangle(hidden, null, new RoundedRect(RowRect(2)));
                context.DrawGlyphRun(Brushes.Black, rows[3]);
            }

            try
            {
                var expected = Render(gpu, Draw, batched: false, subpixel, out _);

                Render(gpu, Draw, batched: true, subpixel, out _);

                var before = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation);
                var actual = Render(gpu, Draw, batched: true, subpixel, out var draws);
                var flushed = DrawingContextImpl.GetBatchesFlushedOnThread(GlyphBatchFlushReason.CanvasOperation) -
                              before;

                TransformedAtlasTests.AssertEqual(expected, actual, "batched frame");
                Assert.Equal(2, flushed);
                Assert.Equal(3, draws);
            }
            finally
            {
                DisposeAll(rows);
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void Batched_Runs_Are_Counted_By_The_Kind_Of_Their_Innermost_Clip(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

            // Every run lies well inside the band from 0 to 100 and crosses x = 60.
            var runs = Enumerable.Range(0, 7)
                .Select(_ => WideRunMaskTests.CreateRun(typeface, "Clipped words", 14, new Point(20, 40)))
                .ToArray();

            void Draw(DrawingContextImpl context)
            {
                context.DrawGlyphRun(Brushes.Black, runs[0]);

                context.PushClip(new Rect(0, 0, Width, 100));
                context.DrawGlyphRun(Brushes.Black, runs[1]);
                context.PopClip();

                context.PushClip(new Rect(0, 0, 60, 100));
                context.DrawGlyphRun(Brushes.Black, runs[2]);
                context.PopClip();

                context.PushClip(new Rect(0.5, 0.5, Width - 1, 99));
                context.DrawGlyphRun(Brushes.Black, runs[3]);
                context.PopClip();

                context.PushClip(new RoundedRect(new Rect(0, 0, Width, 100), 4));
                context.DrawGlyphRun(Brushes.Black, runs[4]);
                context.PopClip();

                // A clip pushed under a rotation, with the run drawn upright inside it.
                context.Transform = Matrix.CreateRotation(Math.PI / 6);
                context.PushClip(new Rect(-1000, -1000, 3000, 3000));
                context.Transform = Matrix.Identity;
                context.DrawGlyphRun(Brushes.Black, runs[5]);
                context.PopClip();

                // Popping the clip restores the rotation it was pushed under.
                context.Transform = Matrix.Identity;

                // Nested inside a clip it crosses, so it is not inside every clip.
                context.PushClip(new Rect(0, 0, 60, 100));
                context.PushClip(new Rect(0, 0, Width, 100));
                context.DrawGlyphRun(Brushes.Black, runs[6]);
                context.PopClip();
                context.PopClip();
            }

            var kinds = Enum.GetValues<GlyphRunClipKind>();

            (long[] Inside, long[] Crossing, long Everywhere) Read() => (
                kinds.Select(k => DrawingContextImpl.GetClippedRunsOnThread(k, inside: true)).ToArray(),
                kinds.Select(k => DrawingContextImpl.GetClippedRunsOnThread(k, inside: false)).ToArray(),
                DrawingContextImpl.RunsInsideEveryClipOnThread);

            try
            {
                Render(gpu, Draw);

                var before = Read();

                Render(gpu, Draw);

                var after = Read();
                var counted = new Dictionary<(GlyphRunClipKind, bool), long>();

                for (var i = 0; i < kinds.Length; i++)
                {
                    if (after.Inside[i] != before.Inside[i])
                    {
                        counted[(kinds[i], true)] = after.Inside[i] - before.Inside[i];
                    }

                    if (after.Crossing[i] != before.Crossing[i])
                    {
                        counted[(kinds[i], false)] = after.Crossing[i] - before.Crossing[i];
                    }
                }

                Assert.Equal(Describe(new Dictionary<(GlyphRunClipKind, bool), long>
                {
                    [(GlyphRunClipKind.None, true)] = 1,
                    [(GlyphRunClipKind.PixelAlignedRect, true)] = 2,
                    [(GlyphRunClipKind.PixelAlignedRect, false)] = 1,
                    [(GlyphRunClipKind.FractionalRect, true)] = 1,
                    [(GlyphRunClipKind.RoundedRectOrGeometry, true)] = 1,
                    [(GlyphRunClipKind.Transformed, true)] = 1,
                }), Describe(counted));
                Assert.Equal(5, after.Everywhere - before.Everywhere);
            }
            finally
            {
                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }

        private static string Describe(Dictionary<(GlyphRunClipKind Kind, bool Inside), long> counts) =>
            string.Join(", ", counts.OrderBy(c => c.Key.Kind).ThenBy(c => c.Key.Inside)
                .Select(c => $"{c.Key.Kind} {(c.Key.Inside ? "inside" : "crossing")}: {c.Value}"));

        /// <summary>The band of row <paramref name="index"/>, which holds its run.</summary>
        internal static Rect RowRect(int index) => new(0, index * RowHeight, Width, RowHeight);

        /// <summary>One line of text per row, each inside its row's band.</summary>
        internal static ManagedGlyphRunImpl[] CreateRows(GlyphTypeface typeface, int count)
        {
            var rows = new ManagedGlyphRunImpl[count];

            for (var i = 0; i < count; i++)
            {
                rows[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], 13,
                    new Point(10.3, i * RowHeight + 19));
            }

            return rows;
        }

        internal static void DisposeAll(ManagedGlyphRunImpl[] runs)
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }

        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw) =>
            Render(gpu, draw, batched: true, subpixel: false, out _);

        /// <summary>
        /// Renders a frame in one drawing session on an untouched GPU surface and reads it back,
        /// reporting the atlas draws the session issued. A subpixel frame draws on a display-bound
        /// surface with horizontal RGB stripes, from the subpixel run atlas.
        /// </summary>
        internal static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw, bool batched, bool subpixel,
            out int atlasDraws)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = subpixel
                ? SKSurface.Create(gpu.GrContext, true, info, 0, GRSurfaceOrigin.TopLeft,
                    new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal), false)
                : SKSurface.Create(gpu.GrContext, true, info);

            Assert.SkipWhen(surface is null, "GPU surface creation failed.");

            var before = DrawingContextImpl.AtlasDrawsOnThread;

            using (var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
                   {
                       Surface = surface,
                       GrContext = gpu.GrContext,
                       Dpi = new Vector(96, 96),
                       SurfaceIsDisplay = subpixel,
                   }))
            {
                context.BatchesGlyphAtlasDraws = batched;
                surface.Canvas.Clear(SKColors.Transparent);
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
