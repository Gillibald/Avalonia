using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Rotated cells, each clipped to its own bounds, of transformed glyph runs on a GPU context:
    /// a grid of rotated labels or emoji, as a layout of rotated items draws it. A run that keeps
    /// clear of its rotated clip's edges stays pending across the clip's push and pop, so the
    /// grid draws in a handful of atlas calls rather than one per cell, with exactly the pixels of
    /// drawing every run on its own under its clip.
    /// </summary>
    public class GlyphAtlasRotatedClipBatchTests
    {
        private const int Columns = 8;
        private const int Rows = 6;
        private const double CellWidth = 60;
        private const double CellHeight = 56;

        private static readonly IBrush s_text = new ImmutableSolidColorBrush(Color.FromRgb(0x20, 0x30, 0x60));

        public enum Content
        {
            /// <summary>A short Latin label per cell: A8 masks.</summary>
            Latin,

            /// <summary>One CBDT strike glyph per cell: colour masks.</summary>
            Strike,
        }

        public enum ClipShape
        {
            Rect,
            RoundedRectWithoutRadii,
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (var backend in new[] { GpuBackend.Metal, GpuBackend.NativeGl, GpuBackend.Angle, GpuBackend.Vulkan })
            {
                foreach (var content in Enum.GetValues<Content>())
                {
                    foreach (var clip in Enum.GetValues<ClipShape>())
                    {
                        yield return new object[] { backend, content, clip };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Rotated_Cells_Clipped_To_Their_Bounds_Draw_In_A_Few_Atlas_Calls_With_The_Pixels_Of_Each_Drawn_Alone(
            GpuBackend backend, Content content, ClipShape clip)
        {
            using var gpu = ColorAtlasTests.CreateGpu(backend, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var runs = CreateCells(content, out var owners);

            void Draw(DrawingContextImpl context)
            {
                context.Clear(Colors.White);

                for (var i = 0; i < runs.Length; i++)
                {
                    context.Transform = CellTransform(i);
                    PushClip(context, clip, new Rect(0, 0, CellWidth, CellHeight));
                    context.DrawGlyphRun(s_text, runs[i]);
                    context.PopClip();
                }

                context.Transform = Matrix.Identity;
            }

            try
            {
                var expected = GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: false, subpixel: false, out var alone);

                // The first frame places the masks; the second draws them as a steady frame does.
                GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: true, subpixel: false, out _);

                var actual = GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: true, subpixel: false, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, $"{content} cells under {clip} clips");
                Assert.True(alone >= runs.Length, $"the reference drew {alone} atlas calls for {runs.Length} cells");
                Assert.True(draws <= 4, $"{runs.Length} rotated cells drew {draws} atlas calls");
            }
            finally
            {
                GlyphAtlasClipBatchTests.DisposeAll(runs);
                GC.KeepAlive(owners);
            }
        }

        /// <summary>
        /// Cells clipped tightly around their content, as a text block clips to its bounds: the
        /// clip is the run's ink grown by four DIPs, so under the turn the corners of the run's
        /// device bounds reach past the clip's edges while its ink keeps clear of them. Such runs
        /// stay pending too, measured in the clip's own frame.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void Rotated_Cells_Whose_Ink_Keeps_Clear_Of_A_Tight_Clip_Draw_In_A_Few_Atlas_Calls(GpuBackend backend,
            Content content, ClipShape clip)
        {
            using var gpu = ColorAtlasTests.CreateGpu(backend, true);
            using var scope = ColorAtlasTests.CreateEnvironment();
            var runs = CreateCells(content, out var owners, lastCrossesItsClip: false);

            void Draw(DrawingContextImpl context)
            {
                context.Clear(Colors.White);

                for (var i = 0; i < runs.Length; i++)
                {
                    context.Transform = CellTransform(i, degrees: 15);
                    PushClip(context, clip, InkOf(runs[i], content).Inflate(4));
                    context.DrawGlyphRun(s_text, runs[i]);
                    context.PopClip();
                }

                context.Transform = Matrix.Identity;
            }

            try
            {
                var expected = GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: false, subpixel: false, out var alone);

                GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: true, subpixel: false, out _);

                var actual = GlyphAtlasClipBatchTests.Render(gpu, Draw, batched: true, subpixel: false, out var draws);

                TransformedAtlasTests.AssertEqual(expected, actual, $"{content} cells under tight {clip} clips");
                Assert.True(alone >= runs.Length, $"the reference drew {alone} atlas calls for {runs.Length} cells");
                Assert.True(draws <= 4, $"{runs.Length} rotated cells drew {draws} atlas calls");
            }
            finally
            {
                GlyphAtlasClipBatchTests.DisposeAll(runs);
                GC.KeepAlive(owners);
            }
        }

        /// <summary>
        /// The box a cell's run inks: its outline bounds, and for a strike glyph its image too, a
        /// full-em square standing on the em line at the pen.
        /// </summary>
        private static Rect InkOf(ManagedGlyphRunImpl run, Content content)
        {
            if (content == Content.Latin)
            {
                return run.Bounds;
            }

            var em = run.FontRenderingEmSize;
            var pen = run.BaselineOrigin;

            return run.Bounds.Union(new Rect(pen.X, pen.Y - em, em, em));
        }

        /// <summary>
        /// Cell <paramref name="index"/>: a turn of 15 degrees about its centre, at its place in
        /// the grid. The last cell turns its run out of its clip, so the clip cuts it.
        /// </summary>
        private static Matrix CellTransform(int index, double? degrees = null)
        {
            var column = index % Columns;
            var row = index / Columns;

            degrees ??= index == Columns * Rows - 1 ? 40 : 15;

            return Matrix.CreateTranslation(-CellWidth / 2, -CellHeight / 2) *
                   Matrix.CreateRotation(degrees.Value * Math.PI / 180) *
                   Matrix.CreateTranslation(CellWidth / 2 + column * CellWidth + 2.3, CellHeight / 2 + row * CellHeight + 4.6);
        }

        private static void PushClip(DrawingContextImpl context, ClipShape shape, Rect rect)
        {
            if (shape == ClipShape.Rect)
            {
                context.PushClip(rect);
            }
            else
            {
                context.PushClip(new RoundedRect(rect));
            }
        }

        /// <summary>
        /// The cells' runs; <paramref name="owners"/> holds the glyph runs that own strike runs, which
        /// must outlive the draws.
        /// </summary>
        private static ManagedGlyphRunImpl[] CreateCells(Content content, out List<GlyphRun> owners,
            bool lastCrossesItsClip = true)
        {
            var runs = new ManagedGlyphRunImpl[Columns * Rows];

            owners = new List<GlyphRun>();

            if (content == Content.Latin)
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);

                for (var i = 0; i < runs.Length; i++)
                {
                    // The last cell's label runs past its right edge, which the clip cuts.
                    var text = lastCrossesItsClip && i == runs.Length - 1 ? "Clipped!" : "Ag" + i;

                    runs[i] = WideRunMaskTests.CreateRun(typeface, text, 15, new Point(14.3, 34.6));
                }

                return runs;
            }

            var strikes = BackendBitmapStrikeTests.CreateTwoStrikeTypeface(out var glyph);

            for (var i = 0; i < runs.Length; i++)
            {
                // The last cell's glyph sits across its right edge.
                var origin = lastCrossesItsClip && i == runs.Length - 1 ? new Point(44, 38) : new Point(18, 38);
                var run = new GlyphRun(strikes, 22, default, new List<GlyphInfo> { new(glyph, 0, 26) }, origin);

                owners.Add(run);
                runs[i] = (ManagedGlyphRunImpl)run.PlatformImpl.Item;
            }

            return runs;
        }
    }
}
