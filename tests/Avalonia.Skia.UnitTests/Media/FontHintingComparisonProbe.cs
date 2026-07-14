using System;
using System.IO;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: per-size ink heights of hinted DirectWrite output (the Skia
    /// blob with full hinting runs the font bytecode on Windows) against the managed grid-fit
    /// masks, plus a side-by-side strip image. Env-gated, not part of the suite.</summary>
    public class FontHintingComparisonProbe
    {
        [Fact]
        public void Measure_Zone_Rounding_Against_DirectWrite()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("FONT_HINTING_PROBE") != "1", "probe");
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");

            var report = new StringBuilder();

            foreach (var family in new[] { "Segoe UI", "Arial" })
            {
                using var skTypeface = SKFontManager.Default.MatchFamily(family, SKFontStyle.Normal);

                if (skTypeface is null)
                {
                    continue;
                }

                var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));

                report.AppendLine($"== {family} (upem {typeface.Metrics.DesignEmHeight}) ==");

                foreach (var reference in new[] { 'x', 'H', 'p' })
                {
                    Assert.True(typeface.TryGetGlyphInkBounds(typeface.CharacterToGlyphMap[reference], out var box));

                    var designExtent = reference == 'p' ? -box.YMin : box.YMax;

                    report.AppendLine($"-- '{reference}' design extent {designExtent} --");
                    report.AppendLine("size | design px | DW rows | managed rows");

                    for (var size = 9; size <= 16; size++)
                    {
                        var scale = (float)size / typeface.Metrics.DesignEmHeight;
                        var designPx = designExtent * scale;

                        var dwRows = MeasureDirectWriteRows(skTypeface, reference, size, below: reference == 'p');
                        var managedRows = MeasureManagedRows(typeface, reference, size, below: reference == 'p');

                        report.AppendLine(FormattableString.Invariant(
                            $"{size,4} | {designPx,9:0.00} | {dwRows,7} | {managedRows,12}"));
                    }
                }
            }

            if (Environment.GetEnvironmentVariable("COLOR_GLYPH_DIAG_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                DumpStrip(Path.Combine(dir, "hinting-strip.png"));
            }

            Assert.Fail(report.ToString());
        }

        /// <summary>Ink rows (coverage at least half) above (or below) the baseline as
        /// DirectWrite renders them: full hinting, grayscale, no subpixel positioning.</summary>
        private static int MeasureDirectWriteRows(SKTypeface skTypeface, char reference, int size, bool below)
        {
            const int canvas = 64;
            const int baseline = 48;

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
            skCanvas.DrawText(reference.ToString(), 8, baseline, SKTextAlign.Left, font, paint);
            skCanvas.Flush();

            return CountRows(bitmap, canvas, baseline, below);
        }

        private static int MeasureManagedRows(GlyphTypeface typeface, char reference, int size, bool below)
        {
            var glyph = typeface.CharacterToGlyphMap[reference];
            var scratch = new GlyphPathBuilder();
            var mask = GlyphMasks.Build(typeface, scratch,
                new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), 0, GlyphMaskMode.Antialiased));

            var rows = 0;

            for (var y = 0; y < mask.Height; y++)
            {
                var deviceRow = mask.Top + y;   // baseline-relative, negative above

                if (below ? deviceRow < 0 : deviceRow >= 0)
                {
                    continue;
                }

                var max = 0;

                for (var x = 0; x < mask.Width; x++)
                {
                    max = Math.Max(max, mask.Alpha[y * mask.Width + x]);
                }

                if (max >= 128)
                {
                    rows++;
                }
            }

            return rows;
        }

        private static int CountRows(SKBitmap bitmap, int canvas, int baseline, bool below)
        {
            var rows = 0;

            for (var y = 0; y < canvas; y++)
            {
                if (below ? y < baseline : y >= baseline)
                {
                    continue;
                }

                var darkest = 255;

                for (var x = 0; x < canvas; x++)
                {
                    darkest = Math.Min(darkest, bitmap.GetPixel(x, y).Red);
                }

                if (darkest <= 127)
                {
                    rows++;
                }
            }

            return rows;
        }

        private static void DumpStrip(string path)
        {
            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            if (skTypeface is null)
            {
                return;
            }

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));
            const string sample = "Hxzo Hamburgefonstiv 123";
            var sizes = new[] { 9, 10, 11, 12, 13, 14, 16 };

            var info = new SKImageInfo(560, sizes.Length * 44 + 8, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            var y = 8;

            foreach (var size in sizes)
            {
                // Left: DirectWrite (full hinting). Right: the managed mask path.
                using (var font = new SKFont(skTypeface, size)
                       {
                           Hinting = SKFontHinting.Full,
                           Subpixel = false,
                           Edging = SKFontEdging.Antialias,
                       })
                using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
                {
                    canvas.DrawText($"{size}px {sample}", 4, y + size + 4, SKTextAlign.Left, font, paint);
                }

                DrawManaged(canvas, typeface, $"{size}px {sample}", 260, y + size + 4, size);

                y += 44;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);

            data.SaveTo(file);
        }

        private static void DrawManaged(SKCanvas canvas, GlyphTypeface typeface, string text,
            int penX, int baselineY, float size)
        {
            var scratch = new GlyphPathBuilder();
            var scale = size / typeface.Metrics.DesignEmHeight;
            var x = (float)penX;

            foreach (var c in text)
            {
                if (!typeface.CharacterToGlyphMap.ContainsGlyph(c))
                {
                    continue;
                }

                var glyph = typeface.CharacterToGlyphMap[c];
                var mask = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, GlyphMaskKey.QuantizeScale(size), 0, GlyphMaskMode.Antialiased));

                if (!mask.IsEmpty)
                {
                    for (var my = 0; my < mask.Height; my++)
                    {
                        for (var mx = 0; mx < mask.Width; mx++)
                        {
                            var coverage = mask.Alpha[my * mask.Width + mx];

                            if (coverage == 0)
                            {
                                continue;
                            }

                            var px = (int)MathF.Round(x) + mask.Left + mx;
                            var py = baselineY + mask.Top + my;
                            var value = (byte)(255 - coverage);

                            canvas.DrawPoint(px, py, new SKColor(value, value, value));
                        }
                    }
                }

                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                x += metrics.AdvanceWidth * scale;
            }
        }
    }
}
