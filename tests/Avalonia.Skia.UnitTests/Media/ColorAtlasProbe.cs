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
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Diagnostic probe (not a gate): renders rows of Segoe UI Emoji at several sizes on GPU
    /// contexts, upright and under static transforms, with the colour atlas off (run masks
    /// upright, vector replay transformed) and on, and writes before.png, after.png and diff.png
    /// (absolute difference times 16) per context and transform plus stats.txt, for a visual
    /// review of the colour atlas. Enabled by setting COLOR_ATLAS_PROBE_DIR to the output
    /// directory.
    /// </summary>
    public class ColorAtlasProbe
    {
        private const int Width = 2200;
        private const int Height = 1500;

        private static readonly double[] s_sizes = { 14, 20, 28, 48, 96 };

        private static readonly int[] s_codepoints =
        {
            0x1F600, 0x1F602, 0x1F60D, 0x1F914, 0x1F525, 0x2764, 0x1F44D, 0x1F389, 0x1F308, 0x1F98A,
            0x1F680, 0x1F355, 0x1F30D, 0x1F4A1, 0x1F3B8, 0x1F431, 0x1F33B, 0x1F4F7,
        };

        private static readonly (string Name, Matrix Linear)[] s_transforms =
        {
            ("upright", Matrix.Identity),
            ("rotate15", Matrix.CreateRotation(15 * Math.PI / 180)),
            ("rotate-30", Matrix.CreateRotation(-30 * Math.PI / 180)),
            ("skew", new Matrix(1, 0, -0.3, 1, 0, 0)),
            ("scale-1.4x0.8", Matrix.CreateScale(1.4, 0.8)),
        };

        [Fact]
        public void Render_Before_And_After_Sheets()
        {
            var directory = Environment.GetEnvironmentVariable("COLOR_ATLAS_PROBE_DIR");

            Assert.SkipWhen(string.IsNullOrEmpty(directory), "Probe disabled.");

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");

            Assert.SkipUnless(File.Exists(path), "Segoe UI Emoji is not installed.");

            Directory.CreateDirectory(directory!);

            using var scope = ColorAtlasTests.CreateEnvironment();
            var typeface = TestGlyphTypefaces.FromSKTypeface(SKTypeface.FromFile(path));
            var glyphs = s_codepoints.Select(c => typeface.CharacterToGlyphMap[c]).ToArray();
            var report = new StringBuilder();

            foreach (var backend in new[] { GpuBackend.Angle, GpuBackend.NativeGl, GpuBackend.Vulkan })
            {
                using var gpu = TransformedAtlasTests.CreateGpu(backend, false);

                foreach (var (name, linear) in s_transforms)
                {
                    var transform = Matrix.CreateTranslation(-1000, -330) * linear *
                                    Matrix.CreateTranslation(Width / 2.0, Height / 2.0);
                    byte[] before;

                    using (ColorAtlasTests.SwitchColorAtlasOff())
                    {
                        before = Render(gpu, transform, typeface, glyphs);
                    }

                    var after = Render(gpu, transform, typeface, glyphs);
                    var label = $"{backend.ToString().ToLowerInvariant()}-{name}";

                    report.AppendLine(Compare(directory!, label, before, after));
                }
            }

            File.WriteAllText(Path.Combine(directory!, "stats.txt"), report.ToString());
        }

        private static string Compare(string directory, string label, byte[] before, byte[] after)
        {
            var diff = new byte[before.Length];
            var max = 0;
            long sum = 0;
            var differing = 0;
            var inked = 0;

            for (var i = 0; i < before.Length; i += 4)
            {
                var pixelMax = 0;

                for (var c = 0; c < 3; c++)
                {
                    var d = Math.Abs(before[i + c] - after[i + c]);

                    pixelMax = Math.Max(pixelMax, d);
                    sum += d;
                    diff[i + c] = (byte)(255 - Math.Min(255, d * 16));
                }

                diff[i + 3] = 255;
                max = Math.Max(max, pixelMax);
                differing += pixelMax > 0 ? 1 : 0;
                inked += before[i] != 255 || before[i + 1] != 255 || before[i + 2] != 255 ? 1 : 0;
            }

            Save(Path.Combine(directory, $"{label}-before.png"), before);
            Save(Path.Combine(directory, $"{label}-after.png"), after);
            Save(Path.Combine(directory, $"{label}-diff.png"), diff);

            return string.Format(CultureInfo.InvariantCulture,
                "{0}: max {1} levels, {2} of {3} inked pixels differ, mean {4:F4} levels per channel over the inked pixels",
                label, max, differing, inked, inked == 0 ? 0 : sum / (inked * 3.0));
        }

        // One row per size, the glyphs at their font advances, so the pens fall on every phase.
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

        private static void Save(string path, byte[] bgra)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);

            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);

            data.SaveTo(stream);
        }

        private static byte[] Render(GpuTestContext gpu, Matrix transform, GlyphTypeface typeface, ushort[] glyphs)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                impl.Transform = transform;
                DrawRows(context, typeface, glyphs);
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
