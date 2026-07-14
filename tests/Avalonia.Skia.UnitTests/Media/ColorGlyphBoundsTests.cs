using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A color glyph's ink is its layer union (COLR v0) or paint-graph extent (COLR v1), not
    /// its base outline: a managed run that declares base-outline bounds under-invalidates
    /// and clips color glyphs on partial redraws. The ink the managed stack draws is the
    /// ground truth the managed bounds must contain. Skips without the Windows-shipped
    /// Segoe UI Emoji.
    /// </summary>
    public class ColorGlyphBoundsTests
    {
        [Fact]
        public void Managed_Run_Bounds_Contain_The_Drawn_Color_Ink()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Segoe UI Emoji.");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI Emoji", SKFontStyle.Normal);

            Assert.SkipWhen(skTypeface is null || !skTypeface.FamilyName.Contains("Emoji"),
                "Segoe UI Emoji is not installed.");

            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            var typeface = TestGlyphTypefaces.FromSKTypeface(skTypeface!);

            Assert.NotNull(typeface);
            Assert.SkipWhen(typeface!.ColorTable is null, "Segoe UI Emoji has no COLR table here.");

            var codepoints = new[] { 0x1F525, 0x2764, 0x1F308, 0x1F98A, 0x1F680, 0x2B50, 0x1F600 };
            var failures = new List<string>();
            var checked_ = 0;

            foreach (var codepoint in codepoints)
            {
                if (!typeface.CharacterToGlyphMap.ContainsGlyph(codepoint))
                {
                    continue;
                }

                var glyph = typeface.CharacterToGlyphMap[codepoint];

                typeface.TryGetGlyphMetrics(glyph, out var metrics);

                var scale = 64.0 / typeface.Metrics.DesignEmHeight;
                var infos = new List<GlyphInfo> { new(glyph, 0, metrics.AdvanceWidth * scale) };
                var origin = new Point(48, 112);

                using var managed = new ManagedGlyphRunImpl(typeface, 64, infos, origin);

                checked_++;

                var ink = DrawnInk(typeface, infos, origin, managed);

                if (ink is null)
                {
                    failures.Add(FormattableString.Invariant($"U+{codepoint:X}: nothing drawn"));
                    continue;
                }

                // Antialiased edges bleed up to a pixel past the declared box.
                if (!managed.Bounds.Inflate(1).Contains(ink.Value))
                {
                    failures.Add(FormattableString.Invariant(
                        $"U+{codepoint:X}: managed {managed.Bounds} does not contain the drawn ink {ink.Value}"));
                }
            }

            Assert.True(checked_ >= 4, "Too few emoji resolved to glyphs for a meaningful check.");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        /// <summary>
        /// The pixel extent the managed stack paints for the run: COLR v1 glyphs through the
        /// colour split, everything else through the glyph run itself.
        /// </summary>
        private static Rect? DrawnInk(GlyphTypeface typeface, List<GlyphInfo> infos, Point origin,
            ManagedGlyphRunImpl managed)
        {
            const int size = 256;

            var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(
                canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);
            using var glyphRun = new GlyphRun(typeface, 64, default, infos, origin);

            canvas.Clear(SKColors.White);

            if (!ColorGlyphRunSplitter.TryDraw(context, glyphRun, Brushes.Black))
            {
                contextImpl.DrawGlyphRun(Brushes.Black, managed);
            }

            var pixels = bitmap.GetPixelSpan();
            int left = size, top = size, right = -1, bottom = -1;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;

                    if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x + 1);
                        bottom = Math.Max(bottom, y + 1);
                    }
                }
            }

            return right < 0 ? null : new Rect(left, top, right - left, bottom - top);
        }
    }
}
