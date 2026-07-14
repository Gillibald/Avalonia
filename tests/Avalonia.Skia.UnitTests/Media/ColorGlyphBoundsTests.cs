using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A color glyph's ink is its layer union (COLR v0) or paint-graph extent (COLR v1), not
    /// its base outline — a managed run that declares base-outline bounds under-invalidates
    /// and clips color glyphs on partial redraws. The backend run impl (Skia measures color
    /// extents) is the ground truth the managed bounds must contain. Skips without the
    /// Windows-shipped Segoe UI Emoji.
    /// </summary>
    public class ColorGlyphBoundsTests
    {
        [Fact]
        public void Managed_Run_Bounds_Contain_The_Backend_Color_Ink()
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped Segoe UI Emoji.");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI Emoji", SKFontStyle.Normal);

            Assert.SkipWhen(skTypeface is null || !skTypeface.FamilyName.Contains("Emoji"),
                "Segoe UI Emoji is not installed.");

            var typeface = GlyphTypeface.TryCreate(new SkiaTypeface(skTypeface!, FontSimulations.None));

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

                using var managed = new ManagedGlyphRunImpl(typeface, 64, infos, new Point(0, 0));
                using var backend = new GlyphRunImpl(typeface, 64, infos, new Point(0, 0));

                checked_++;

                // The backend bounds are Skia's measurement of the color extents; the managed
                // declaration must cover them (small deflation absorbs conservative padding).
                var truth = backend.Bounds.Deflate(1.5);

                if (!managed.Bounds.Contains(truth))
                {
                    failures.Add(FormattableString.Invariant(
                        $"U+{codepoint:X}: managed {managed.Bounds} does not contain backend {backend.Bounds}"));
                }
            }

            Assert.True(checked_ >= 4, "Too few emoji resolved to glyphs for a meaningful check.");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
    }
}
