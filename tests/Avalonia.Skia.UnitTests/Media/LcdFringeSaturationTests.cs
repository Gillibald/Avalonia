using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Pins the subpixel fringe saturation to the DirectWrite host: at identical glyphs and
    /// integer pens, the mean |R-B| of fringed pixels in our composite must sit close to the
    /// DW blob's. A too-wide stripe filter washes fringes soft (reads as a temperature cast
    /// against ClearType); no filtering overshoots into harsh color. Windows-only by nature.
    /// </summary>
    public class LcdFringeSaturationTests
    {
        private const string Sample = "Hamburgefonstiv fi ffl 0123";

        [Fact]
        public void Fringe_Saturation_Tracks_The_DirectWrite_Host()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "DirectWrite comparison");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal);

            Assert.NotNull(skTypeface);

            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface!, FontSimulations.None));
            var table = MaskGamma.BuildCalibrationTable(0, MaskGamma.LcdContrast, MaskGamma.LcdGamma);

            foreach (var size in new float[] { 11, 13, 16, 24 })
            {
                var reference = RenderBlob(skTypeface!, typeface, size, out var pens, out var width, out var height);
                var ours = ComposeSubpixel(typeface, size, pens, width, height, table);

                var referenceSaturation = MeanFringeSaturation(reference, width, height, bgra: true);
                var oursSaturation = MeanFringeSaturation(ours, width, height, bgra: false);

                Assert.True(referenceSaturation > 0, "reference produced no fringes - LCD rendering unavailable?");

                var ratio = oursSaturation / referenceSaturation;

                Assert.True(ratio >= 0.8 && ratio <= 1.15,
                    FormattableString.Invariant(
                        $"{size}px: fringe saturation {oursSaturation:0.0} vs DW {referenceSaturation:0.0} (ratio {ratio:0.00}) - the stripe filter is washing or overshooting the DW fringe character"));
            }
        }

        private static double MeanFringeSaturation(byte[] pixels, int width, int height, bool bgra)
        {
            var stride = bgra ? 4 : 3;
            var saturation = 0.0;
            var fringed = 0;

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

                var delta = Math.Abs(r - b);

                if (delta > 8)
                {
                    saturation += delta;
                    fringed++;
                }
            }

            return fringed == 0 ? 0 : saturation / fringed;
        }

        private static byte[] ComposeSubpixel(GlyphTypeface typeface, float size, int[] pens,
            int width, int height, byte[] table)
        {
            var composite = new byte[width * height * 3];

            for (var i = 0; i < composite.Length; i++)
            {
                composite[i] = 255;
            }

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
