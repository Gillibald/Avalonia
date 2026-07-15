using System;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: quantifies the fringe "temperature" gap between our subpixel
    /// composite and the DirectWrite-host LCD blob at identical glyphs and integer pens.
    /// Reports per-channel RMSE, warm/cool fringe energy, and an RMSE sweep over a
    /// ClearType-level style chroma attenuation applied after the gamma table — DirectWrite
    /// mutes fringes toward the grayscale average and we never modeled that stage.
    /// Env-gated, not a gate.</summary>
    public class LcdTemperatureProbe
    {
        private const string Sample = "Hamburgefonstiv fi ffl 0123";

        [Fact]
        public void Measure_Fringe_Temperature_Against_The_DirectWrite_Blob()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("LCD_TEMPERATURE_PROBE") != "1", "probe");
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
            var report = new StringBuilder();
            float[] sizes = { 11, 13, 16, 24 };
            double[] levels = { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.35, 0.2, 0.0 };

            var lcdTable = MaskGamma.BuildCalibrationTable(0, 0.2, 1.6);     // production LCD family
            var grayTable = MaskGamma.BuildCalibrationTable(0, 0.5, 2.2);    // production grayscale family

            var levelTotals = new double[levels.Length];
            double grayTotal = 0;

            foreach (var size in sizes)
            {
                var reference = RenderBlob(skTypeface!, typeface, size, out var pens, out var width, out var height);

                report.AppendLine(FormattableString.Invariant($"== {size}px ({width}x{height}) =="));

                // Reference temperature: signed warm/cool fringe energy per side.
                Fringes(reference, width, height, bgra: true, out var refWarm, out var refCool, out var refSat);
                report.AppendLine(FormattableString.Invariant(
                    $"  DW blob   : warm {refWarm:0} cool {refCool:0} meanSat {refSat:0.00}"));

                // Our subpixel composite through the production LCD table, then per level.
                var subpixel = ComposeSubpixel(typeface, size, pens, width, height, lcdTable);

                for (var li = 0; li < levels.Length; li++)
                {
                    var attenuated = ApplyLevel(subpixel, levels[li]);
                    var rmse = Rmse(attenuated, reference, width, height, out var r, out var g, out var b);

                    levelTotals[li] += rmse;

                    if (li == 0)
                    {
                        Fringes(attenuated, width, height, bgra: false, out var warm, out var cool, out var sat);
                        report.AppendLine(FormattableString.Invariant(
                            $"  ours level 1.00: rmse {rmse:0.00} (R {r:0.00} G {g:0.00} B {b:0.00}) warm {warm:0} cool {cool:0} meanSat {sat:0.00}"));
                    }
                    else
                    {
                        report.AppendLine(FormattableString.Invariant(
                            $"  ours level {levels[li]:0.00}: rmse {rmse:0.00} (R {r:0.00} G {g:0.00} B {b:0.00})"));
                    }
                }

                // The user's paradox: plain grayscale masks against the DW LCD reference.
                var gray = ComposeGrayscale(typeface, size, pens, width, height, grayTable);
                var grayRmse = Rmse(gray, reference, width, height, out var gr, out var gg, out var gb);

                grayTotal += grayRmse;
                report.AppendLine(FormattableString.Invariant(
                    $"  grayscale : rmse {grayRmse:0.00} (R {gr:0.00} G {gg:0.00} B {gb:0.00})"));
            }

            var bestLevel = 0.0;
            var best = double.MaxValue;

            for (var li = 0; li < levels.Length; li++)
            {
                report.AppendLine(FormattableString.Invariant($"level {levels[li]:0.00} sum {levelTotals[li]:0.00}"));

                if (levelTotals[li] < best)
                {
                    best = levelTotals[li];
                    bestLevel = levels[li];
                }
            }

            report.AppendLine(FormattableString.Invariant(
                $"BEST level {bestLevel:0.00} (sum {best:0.00}); grayscale sum {grayTotal:0.00}"));
            Assert.Fail(report.ToString());
        }

        [Fact]
        public void Sweep_Stripe_Kernels_And_Transfer_Curves()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("LCD_TEMPERATURE_PROBE") != "1", "probe");
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
            var report = new StringBuilder();
            float[] sizes = { 11, 13, 16, 24 };

            // Symmetric stripe kernels over the 3x samples; sum == divisor keeps interiors solid.
            (string Name, int[] Taps)[] kernels =
            {
                ("12321/9", new[] { 1, 2, 3, 2, 1 }),
                ("121/4  ", new[] { 0, 1, 2, 1, 0 }),
                ("111/3  ", new[] { 0, 1, 1, 1, 0 }),
                ("1/1    ", new[] { 0, 0, 1, 0, 0 }),
            };

            // Transfer curves: the production LCD table plus DW-style quadratic enhanced
            // contrast (x + k*x*(1-x)) followed by a display-gamma exponent.
            (string Name, byte[] Table)[] transfers = BuildTransferCandidates();

            var totals = new double[kernels.Length, transfers.Length];

            foreach (var size in sizes)
            {
                var reference = RenderBlob(skTypeface!, typeface, size, out var pens, out var width, out var height);

                Fringes(reference, width, height, bgra: true, out _, out _, out var refSat);
                report.AppendLine(FormattableString.Invariant($"== {size}px == (DW meanSat {refSat:0.0})"));

                var raw = RasterizeRaw(typeface, size, pens);

                for (var ki = 0; ki < kernels.Length; ki++)
                {
                    var line = new StringBuilder(FormattableString.Invariant($"  {kernels[ki].Name}:"));

                    for (var ti = 0; ti < transfers.Length; ti++)
                    {
                        var composite = ComposeFromRaw(raw, pens, width, height, size, kernels[ki].Taps, transfers[ti].Table);
                        var rmse = Rmse(composite, reference, width, height, out _, out _, out _);

                        totals[ki, ti] += rmse;

                        Fringes(composite, width, height, bgra: false, out _, out _, out var sat);
                        line.Append(FormattableString.Invariant($"  {transfers[ti].Name} {rmse:0.0}/{sat:0.0}"));
                    }

                    report.AppendLine(line.ToString());
                }
            }

            var bestK = 0;
            var bestT = 0;
            var best = double.MaxValue;

            for (var ki = 0; ki < kernels.Length; ki++)
            {
                for (var ti = 0; ti < transfers.Length; ti++)
                {
                    if (totals[ki, ti] < best)
                    {
                        best = totals[ki, ti];
                        bestK = ki;
                        bestT = ti;
                    }
                }
            }

            report.AppendLine(FormattableString.Invariant(
                $"BEST {kernels[bestK].Name} + {transfers[bestT].Name} (sum {best:0.00}; current 12321+prod = {totals[0, 0]:0.00})"));
            Assert.Fail(report.ToString());
        }

        private static (string, byte[])[] BuildTransferCandidates()
        {
            var list = new System.Collections.Generic.List<(string, byte[])>
            {
                ("prod(0.2/1.6)", MaskGamma.BuildCalibrationTable(0, 0.2, 1.6)),
            };

            foreach (var k in new[] { 0.0, 0.5, 1.0 })
            {
                foreach (var gamma in new[] { 1.4, 1.8, 2.2 })
                {
                    var table = new byte[256];

                    for (var i = 0; i < 256; i++)
                    {
                        var x = i / 255.0;
                        var contrasted = x + k * x * (1 - x);
                        table[i] = (byte)Math.Clamp(Math.Round(255.0 * Math.Pow(contrasted, 1.0 / gamma)), 0, 255);
                    }

                    list.Add((FormattableString.Invariant($"q{k:0.0}/g{gamma:0.0}"), table));
                }
            }

            return list.ToArray();
        }

        private sealed class RawGlyph
        {
            public byte[] Samples = Array.Empty<byte>();
            public int Width;
            public int Height;
            public int Left;
            public int Top;
        }

        /// <summary>The 3x coverage samples per glyph, mirroring the subpixel branch of
        /// GlyphMasks.Build (no grid fit, phase 0) so kernels can be swapped downstream.</summary>
        private static RawGlyph[] RasterizeRaw(GlyphTypeface typeface, float size, int[] pens)
        {
            var scale = size / typeface.Metrics.DesignEmHeight;
            var scratch = new GlyphPathBuilder();
            var result = new RawGlyph[Sample.Length];

            for (var i = 0; i < Sample.Length; i++)
            {
                var raw = new RawGlyph();

                result[i] = raw;

                var glyph = typeface.CharacterToGlyphMap[Sample[i]];

                if (!typeface.TryGetGlyphInkBounds(glyph, out var box) ||
                    box.XMax <= box.XMin || box.YMax <= box.YMin)
                {
                    continue;
                }

                var left = (int)Math.Floor(box.XMin * scale) - GlyphMasks.SubpixelApron;
                var top = (int)Math.Floor(-box.YMax * scale) - 1;
                var width = (int)Math.Ceiling(box.XMax * scale) + GlyphMasks.SubpixelApron - left;
                var height = (int)Math.Ceiling(-box.YMin * scale) + 1 - top;

                scratch.Reset();

                if (!typeface.TryBuildGlyphContours(glyph, new Matrix(scale * 3, 0, 0, -scale, 0, 0), scratch))
                {
                    continue;
                }

                var samples = new byte[width * 3 * height];

                GlyphRasterizer.Rasterize(scratch, width * 3, height, -left * 3, -top, aliased: false, samples);

                raw.Samples = samples;
                raw.Width = width;
                raw.Height = height;
                raw.Left = left;
                raw.Top = top;
            }

            return result;
        }

        private static byte[] ComposeFromRaw(RawGlyph[] raw, int[] pens, int width, int height,
            float size, int[] taps, byte[] table)
        {
            var composite = NewWhiteRgb(width, height);
            var baseline = (int)Math.Ceiling(size * 1.25);
            var divisor = 0;

            foreach (var tap in taps)
            {
                divisor += tap;
            }

            for (var i = 0; i < raw.Length; i++)
            {
                var glyph = raw[i];

                if (glyph.Samples.Length == 0)
                {
                    continue;
                }

                var subWidth = glyph.Width * 3;

                for (var y = 0; y < glyph.Height; y++)
                {
                    var row = baseline + glyph.Top + y;

                    if (row < 0 || row >= height)
                    {
                        continue;
                    }

                    var sampleRow = y * subWidth;

                    for (var x = 0; x < glyph.Width; x++)
                    {
                        var column = pens[i] + glyph.Left + x;

                        if (column < 0 || column >= width)
                        {
                            continue;
                        }

                        for (var channel = 0; channel < 3; channel++)
                        {
                            var s = x * 3 + channel;
                            var acc = 0;

                            for (var tap = 0; tap < taps.Length; tap++)
                            {
                                if (taps[tap] == 0)
                                {
                                    continue;
                                }

                                var index = s + tap - 2;

                                if (index >= 0 && index < subWidth)
                                {
                                    acc += taps[tap] * glyph.Samples[sampleRow + index];
                                }
                            }

                            var corrected = table[(acc + divisor / 2) / divisor];
                            var target = (row * width + column) * 3 + channel;

                            composite[target] = (byte)(composite[target] * (255 - corrected) / 255);
                        }
                    }
                }
            }

            return composite;
        }

        /// <summary>Chroma attenuation toward the per-pixel channel mean — the ClearType-level
        /// analog. Level 1 = untouched fringes, level 0 = pure grayscale of the same coverage.</summary>
        private static byte[] ApplyLevel(byte[] rgb, double level)
        {
            if (level >= 1.0)
            {
                return rgb;
            }

            var result = new byte[rgb.Length];

            for (var i = 0; i < rgb.Length; i += 3)
            {
                var mean = (rgb[i] + rgb[i + 1] + rgb[i + 2]) / 3.0;

                for (var c = 0; c < 3; c++)
                {
                    result[i + c] = (byte)Math.Clamp(mean + level * (rgb[i + c] - mean), 0, 255);
                }
            }

            return result;
        }

        private static void Fringes(byte[] pixels, int width, int height, bool bgra,
            out double warm, out double cool, out double meanSaturation)
        {
            warm = 0;
            cool = 0;
            var saturation = 0.0;
            var fringed = 0;
            var stride = bgra ? 4 : 3;

            for (var i = 0; i < width * height; i++)
            {
                var offset = i * stride;
                int r = bgra ? pixels[offset + 2] : pixels[offset];
                int g = pixels[offset + 1];
                int b = bgra ? pixels[offset] : pixels[offset + 2];

                if (r > 250 && g > 250 && b > 250)
                {
                    continue;
                }

                var delta = r - b;

                if (delta > 0)
                {
                    warm += delta;
                }
                else
                {
                    cool -= delta;
                }

                if (Math.Abs(delta) > 8)
                {
                    saturation += Math.Abs(delta);
                    fringed++;
                }
            }

            meanSaturation = fringed == 0 ? 0 : saturation / fringed;
        }

        private static double Rmse(byte[] rgb, byte[] referenceBgra, int width, int height,
            out double rmseR, out double rmseG, out double rmseB)
        {
            double sr = 0, sg = 0, sb = 0;
            var counted = 0;

            for (var i = 0; i < width * height; i++)
            {
                var us = i * 3;
                var them = i * 4;

                int dr = rgb[us] - referenceBgra[them + 2];
                int dg = rgb[us + 1] - referenceBgra[them + 1];
                int db = rgb[us + 2] - referenceBgra[them];

                var inked = rgb[us] < 250 || rgb[us + 1] < 250 || rgb[us + 2] < 250 ||
                            referenceBgra[them] < 250 || referenceBgra[them + 1] < 250 || referenceBgra[them + 2] < 250;

                if (inked)
                {
                    sr += (double)dr * dr;
                    sg += (double)dg * dg;
                    sb += (double)db * db;
                    counted++;
                }
            }

            if (counted == 0)
            {
                rmseR = rmseG = rmseB = 0;
                return 0;
            }

            rmseR = Math.Sqrt(sr / counted);
            rmseG = Math.Sqrt(sg / counted);
            rmseB = Math.Sqrt(sb / counted);

            return Math.Sqrt((sr + sg + sb) / (3.0 * counted));
        }

        private static byte[] ComposeSubpixel(GlyphTypeface typeface, float size, int[] pens,
            int width, int height, byte[] table)
        {
            var composite = NewWhiteRgb(width, height);
            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var baseline = (int)Math.Ceiling(size * 1.25);

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
                            var corrected = table[mask.Alpha[(y * mask.Width + x) * 3 + channel]];
                            var index = (row * width + column) * 3 + channel;

                            composite[index] = (byte)(composite[index] * (255 - corrected) / 255);
                        }
                    }
                }
            }

            return composite;
        }

        private static byte[] ComposeGrayscale(GlyphTypeface typeface, float size, int[] pens,
            int width, int height, byte[] table)
        {
            var composite = NewWhiteRgb(width, height);
            var scratch = new GlyphPathBuilder();
            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var baseline = (int)Math.Ceiling(size * 1.25);

            for (var i = 0; i < Sample.Length; i++)
            {
                var glyph = typeface.CharacterToGlyphMap[Sample[i]];
                var mask = GlyphMasks.Build(typeface, scratch,
                    new GlyphMaskKey(glyph, scaleQ, 0, GlyphMaskMode.Antialiased, GridFit: false));

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

                        var corrected = table[mask.Alpha[y * mask.Width + x]];
                        var index = (row * width + column) * 3;

                        for (var channel = 0; channel < 3; channel++)
                        {
                            composite[index + channel] = (byte)(composite[index + channel] * (255 - corrected) / 255);
                        }
                    }
                }
            }

            return composite;
        }

        private static byte[] NewWhiteRgb(int width, int height)
        {
            var composite = new byte[width * height * 3];

            for (var i = 0; i < composite.Length; i++)
            {
                composite[i] = 255;
            }

            return composite;
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
    }
}
