using System;
using System.IO;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: per-glyph shape comparison of hinted DirectWrite output vs
    /// managed grid-fit masks across instructed (Segoe UI, Arial) and ttfautohint (Inter)
    /// fonts. Dumps side-by-side and overlay strips plus baseline-relative row signatures,
    /// so small-size shape divergences can be named and classified (our policy vs the
    /// font's bytecode). Env-gated, not part of the suite.</summary>
    public class GlyphShapeDiagProbe
    {
        private const string Glyphs = "aegswx8SBf49ltH";

        [Fact]
        public void Dump_Shape_Comparison()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("GLYPH_SHAPE_DIAG_DIR") is not { Length: > 0 }, "probe");

            var directory = Environment.GetEnvironmentVariable("GLYPH_SHAPE_DIAG_DIR")!;

            Directory.CreateDirectory(directory);

            var report = new StringBuilder();

            foreach (var family in new[] { "Segoe UI", "Arial" })
            {
                using var skTypeface = SKFontManager.Default.MatchFamily(family, SKFontStyle.Normal);

                if (skTypeface is null)
                {
                    continue;
                }

                DumpFamily(directory, skTypeface, family.Replace(" ", ""), report);
            }

            using (var inter = LoadInter())
            {
                DumpFamily(directory, inter, "Inter", report);
            }

            File.WriteAllText(Path.Combine(directory, "shape-report.txt"), report.ToString());
            Assert.Fail(report.ToString());
        }

        private static void DumpFamily(string directory, SKTypeface skTypeface, string label,
            StringBuilder report)
        {
            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));

            report.AppendLine($"#### {label} (upem {typeface.Metrics.DesignEmHeight}) ####");

            foreach (var reference in "xHlbfto8p")
            {
                if (!typeface.CharacterToGlyphMap.ContainsGlyph(reference))
                {
                    continue;
                }

                typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap[reference], out var box);
                report.AppendLine(FormattableString.Invariant($"ink '{reference}': YMax {box.YMax} YMin {box.YMin}"));
            }

            foreach (var size in new[] { 9, 10, 11, 12, 14, 16 })
            {
                DumpSize(directory, skTypeface, typeface, size, report, label);
            }
        }

        private static SKTypeface LoadInter()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "tests")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "Avalonia.RenderTests", "Assets", "Inter-Regular.ttf"));
            var skTypeface = SKTypeface.FromData(SKData.CreateCopy(bytes));

            Assert.NotNull(skTypeface);

            return skTypeface!;
        }

        private static void DumpSize(string directory, SKTypeface skTypeface, GlyphTypeface typeface,
            int size, StringBuilder report, string label)
        {
            const int cell = 26;          // design cell in device px around each rendering
            const int zoom = 8;
            var columns = Glyphs.Length;

            // Three bands per size: DirectWrite, managed, overlay (DW red / managed blue).
            var info = new SKImageInfo(columns * cell * zoom, 3 * cell * zoom + 24,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);
            report.AppendLine($"== {size}px ==");

            for (var i = 0; i < Glyphs.Length; i++)
            {
                var c = Glyphs[i];
                var dw = RenderDirectWrite(skTypeface, c, size, out var dwBaseline, out var dwLeft);
                var managed = RenderManaged(typeface, c, size, out var mBaseline, out var mLeft);

                // Row signatures: baseline-relative rows that contain ink at half coverage.
                report.AppendLine(FormattableString.Invariant(
                    $"'{c}': DW rows {Signature(dw, dwBaseline)} | managed rows {Signature(managed, mBaseline)}"));

                DrawCell(canvas, dw, i * cell, 0, zoom, cell, dwBaseline, SKColors.Black);
                DrawCell(canvas, managed, i * cell, cell, zoom, cell, mBaseline, SKColors.Black);

                // Overlay aligned on baseline and left ink edge.
                DrawCell(canvas, dw, i * cell, 2 * cell, zoom, cell, dwBaseline, new SKColor(220, 0, 0), dwLeft);
                DrawCell(canvas, managed, i * cell, 2 * cell, zoom, cell, mBaseline, new SKColor(0, 0, 220, 160), mLeft);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(directory, $"shapes-{label}-{size}px.png"));

            data.SaveTo(file);
        }

        /// <summary>Coverage grid from the hinted DirectWrite-host blob path (grayscale,
        /// integer pen), tight-cropped, plus the baseline row index and left ink column.</summary>
        private static byte[,] RenderDirectWrite(SKTypeface skTypeface, char c, int size,
            out int baselineRow, out int leftColumn)
        {
            const int canvas = 64;
            const int baseline = 44;
            const int penX = 8;

            var info = new SKImageInfo(canvas, canvas, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var skCanvas = new SKCanvas(bitmap);
            using var font = new SKFont(skTypeface, size)
            {
                Hinting = SKFontHinting.Full,
                Subpixel = false,
                Edging = SKFontEdging.Antialias,
            };
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            skCanvas.Clear(SKColors.White);
            skCanvas.DrawText(c.ToString(), penX, baseline, SKTextAlign.Left, font, paint);
            skCanvas.Flush();

            // Tight crop.
            int minX = canvas, minY = canvas, maxX = -1, maxY = -1;

            for (var y = 0; y < canvas; y++)
            {
                for (var x = 0; x < canvas; x++)
                {
                    if (255 - bitmap.GetPixel(x, y).Red > 12)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            if (maxX < 0)
            {
                baselineRow = 0;
                leftColumn = 0;
                return new byte[0, 0];
            }

            var grid = new byte[maxY - minY + 1, maxX - minX + 1];

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    grid[y - minY, x - minX] = (byte)(255 - bitmap.GetPixel(x, y).Red);
                }
            }

            baselineRow = baseline - minY;
            leftColumn = 0;
            return grid;
        }

        private static byte[,] RenderManaged(GlyphTypeface typeface, char c, int size,
            out int baselineRow, out int leftColumn)
        {
            var glyph = typeface.CharacterToGlyphMap[c];
            var scratch = new GlyphPathBuilder();
            var mask = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), 0, GlyphMaskMode.Antialiased));

            var grid = new byte[mask.Height, mask.Width];

            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = 0; x < mask.Width; x++)
                {
                    grid[y, x] = mask.Alpha[y * mask.Width + x];
                }
            }

            baselineRow = -mask.Top;   // device row 0 = baseline
            leftColumn = 0;
            return grid;
        }

        private static string Signature(byte[,] grid, int baselineRow)
        {
            // Rows with any coverage >= 128, reported baseline-relative (negative above).
            var rows = new StringBuilder("[");
            var height = grid.GetLength(0);
            var width = grid.GetLength(1);

            for (var y = 0; y < height; y++)
            {
                var max = 0;

                for (var x = 0; x < width; x++)
                {
                    max = Math.Max(max, grid[y, x]);
                }

                if (max >= 128)
                {
                    rows.Append(y - baselineRow).Append(' ');
                }
            }

            return rows.Append(']').ToString();
        }

        private static void DrawCell(SKCanvas canvas, byte[,] grid, int cellX, int cellY,
            int zoom, int cell, int baselineRow, SKColor color, int alignLeft = -1)
        {
            var height = grid.GetLength(0);
            var width = grid.GetLength(1);

            // Anchor the baseline at 3/4 cell height; left edge at 4 px unless aligning ink.
            var originY = cellY + (cell * 3) / 4 - baselineRow;
            var originX = cellX + 4;

            using var paint = new SKPaint();

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var coverage = grid[y, x];

                    if (coverage == 0)
                    {
                        continue;
                    }

                    paint.Color = color.WithAlpha((byte)(color.Alpha * coverage / 255));
                    canvas.DrawRect((originX + x) * zoom, (originY + y) * zoom, zoom, zoom, paint);
                }
            }
        }
    }
}
