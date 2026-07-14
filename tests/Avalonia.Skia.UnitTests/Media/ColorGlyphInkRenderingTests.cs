using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Pixel-level truth for color glyph rendering through the REAL text pipeline —
    /// TextLayout shaping, the record-time COLR splitter, drawings and masks — comparing the
    /// inked extent of the managed stack against the backend stack per emoji. Declared bounds
    /// can lie; inked pixels cannot. Set COLOR_GLYPH_DIAG_DIR to dump side-by-side renders for
    /// eyeballing. Skips without the Windows-shipped Segoe UI Emoji.
    /// </summary>
    public class ColorGlyphInkRenderingTests
    {
        private const int Canvas = 256;
        private const int Margin = 72;
        private const double EmSize = 64;

        [Fact]
        public void Managed_TextLayout_Emoji_Ink_Matches_The_Backend()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Segoe UI Emoji.");

            using var probe = SKFontManager.Default.MatchFamily("Segoe UI Emoji", SKFontStyle.Normal);

            Assert.SkipWhen(probe is null || !probe.FamilyName.Contains("Emoji"),
                "Segoe UI Emoji is not installed.");

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new FontManagerImpl()));

            var options = new FontManagerOptions();

            AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(options);

            var samples = new[] { "🔥", "❤️", "🌈", "🦊", "😀", "🚀", "😀🔥❤️", "A🔥B🌈C" };
            var failures = new List<string>();
            var diagDir = Environment.GetEnvironmentVariable("COLOR_GLYPH_DIAG_DIR");

            for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                var sample = samples[sampleIndex];
                var backend = RenderInk(sample, TextRasterizationMode.Backend, options, diagDir, sampleIndex);
                var managed = RenderInk(sample, TextRasterizationMode.Managed, options, diagDir, sampleIndex);

                if (backend is null || managed is null)
                {
                    failures.Add($"{sample}: no ink at all (backend {backend}, managed {managed})");
                    continue;
                }

                var b = backend.Value;
                var m = managed.Value;

                // Sub-pixel phase and AA explain a couple of pixels; anything more is missing
                // or displaced ink. The per-edge deltas name the clipped side directly.
                const double tolerance = 2.5;

                if (Math.Abs(m.Left - b.Left) > tolerance || Math.Abs(m.Top - b.Top) > tolerance ||
                    Math.Abs(m.Right - b.Right) > tolerance || Math.Abs(m.Bottom - b.Bottom) > tolerance)
                {
                    failures.Add(FormattableString.Invariant(
                        $"{sample}: managed ink {m} vs backend {b} (dL {m.Left - b.Left:0.0} dT {m.Top - b.Top:0.0} dR {m.Right - b.Right:0.0} dB {m.Bottom - b.Bottom:0.0})"));
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void Managed_Emoji_Ink_Is_Scale_Consistent()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Segoe UI Emoji.");

            using var probe = SKFontManager.Default.MatchFamily("Segoe UI Emoji", SKFontStyle.Normal);

            Assert.SkipWhen(probe is null || !probe.FamilyName.Contains("Emoji"),
                "Segoe UI Emoji is not installed.");

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new FontManagerImpl()));

            var options = new FontManagerOptions();

            AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(options);

            var samples = new[] { "😀", "🦊", "🔥", "❤️", "🌍", "👍" };
            var sizes = new[] { 24.0, 32.0, 48.0, 96.0 };
            var failures = new List<string>();
            var diagDir = Environment.GetEnvironmentVariable("COLOR_GLYPH_DIAG_DIR");

            for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                var sample = samples[sampleIndex];

                // Reference: the largest size, normalized to fractions of the em.
                var reference = RenderInkAt(sample, 96.0, options, diagDir, $"scale{sampleIndex}-96");

                if (reference is null)
                {
                    failures.Add($"{sample}: no ink at 96px");
                    continue;
                }

                var r = Normalize(reference.Value, 96.0);

                foreach (var emSize in sizes)
                {
                    if (emSize == 96.0)
                    {
                        continue;
                    }

                    var ink = RenderInkAt(sample, emSize, options, diagDir,
                        FormattableString.Invariant($"scale{sampleIndex}-{emSize:0}"));

                    if (ink is null)
                    {
                        failures.Add(FormattableString.Invariant($"{sample}@{emSize}: no ink"));
                        continue;
                    }

                    var n = Normalize(ink.Value, emSize);

                    // Compare in the smaller size's device pixels: AA, phase snapping, and mask
                    // rounding explain a couple of pixels; a hard clip is far bigger.
                    const double tolerance = 2.5;

                    var dL = (n.Left - r.Left) * emSize;
                    var dT = (n.Top - r.Top) * emSize;
                    var dR = (n.Right - r.Right) * emSize;
                    var dB = (n.Bottom - r.Bottom) * emSize;

                    if (Math.Abs(dL) > tolerance || Math.Abs(dT) > tolerance ||
                        Math.Abs(dR) > tolerance || Math.Abs(dB) > tolerance)
                    {
                        failures.Add(FormattableString.Invariant(
                            $"{sample}@{emSize}: normalized ink drifts from the 96px reference by (dL {dL:0.0} dT {dT:0.0} dR {dR:0.0} dB {dB:0.0}) device px"));
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private static Rect Normalize(Rect ink, double emSize) => new(
            (ink.X - Margin) / emSize, (ink.Y - Margin) / emSize,
            ink.Width / emSize, ink.Height / emSize);

        private static Rect? RenderInkAt(string text, double emSize, FontManagerOptions options,
            string? diagDir, string diagName)
        {
            options.TextRasterizationMode = TextRasterizationMode.Managed;

            var info = new SKImageInfo(Canvas, Canvas, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            using var layout = new TextLayout(text, new Typeface("Segoe UI Emoji"), emSize, Brushes.Black);
            using (var drawingContext = new PlatformDrawingContext(context, false))
            {
                layout.Draw(drawingContext, new Point(Margin, Margin));
            }

            if (!string.IsNullOrEmpty(diagDir))
            {
                Directory.CreateDirectory(diagDir!);

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(diagDir!, $"{diagName}.png"));

                data.SaveTo(file);
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (var y = 0; y < Canvas; y++)
            {
                for (var x = 0; x < Canvas; x++)
                {
                    var color = bitmap.GetPixel(x, y);

                    if (Math.Abs(color.Red - 255) > 12 || Math.Abs(color.Green - 255) > 12 ||
                        Math.Abs(color.Blue - 255) > 12)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            return minX > maxX ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static Rect? RenderInk(string text, TextRasterizationMode mode, FontManagerOptions options,
            string? diagDir, int sampleIndex)
        {
            options.TextRasterizationMode = mode;

            var info = new SKImageInfo(Canvas, Canvas, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            canvas.Clear(SKColors.White);

            // The layout is created after the mode flip so its runs materialize on that stack.
            using var layout = new TextLayout(text, new Typeface("Segoe UI Emoji"), EmSize, Brushes.Black);
            using (var drawingContext = new PlatformDrawingContext(context, false))
            {
                layout.Draw(drawingContext, new Point(Margin, Margin));
            }

            if (!string.IsNullOrEmpty(diagDir))
            {
                Directory.CreateDirectory(diagDir!);

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(diagDir!,
                    $"sample{sampleIndex}-{mode}.png"));

                data.SaveTo(file);
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (var y = 0; y < Canvas; y++)
            {
                for (var x = 0; x < Canvas; x++)
                {
                    var color = bitmap.GetPixel(x, y);

                    if (Math.Abs(color.Red - 255) > 12 || Math.Abs(color.Green - 255) > 12 ||
                        Math.Abs(color.Blue - 255) > 12)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            return minX > maxX ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }
}
