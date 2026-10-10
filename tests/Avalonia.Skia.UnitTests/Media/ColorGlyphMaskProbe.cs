using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Diagnostic probe (not a gate): renders rows of Segoe UI Emoji at several sizes as vectors
    /// and from colour masks, on a raster surface and on an ANGLE surface where one is available,
    /// and writes vectors.png, masks.png and diff.png (absolute difference times 16) per surface
    /// plus stats.txt, for a visual review of the colour masks. Enabled by setting
    /// COLOR_MASK_PROBE_DIR to the output directory.
    /// </summary>
    public class ColorGlyphMaskProbe
    {
        private const int Width = 2000;

        private static readonly double[] s_sizes = { 14, 20, 28, 48, 96 };

        private static readonly int[] s_codepoints =
        {
            0x1F600, 0x1F602, 0x1F60D, 0x1F914, 0x1F525, 0x2764, 0x1F44D, 0x1F389, 0x1F308, 0x1F98A,
            0x1F680, 0x1F355, 0x1F30D, 0x1F4A1, 0x1F3B8, 0x1F431, 0x1F33B, 0x1F4F7,
        };

        [Fact]
        public void Render_Vector_And_Mask_Sheets()
        {
            var directory = Environment.GetEnvironmentVariable("COLOR_MASK_PROBE_DIR");

            Assert.SkipWhen(string.IsNullOrEmpty(directory), "Probe disabled.");

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");

            Assert.SkipUnless(File.Exists(path), "Segoe UI Emoji is not installed.");

            Directory.CreateDirectory(directory!);

            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            var typeface = TestGlyphTypefaces.FromSKTypeface(SKTypeface.FromFile(path));
            var glyphs = s_codepoints.Select(c => typeface.CharacterToGlyphMap[c]).ToArray();
            var height = (int)s_sizes.Sum(s => Math.Ceiling(s * 1.5)) + 16;
            var report = new StringBuilder();

            void Sheet(string name, Func<Action<DrawingContext>, byte[]> render)
            {
                byte[] Draw(bool masks)
                {
                    var previous = ColorGlyphRunSplitter.UseColorMasks;
                    ColorGlyphRunSplitter.UseColorMasks = masks;

                    try
                    {
                        return render(context => DrawRows(context, typeface, glyphs));
                    }
                    finally
                    {
                        ColorGlyphRunSplitter.UseColorMasks = previous;
                    }
                }

                var vectors = Draw(false);
                var masked = Draw(true);
                var diff = new byte[vectors.Length];
                var max = 0;
                long sum = 0;
                var differing = 0;

                for (var i = 0; i < vectors.Length; i += 4)
                {
                    var pixelMax = 0;

                    for (var c = 0; c < 3; c++)
                    {
                        var d = Math.Abs(vectors[i + c] - masked[i + c]);

                        pixelMax = Math.Max(pixelMax, d);
                        sum += d;
                        diff[i + c] = (byte)(255 - Math.Min(255, d * 16));
                    }

                    diff[i + 3] = 255;
                    max = Math.Max(max, pixelMax);
                    differing += pixelMax > 0 ? 1 : 0;
                }

                Save(Path.Combine(directory!, $"{name}-vectors.png"), vectors, height);
                Save(Path.Combine(directory!, $"{name}-masks.png"), masked, height);
                Save(Path.Combine(directory!, $"{name}-diff.png"), diff, height);

                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: max {1} levels, {2} pixels differ, mean {3:F4} levels per channel over the sheet",
                    name, max, differing, sum / (vectors.Length / 4.0 * 3)));
            }

            Sheet("raster", draw => RenderOnRaster(height, draw));

            using (var gpu = GpuTestContext.TryCreate(GpuBackend.Angle, out var reason))
            {
                if (gpu is null)
                {
                    report.AppendLine("angle: skipped, " + reason);
                }
                else
                {
                    Sheet("angle", draw => RenderOnGpu(gpu, height, draw));
                }
            }

            File.WriteAllText(Path.Combine(directory!, "stats.txt"), report.ToString());
        }

        // One row per size, the glyphs at their font advances from a whole-pixel baseline, so the
        // pens fall on every quarter-pixel phase.
        private static void DrawRows(DrawingContext context, GlyphTypeface typeface, ushort[] glyphs)
        {
            var brush = new ImmutableSolidColorBrush(Colors.Black);
            var y = 8.0;

            foreach (var size in s_sizes)
            {
                y += Math.Ceiling(size * 1.2);

                var scale = size / typeface.Metrics.DesignEmHeight;
                var infos = new List<GlyphInfo>();

                for (var i = 0; i < glyphs.Length; i++)
                {
                    typeface.TryGetGlyphMetrics(glyphs[i], out var metrics);
                    infos.Add(new GlyphInfo(glyphs[i], i, metrics.AdvanceWidth * scale + size * 0.07));
                }

                using var run = new GlyphRun(typeface, size, default, infos, new Point(8, y));

                context.DrawGlyphRun(brush, run);

                y += Math.Ceiling(size * 0.3);
            }
        }

        private static void Save(string path, byte[] bgra, int height)
        {
            var info = new SKImageInfo(Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);

            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);

            data.SaveTo(stream);
        }

        private static byte[] RenderOnRaster(int height, Action<DrawingContext> draw)
        {
            var info = new SKImageInfo(Width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                draw(context);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderOnGpu(GpuTestContext gpu, int height, Action<DrawingContext> draw)
        {
            var info = new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                draw(context);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[readInfo.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface!.ReadPixels(readInfo, (IntPtr)p, readInfo.RowBytes, 0, 0));
                }
            }

            return pixels;
        }
    }
}
