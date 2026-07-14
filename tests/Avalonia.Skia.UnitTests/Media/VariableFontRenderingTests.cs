using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The managed table walkers must bake glyph outlines at the variation point the system
    /// font collection matched: a Bahnschrift requested at Bold has to produce Bold outlines
    /// from gvar, not the file's default instance. Skips where the system has no Bahnschrift.
    /// </summary>
    public class VariableFontRenderingTests
    {
        [Theory]
        [InlineData(300)]
        [InlineData(700)]
        public void Managed_Outlines_Track_The_Platform_Matched_Weight(int weight)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Bahnschrift.");

            using var skTypeface = SKFontManager.Default.MatchFamily("Bahnschrift",
                new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));

            Assert.SkipWhen(skTypeface is null || !skTypeface.FamilyName.Contains("Bahnschrift"),
                "Bahnschrift is not installed.");

            using var app = StartWithSystemFonts();

            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface("Bahnschrift", FontStyle.Normal, (FontWeight)weight), out var typeface));

            var glyph = typeface!.CharacterToGlyphMap['H'];

            // The same face instanced explicitly at the requested weight is the reference; the
            // default instance is what a lost platform match falls back to.
            var defaultInstance = typeface.WithVariation(default);
            var explicitInstance = defaultInstance.WithVariations(
                FontVariationSettings.Parse(FormattableString.Invariant($"wght={weight}")));

            var matched = Contours(typeface, glyph);

            Assert.Equal(Contours(explicitInstance, glyph), matched);
            Assert.NotEqual(Contours(defaultInstance, glyph), matched);
        }

        /// <summary>The walked outline of <paramref name="glyph"/>, one entry per curve point.</summary>
        private static string Contours(GlyphTypeface typeface, ushort glyph)
        {
            var sink = new SlugContourSink();

            Assert.True(typeface.TryBuildGlyphContours(glyph, Matrix.Identity, sink));

            var builder = new System.Text.StringBuilder();

            for (var contour = 0; contour < sink.ContourCount; contour++)
            {
                for (var i = 0; i < sink.GetCurveCount(contour); i++)
                {
                    var curve = sink.GetCurve(contour, i);

                    builder.Append(FormattableString.Invariant(
                        $"{curve.X1},{curve.Y1} {curve.X2},{curve.Y2} {curve.X3},{curve.Y3};"));
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        [Fact]
        public void Matched_Weights_Produce_Distinct_Managed_Instances()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Bahnschrift.");

            using var light = SKFontManager.Default.MatchFamily("Bahnschrift",
                new SKFontStyle(300, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
            using var bold = SKFontManager.Default.MatchFamily("Bahnschrift",
                new SKFontStyle(700, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));

            Assert.SkipWhen(light is null || bold is null || !light!.FamilyName.Contains("Bahnschrift"),
                "Bahnschrift is not installed.");

            using var app = StartWithSystemFonts();

            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface("Bahnschrift", FontStyle.Normal, FontWeight.Light), out var lightTypeface));
            Assert.True(FontManager.Current.TryGetGlyphTypeface(
                new Typeface("Bahnschrift", FontStyle.Normal, FontWeight.Bold), out var boldTypeface));

            // The instances must differ at the variation layer, not merely at the platform
            // handle: the bold clone carries non-default settings and distinct coordinates.
            Assert.True(boldTypeface.VariationPosition != lightTypeface.VariationPosition,
                "Light and Bold resolved to identical variation settings — the platform-matched weight was not applied.");
        }

        private static IDisposable StartWithSystemFonts()
            => UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(
                renderInterface: new PlatformRenderInterface(), systemFontProvider: new SkiaFontProvider()));
    }
}
