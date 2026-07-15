using System;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: sweeps LCD gamma/contrast candidates and scores each by RMSE
    /// against the DirectWrite-host LCD blob rendering the identical glyphs at identical
    /// integer pens with hinting off — geometry cancels, only the coverage transfer differs.
    /// Picks the constants for <see cref="MaskGamma.LcdGamma"/>. Env-gated, not a gate.</summary>
    public class LcdGammaCalibrationProbe
    {
        private const string Sample = "Hamburgefonstiv fi ffl 0123";

        [Fact]
        public void Sweep_Lcd_Gamma_Candidates()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("LCD_GAMMA_CALIBRATION") != "1", "probe");
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
            var report = new StringBuilder();
            double[] gammas = { 1.2, 1.4, 1.6, 1.8, 2.0, 2.2 };
            double[] contrasts = { 0.2, 0.35, 0.5 };
            float[] sizes = { 11, 13, 16, 24 };

            var totals = new double[gammas.Length, contrasts.Length];
            var offTotal = 0.0;

            foreach (var size in sizes)
            {
                var reference = RenderBlob(skTypeface!, typeface, size, out var pens, out var width, out var height);

                report.AppendLine(FormattableString.Invariant($"== {size}px ({width}x{height}) =="));

                for (var gi = 0; gi < gammas.Length; gi++)
                {
                    var line = new StringBuilder(FormattableString.Invariant($"gamma {gammas[gi]:0.0}:"));

                    for (var ci = 0; ci < contrasts.Length; ci++)
                    {
                        var table = MaskGamma.BuildCalibrationTable(0, contrasts[ci], gammas[gi]);
                        var rmse = Score(typeface, size, pens, width, height, reference, table);

                        totals[gi, ci] += rmse;
                        line.Append(FormattableString.Invariant($"  c{contrasts[ci]:0.00} {rmse:0.00}"));
                    }

                    report.AppendLine(line.ToString());
                }

                var off = Score(typeface, size, pens, width, height, reference, null);

                offTotal += off;
                report.AppendLine(FormattableString.Invariant($"gamma off: {off:0.00}"));
            }

            var bestGamma = 0.0;
            var bestContrast = 0.0;
            var best = double.MaxValue;

            for (var gi = 0; gi < gammas.Length; gi++)
            {
                for (var ci = 0; ci < contrasts.Length; ci++)
                {
                    if (totals[gi, ci] < best)
                    {
                        best = totals[gi, ci];
                        bestGamma = gammas[gi];
                        bestContrast = contrasts[ci];
                    }
                }
            }

            report.AppendLine(FormattableString.Invariant(
                $"BEST aggregate: gamma {bestGamma:0.0} contrast {bestContrast:0.00} (sum {best:0.00}; gamma-off sum {offTotal:0.00})"));
            Assert.Fail(report.ToString());
        }

        /// <summary>The DirectWrite-host LCD blob at explicit integer pens, hinting off.</summary>
        private static byte[] RenderBlob(SKTypeface skTypeface, GlyphTypeface typeface, float size,
            out int[] pens, out int width, out int height)
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

            using var surface = SKSurface.Create(info, new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
            using var font = new SKFont(skTypeface, size)
            {
                Hinting = SKFontHinting.None,
                Subpixel = false,
                Edging = SKFontEdging.SubpixelAntialias,
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

        /// <summary>Our subpixel masks composited black-on-white through the candidate table
        /// (null = linear), scored against the reference bytes.</summary>
        private static double Score(GlyphTypeface typeface, float size, int[] pens,
            int width, int height, byte[] reference, byte[]? table)
        {
            var baseline = (int)Math.Ceiling(size * 1.25);
            var composite = new byte[width * height * 3];

            for (var i = 0; i < composite.Length; i++)
            {
                composite[i] = 255;
            }

            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(size);

            for (var i = 0; i < Sample.Length; i++)
            {
                var glyph = typeface.CharacterToGlyphMap[Sample[i]];
                var mask = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Subpixel, GridFit: false));

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

                        for (var channel = 0; channel < 3; channel++)
                        {
                            var coverage = mask.Alpha[(y * mask.Width + x) * 3 + channel];
                            var corrected = table is null ? coverage : table[coverage];
                            var index = (row * width + column) * 3 + channel;
                            var value = composite[index];

                            composite[index] = (byte)(value * (255 - corrected) / 255);
                        }
                    }
                }
            }

            var squares = 0.0;
            var counted = 0;

            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var us = (row * width + column) * 3;
                    var them = (row * width + column) * 4;

                    // reference is BGRA; composite is RGB.
                    int dr = composite[us] - reference[them + 2];
                    int dg = composite[us + 1] - reference[them + 1];
                    int db = composite[us + 2] - reference[them];

                    var inked = composite[us] < 250 || composite[us + 1] < 250 || composite[us + 2] < 250 ||
                                reference[them] < 250 || reference[them + 1] < 250 || reference[them + 2] < 250;

                    if (inked)
                    {
                        squares += (double)dr * dr + (double)dg * dg + (double)db * db;
                        counted++;
                    }
                }
            }

            return counted == 0 ? 0 : Math.Sqrt(squares / (3.0 * counted));
        }
    }
}
