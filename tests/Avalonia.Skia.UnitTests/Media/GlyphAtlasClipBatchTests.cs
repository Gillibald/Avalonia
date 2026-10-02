using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Glyph runs batched on a GPU context under clips: the clips they are drawn under are
    /// counted by kind and by whether the runs lie inside them.
    /// </summary>
    public class GlyphAtlasClipBatchTests
    {
        private const int Width = 520;
        private const int Height = 360;

        public static IEnumerable<object[]> HardwareContexts()
        {
            yield return new object[] { GpuBackend.NativeGl };
            yield return new object[] { GpuBackend.Angle };
            yield return new object[] { GpuBackend.Metal };
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

        /// <summary>Renders a frame in one batched drawing session on an untouched GPU surface and reads it back.</summary>
        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
            {
                surface!.Canvas.Clear(SKColors.Transparent);
                draw(context);
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
