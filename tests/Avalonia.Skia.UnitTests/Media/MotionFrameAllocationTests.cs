using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The managed memory a frame of moving text costs on a GPU context. The compositor makes a
    /// drawing context per frame, so a zoom's frames are measured that way: a frame that only
    /// stretches cached sprites allocates nothing beyond what making and disposing an empty
    /// context does.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class MotionFrameAllocationTests
    {
        private const int Width = 640;
        private const int Height = 480;

        private static readonly string[] s_lines =
        {
            "Typography is the craft of endowing human language",
            "with a durable visual form. The quick brown fox jumps",
            "over the lazy dog while five boxing wizards jump quickly.",
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
        public void Stretched_Zoom_Frames_Under_Nested_Clips_Allocate_Nothing_Beyond_An_Empty_Frame(GpuBackend backend)
        {
            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var runs = new ManagedGlyphRunImpl[6];

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], 13,
                    new Point(6.3, 30 + i * 18));
            }

            var budget = MotionRasterBudget.BudgetOverride;

            // No frame affords rasterizing, so every moving frame stretches the cached sprites.
            MotionRasterBudget.BudgetOverride = 0;

            void Frame(double scale)
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);

                // Cards within a list, as a dashboard nests them: more levels than the clip
                // stack starts with, a rounded clip that a rectangle drawn under it applies to
                // the canvas, and the text inside all of them.
                for (var depth = 0; depth < 12; depth++)
                {
                    context.PushClip(new Rect(depth, depth, Width - 2 * depth, Height - 2 * depth));
                }

                context.PushClip(new RoundedRect(new Rect(14, 14, Width - 28, Height - 28), 6));
                context.DrawRectangle(Brushes.LightGray, null, new RoundedRect(new Rect(20, 20, 40, 10)));
                context.Transform = Matrix.CreateScale(scale, scale);

                foreach (var run in runs)
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }

                context.Transform = Matrix.Identity;
                context.PopClip();

                for (var depth = 0; depth < 12; depth++)
                {
                    context.PopClip();
                }
            }

            void Empty()
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            }

            try
            {
                // A gesture zooming in and out between two band crossings, 1 and 1.2: the first
                // passes lay out the crossing, grow every buffer and settle every batch; later
                // frames only stretch what they cached.
                static double Scale(int step) => 1.02 + step * 0.01;

                for (var pass = 0; pass < 3; pass++)
                {
                    for (var step = 0; step < 17; step++)
                    {
                        Frame(Scale(step));
                        Empty();
                    }
                }

                long frames = 0;
                long empty = 0;

                for (var pass = 0; pass < 2; pass++)
                {
                    for (var step = 0; step < 17; step++)
                    {
                        var before = ThreadAllocations.Start();

                        Frame(Scale(step));
                        frames += ThreadAllocations.Since(before);

                        before = ThreadAllocations.Start();

                        Empty();
                        empty += ThreadAllocations.Since(before);
                    }
                }

                Assert.True(frames <= empty,
                    $"34 stretched zoom frames allocated {frames} bytes, 34 empty frames {empty} bytes");
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;

                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }

        [Theory]
        [MemberData(nameof(HardwareContexts))]
        public void A_Crossing_Laid_Out_Again_Allocates_Only_What_It_Keeps(GpuBackend backend)
        {
            // A zoom that comes back to a crossing whose sprite set it dropped lays the set out
            // again from the atlas. That frame keeps a laid-out sprite (32 bytes) and the batch's
            // source rectangle and placement (32 bytes) per glyph, plus a few objects per run;
            // everything else it allocated would be garbage, of which the zoom makes a lot.
            const long keptPerGlyph = 32 + 32 + 32;

            using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = SKSurface.Create(gpu.GrContext, true,
                new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var runs = new ManagedGlyphRunImpl[6];
            var glyphs = 0;

            for (var i = 0; i < runs.Length; i++)
            {
                runs[i] = WideRunMaskTests.CreateRun(typeface, s_lines[i % s_lines.Length], 13,
                    new Point(6.3, 30 + i * 18));
                glyphs += runs[i].GlyphCount;
            }

            var budget = MotionRasterBudget.BudgetOverride;

            MotionRasterBudget.BudgetOverride = 0;

            void Frame(double scale)
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);

                context.Transform = Matrix.CreateScale(scale, scale);

                foreach (var run in runs)
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }
            }

            void Empty()
            {
                using var context = TransformedAtlasTests.CreateContext(gpu, surface);
            }

            try
            {
                // Two passes from 1 to 5 and back down: every crossing is laid out once and laid
                // out again after the zoom dropped it, so every buffer has grown before the
                // measured pass.
                var top = MaskGlyphRunRenderer.CrossingScale(5, 1);
                var revisit = top / Math.Pow(MaskGlyphRunRenderer.MaxStretchScale, 6) * 0.99;

                for (var pass = 0; pass < 3; pass++)
                {
                    for (var step = 0; step <= 400; step++)
                    {
                        Frame(1 + 0.01 * step);
                    }

                    var builds = GlyphRasterDiagnostics.SpriteSetBuildsOnThread;
                    var rasterized = GlyphRasterDiagnostics.GlyphRasterizationsOnThread;
                    var before = ThreadAllocations.Start();

                    Frame(revisit);

                    var allocated = ThreadAllocations.Since(before);

                    before = ThreadAllocations.Start();
                    Empty();
                    allocated -= ThreadAllocations.Since(before);

                    Assert.Equal(runs.Length, GlyphRasterDiagnostics.SpriteSetBuildsOnThread - builds);
                    Assert.Equal(0, GlyphRasterDiagnostics.GlyphRasterizationsOnThread - rasterized);

                    if (pass == 2)
                    {
                        Assert.True(allocated <= glyphs * keptPerGlyph,
                            $"laying out {glyphs} glyphs again allocated {allocated} bytes, {allocated / (double)glyphs:F1} per glyph");
                    }
                }
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;

                foreach (var run in runs)
                {
                    run.Dispose();
                }
            }
        }
    }
}
