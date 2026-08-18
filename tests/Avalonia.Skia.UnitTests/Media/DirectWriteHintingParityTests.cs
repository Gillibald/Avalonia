using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A measurement probe, not a gate: legacy system fonts render through their own
    /// programs (both interpretation classes) and through the auto-hinter, all at the same
    /// integer pens as a DirectWrite-hinted reference blob built from the same font bytes.
    ///
    /// Set DW_HINTING_PARITY=1 to run; the report lands in the temp directory. Why no
    /// hard gate: the auto-hinter's rounding policy was calibrated row-by-row against
    /// DirectWrite output, so it wins this metric by construction at tuned sizes, and the
    /// v40 class follows the FreeType lineage, which documents that it does not match
    /// ClearType rendering exactly. The probe is the instrument for tuning the remaining
    /// divergences; the qualitative verdict lives in the waterfall and A/B reviews.
    /// </summary>
    public class DirectWriteHintingParityTests
    {
        private const string Sample = "Hlmnou HIE 14";

        [Fact]
        public void Measure_Bytecode_Hinting_Against_The_DirectWrite_Host()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");
            Assert.SkipWhen(Environment.GetEnvironmentVariable("DW_HINTING_PARITY") is not "1",
                "measurement probe; set DW_HINTING_PARITY=1 to run");

            string[] fontFiles =
            {
                @"C:\Windows\Fonts\tahoma.ttf",
                @"C:\Windows\Fonts\verdana.ttf",
                @"C:\Windows\Fonts\cour.ttf",
            };

            var report = new System.Text.StringBuilder();
            var measured = 0;

            report.AppendLine("font size: v40 / full / autohint (RMSE vs DirectWrite host, same pens)");

            foreach (var fontFile in fontFiles)
            {
                if (!File.Exists(fontFile))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(fontFile);

                var hinted = SyntheticFont.FromBytes(bytes).CreateGlyphTypeface();
                var stripped = SyntheticFont.FromBytes(bytes);

                stripped.Remove("fpgm");
                stripped.Remove("prep");
                stripped.Remove("cvt ");

                var autoHinted = stripped.CreateGlyphTypeface();

                Assert.True(hinted.HasTrueTypeHinting, $"{Path.GetFileName(fontFile)} should be instructed");

                using var skData = SKData.CreateCopy(bytes);
                using var skTypeface = SKTypeface.FromData(skData);

                Assert.NotNull(skTypeface);

                for (var size = 9f; size <= 16f; size++)
                {
                    var reference = RenderDirectWriteBlob(skTypeface!, hinted, size,
                        out var pens, out var width, out var height);
                    var natural = ComposeGrayscale(hinted, size, pens, width, height, stemSnap: false);
                    var full = ComposeGrayscale(hinted, size, pens, width, height, stemSnap: true);
                    var auto = ComposeGrayscale(autoHinted, size, pens, width, height, stemSnap: false);

                    var v40 = Rmse(reference, natural, width, height);
                    var strong = Rmse(reference, full, width, height);
                    var fallback = Rmse(reference, auto, width, height);

                    report.AppendLine(FormattableString.Invariant(
                        $"{Path.GetFileName(fontFile)} {size}px: {v40:0.00} / {strong:0.00} / {fallback:0.00}"));
                }

                measured++;
            }

            var path = Path.Combine(Path.GetTempPath(), "dw-hinting-parity.txt");

            File.WriteAllText(path, report.ToString());
            Assert.True(measured > 0, "no legacy system fonts found to compare against");
        }

        private static double Rmse(byte[] referenceBgra, byte[] luma, int width, int height)
        {
            var sum = 0.0;

            for (var i = 0; i < width * height; i++)
            {
                // The reference is grayscale-on-white in BGRA; any channel is the luma.
                double delta = referenceBgra[i * 4 + 1] - luma[i];

                sum += delta * delta;
            }

            return Math.Sqrt(sum / (width * height));
        }

        private static byte[] ComposeGrayscale(GlyphTypeface typeface, float size, int[] pens,
            int width, int height, bool stemSnap)
        {
            var table = MaskGamma.BuildCalibrationTable(0, MaskGamma.Contrast, MaskGamma.Gamma);
            var luma = new byte[width * height];

            Array.Fill(luma, (byte)255);

            using var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var baseline = (int)Math.Ceiling(size * 1.25);

            for (var i = 0; i < Sample.Length; i++)
            {
                var glyph = typeface.CharacterToGlyphMap[Sample[i]];
                var mask = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, GridFit: true, StemSnap: stemSnap));

                if (mask.IsEmpty)
                {
                    continue;
                }

                for (var y = 0; y < mask.Height; y++)
                {
                    var row = baseline + mask.Top + y;

                    if (row < 0 || row >= height)
                    {
                        continue;
                    }

                    for (var x = 0; x < mask.Width; x++)
                    {
                        var column = pens[i] + mask.Left + x;

                        if (column < 0 || column >= width)
                        {
                            continue;
                        }

                        var coverage = table[mask.Alpha[y * mask.Width + x]];
                        var value = (byte)(255 - coverage);

                        if (value < luma[row * width + column])
                        {
                            luma[row * width + column] = value;
                        }
                    }
                }
            }

            return luma;
        }

        private static byte[] RenderDirectWriteBlob(SKTypeface skTypeface, GlyphTypeface typeface,
            float size, out int[] pens, out int width, out int height)
        {
            var scale = size / typeface.Metrics.DesignEmHeight;
            var glyphs = new ushort[Sample.Length];
            pens = new int[Sample.Length];
            var penX = 8;

            for (var i = 0; i < Sample.Length; i++)
            {
                glyphs[i] = typeface.CharacterToGlyphMap[Sample[i]];
                pens[i] = penX;
                typeface.TryGetGlyphMetrics(glyphs[i], out var metrics);
                penX += (int)Math.Round(metrics.AdvanceWidth * scale);
            }

            width = penX + 8;
            height = (int)Math.Ceiling(size * 1.7);

            var baseline = (int)Math.Ceiling(size * 1.25);
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            using var font = new SKFont(skTypeface, size)
            {
                Hinting = SKFontHinting.Full,
                Subpixel = false,
                Edging = SKFontEdging.Antialias,
            };
            using var paint = new SKPaint { Color = SKColors.Black };
            using var builder = new SKTextBlobBuilder();

            var run = builder.AllocatePositionedRun(font, glyphs.Length);
            var runGlyphs = run.Glyphs;
            var positions = run.Positions;

            for (var i = 0; i < glyphs.Length; i++)
            {
                runGlyphs[i] = glyphs[i];
                positions[i] = new SKPoint(pens[i], baseline);
            }

            surface.Canvas.Clear(SKColors.White);

            using (var blob = builder.Build())
            {
                surface.Canvas.DrawText(blob, 0, 0, paint);
            }

            surface.Canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var readback = new SKBitmap(info);

            snapshot.ReadPixels(info, readback.GetPixels(), readback.RowBytes, 0, 0);

            var pixels = new byte[width * height * 4];

            System.Runtime.InteropServices.Marshal.Copy(readback.GetPixels(), pixels, 0, pixels.Length);

            return pixels;
        }
    }
}
