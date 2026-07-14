using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization.Slug;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The managed table walkers must bake glyph outlines at the variation point the platform
    /// typeface was matched at — a Bahnschrift matched at Bold has to produce Bold outlines
    /// from gvar, not the file's default instance. Skia's own glyph paths for the same
    /// SKTypeface are the ground truth. Skips where the system has no Bahnschrift.
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

            var typeface = GlyphTypeface.TryCreate(new SkiaTypeface(skTypeface!, FontSimulations.None));

            Assert.NotNull(typeface);

            var glyph = typeface!.CharacterToGlyphMap['H'];
            var upem = (float)typeface.Metrics.DesignEmHeight;

            // Managed side: the real walked outline (gvar applied at the active coordinates).
            var sink = new SlugContourSink();

            Assert.True(typeface.TryBuildGlyphContours(glyph, Matrix.Identity, sink));

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;

            for (var contour = 0; contour < sink.ContourCount; contour++)
            {
                for (var i = 0; i < sink.GetCurveCount(contour); i++)
                {
                    var curve = sink.GetCurve(contour, i);

                    minX = Math.Min(minX, Math.Min(curve.X1, Math.Min(curve.X2, curve.X3)));
                    maxX = Math.Max(maxX, Math.Max(curve.X1, Math.Max(curve.X2, curve.X3)));
                    minY = Math.Min(minY, Math.Min(curve.Y1, Math.Min(curve.Y2, curve.Y3)));
                    maxY = Math.Max(maxY, Math.Max(curve.Y1, Math.Max(curve.Y2, curve.Y3)));
                }
            }

            // Ground truth: Skia's path for the same platform typeface at font-unit scale.
            using var font = new SKFont(skTypeface, upem);

            font.Hinting = SKFontHinting.None;

            using var path = font.GetGlyphPath(glyph);

            Assert.NotNull(path);

            var truth = path!.Bounds;
            var tolerance = upem * 0.02f;

            Assert.True(
                Math.Abs((maxX - minX) - truth.Width) <= tolerance &&
                Math.Abs((maxY - minY) - truth.Height) <= tolerance,
                FormattableString.Invariant(
                    $"wght {weight}: managed H box {maxX - minX:0.0} x {maxY - minY:0.0} vs Skia {truth.Width:0.0} x {truth.Height:0.0} (upem {upem})"));
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

            var lightTypeface = GlyphTypeface.TryCreate(new SkiaTypeface(light!, FontSimulations.None))!;
            var boldTypeface = GlyphTypeface.TryCreate(new SkiaTypeface(bold!, FontSimulations.None))!;

            // The instances must differ at the variation layer, not merely at the platform
            // handle: the bold clone carries non-default settings and distinct coordinates.
            Assert.True(boldTypeface.VariationPosition != lightTypeface.VariationPosition,
                "Light and Bold resolved to identical variation settings — the platform-matched weight was not applied.");
        }
    }
}
