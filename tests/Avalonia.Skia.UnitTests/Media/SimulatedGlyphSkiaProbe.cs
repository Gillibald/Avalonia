using System;
using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: compares simulated ink bounds and advances against what Skia's fake
    /// bold and skew draw at a few sizes, reporting glyphs whose Skia path escapes the reported box
    /// or whose Skia advance changes. Skia is not the specification for either. Env-gated, not
    /// part of the suite.</summary>
    public class SimulatedGlyphSkiaProbe
    {
        private const string DejaVuSansUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.DejaVuSans.ttf?assembly=Avalonia.Skia.UnitTests";

        private const string SourceCodeProUri =
            "resm:Avalonia.Skia.UnitTests.Fonts.SourceCodePro-Subset.otf?assembly=Avalonia.Skia.UnitTests";

        [Fact]
        public void Compare_Simulated_Glyphs_With_Skia()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("SIMULATED_GLYPH_SKIA_PROBE") != "1", "probe");

            var report = new List<string>();

            foreach (var uri in new[] { DejaVuSansUri, SourceCodeProUri })
            {
                var root = SimulatedGlyphMetricsTests.Load(uri);
                var glyphs = SimulatedGlyphMetricsTests.GetMappedGlyphs(root);

                foreach (var simulations in new[]
                         {
                             FontSimulations.Bold, FontSimulations.Oblique,
                             FontSimulations.Bold | FontSimulations.Oblique
                         })
                {
                    foreach (var size in new[] { 12f, 36f, 100f })
                    {
                        CompareBounds(root, glyphs, size, simulations, report);
                    }

                    CompareAdvances(root, glyphs, simulations, report);
                }
            }

            Assert.Fail(report.Count == 0 ? "no differences" : string.Join(Environment.NewLine, report));
        }

        private static void CompareBounds(GlyphTypeface root, ushort[] glyphs, float size,
            FontSimulations simulations, List<string> report)
        {
            const double tolerance = 0.5;

            var variant = root.WithSimulations(simulations);
            var bounds = new GlyphBounds[glyphs.Length];

            Assert.True(variant.TryGetGlyphBounds(glyphs, bounds));

            var scale = size / root.Metrics.DesignEmHeight;

            using var skiaTypeface = (SkiaTypeface)new PlatformRenderInterface().CreateTypeface(root);
            using var font = skiaTypeface.CreateSKFont(size, simulations);

            for (var i = 0; i < glyphs.Length; i++)
            {
                using var path = font.GetGlyphPath(glyphs[i]);
                var drawn = path?.TightBounds ?? SKRect.Empty;

                if (drawn.IsEmpty)
                {
                    continue;
                }

                // Glyph bounds are y-up design units; Skia's are y-down pixels.
                var left = bounds[i].XMin * scale;
                var right = bounds[i].XMax * scale;
                var top = -bounds[i].YMax * scale;
                var bottom = -bounds[i].YMin * scale;

                if (left > drawn.Left + tolerance || right < drawn.Right - tolerance ||
                    top > drawn.Top + tolerance || bottom < drawn.Bottom - tolerance)
                {
                    report.Add($"{root.FamilyName} {simulations} {size}px glyph {glyphs[i]}: reported " +
                               $"({left:F2}, {top:F2}, {right:F2}, {bottom:F2}) Skia " +
                               $"({drawn.Left:F2}, {drawn.Top:F2}, {drawn.Right:F2}, {drawn.Bottom:F2})");
                }
            }
        }

        private static void CompareAdvances(GlyphTypeface root, ushort[] glyphs, FontSimulations simulations,
            List<string> report)
        {
            var plain = new float[glyphs.Length];
            var simulated = new float[glyphs.Length];

            using var skiaTypeface = (SkiaTypeface)new PlatformRenderInterface().CreateTypeface(root);

            using (var font = skiaTypeface.CreateSKFont(36, FontSimulations.None))
            {
                font.GetGlyphWidths(glyphs, plain, null);
            }

            using (var font = skiaTypeface.CreateSKFont(36, simulations))
            {
                font.GetGlyphWidths(glyphs, simulated, null);
            }

            for (var i = 0; i < glyphs.Length; i++)
            {
                if (plain[i] != simulated[i])
                {
                    report.Add($"{root.FamilyName} {simulations} glyph {glyphs[i]}: Skia advance " +
                               $"{plain[i]} -> {simulated[i]}");
                }
            }
        }
    }
}
