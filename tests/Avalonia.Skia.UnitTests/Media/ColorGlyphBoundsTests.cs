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

        public static IEnumerable<object[]> ColorKindsAndSimulations()
        {
            foreach (var kind in new[] { "ColrV0", "ColrV1", "Cbdt", "Sbix" })
            {
                foreach (var simulations in new[]
                         {
                             FontSimulations.Bold, FontSimulations.Oblique,
                             FontSimulations.Bold | FontSimulations.Oblique,
                         })
                {
                    yield return new object[] { kind, simulations };
                }
            }
        }

        [Theory]
        [MemberData(nameof(ColorKindsAndSimulations))]
        public void Simulated_Variants_Report_The_Source_Colour_Glyph_Bounds(string kind, FontSimulations simulations)
        {
            using var scope = CreateEnvironment();
            var source = CreateTypeface(kind, out var colorGlyph);
            var variant = source.WithSimulations(simulations);

            Assert.True(variant.IsColorGlyph(colorGlyph));

            // Colour glyphs are never simulated, so every bounds query of the variant answers
            // exactly what the source face answers.
            Assert.Equal(source.TryGetColorGlyphInkBounds(colorGlyph, out var sourceInk),
                variant.TryGetColorGlyphInkBounds(colorGlyph, out var variantInk));
            Assert.Equal(sourceInk, variantInk);

            var sourceBounds = new GlyphBounds[1];
            var variantBounds = new GlyphBounds[1];

            Assert.Equal(source.TryGetGlyphBounds(new[] { colorGlyph }, sourceBounds),
                variant.TryGetGlyphBounds(new[] { colorGlyph }, variantBounds));
            Assert.Equal(sourceBounds[0], variantBounds[0]);

            Assert.True(source.TryGetGlyphMetrics(colorGlyph, out var sourceMetrics));
            Assert.True(variant.TryGetGlyphMetrics(colorGlyph, out var variantMetrics));
            Assert.Equal(sourceMetrics, variantMetrics);

            Assert.Equal(source.GetGlyphOutline(colorGlyph)?.Bounds, variant.GetGlyphOutline(colorGlyph)?.Bounds);

            var infos = new List<GlyphInfo> { new(colorGlyph, 0, 32) };

            using var sourceRun = new ManagedGlyphRunImpl(source, 32, infos, new Point(8, 40));
            using var variantRun = new ManagedGlyphRunImpl(variant, 32, infos, new Point(8, 40));

            Assert.Equal(sourceRun.Bounds, variantRun.Bounds);
        }

        [Theory]
        [MemberData(nameof(ColorKindsAndSimulations))]
        public void Simulated_Mixed_Run_Bounds_Cover_The_Drawn_Ink(string kind, FontSimulations simulations)
        {
            using var scope = CreateEnvironment();
            var typeface = CreateTypeface(kind, out var colorGlyph).WithSimulations(simulations);
            var plainGlyph = typeface.CharacterToGlyphMap['A'];

            // The outline glyph still grows and slants, the colour glyph keeps its own box.
            var infos = new List<GlyphInfo> { new(plainGlyph, 0, 40), new(colorGlyph, 1, 32) };

            using var run = new ManagedGlyphRunImpl(typeface, 32, infos, new Point(8, 40));

            var info = new SKImageInfo(120, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var contextImpl = (DrawingContextImpl)Avalonia.Skia.Helpers.DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
            using var context = new PlatformDrawingContext(contextImpl, ownsImpl: false);

            canvas.Clear(SKColors.White);

            // The v1 glyph needs the split; the rest draws through the mask path.
            using var glyphRun = new GlyphRun(typeface, 32, default, infos, new Point(8, 40));

            if (!ColorGlyphRunSplitter.TryDraw(context, glyphRun, Brushes.Black))
            {
                Assert.True(MaskGlyphRunRenderer.TryDraw(contextImpl, run, Brushes.Black, TextRenderingMode.Antialias,
                    TextHintingMode.None));
            }

            var pixels = bitmap.GetPixelSpan();
            var covered = run.Bounds.Inflate(1);

            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    var i = (y * info.Width + x) * 4;

                    if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
                    {
                        Assert.True(covered.Contains(new Point(x + 0.5, y + 0.5)),
                            $"{kind} {simulations}: ink at ({x}, {y}) lies outside the run bounds {run.Bounds}");
                    }
                }
            }
        }

        private static GlyphTypeface CreateTypeface(string kind, out ushort colorGlyph)
        {
            switch (kind)
            {
                case "ColrV0":
                    return ColorGlyphV1SplitTests.CreateV0Typeface(out colorGlyph);
                case "ColrV1":
                    return ColorGlyphV1SplitTests.CreateV1Typeface(out colorGlyph);
                case "Cbdt":
                    return BitmapGlyphRenderingTests.CreateBitmapTypeface(out colorGlyph, out _);
                default:
                    return BitmapGlyphRenderingTests.CreateSbixTypeface(out colorGlyph);
            }
        }

        private static IDisposable CreateEnvironment()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = TextRasterizationMode.Managed,
                });

            return scope;
        }
    }
}
