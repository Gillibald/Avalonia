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
    /// A hardware GPU context draws the subpixel runs of a frame with one call per page and
    /// colour through the per-channel blender.
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
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), out _);
                Render(gpu, context => DrawAll(context, runs, Brushes.Black), out var draws);

                Assert.Equal(1, draws);
            }
            finally
            {
                DisposeAll(runs);
            }
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
        private static byte[] Render(GpuTestContext gpu, Action<DrawingContextImpl> draw, out int atlasDraws)
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
