using System;
using System.Collections.Generic;
using Avalonia.Media;
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
