using System;
using System.IO;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Bi-level text runs the font's programs with full interpretation on both axes, the
    /// classic engine FreeType ships as its v35 interpreter. The expected values are FreeType
    /// 2.13.2's v35 FT_LOAD_TARGET_MONO on-curve x extremes for the same glyphs; Segoe UI
    /// branches on GETINFO, so it reads which engine the interpreter reports.
    /// </summary>
    public class BiLevelHintingParityTests
    {
        [Theory]
        [InlineData("segoeui.ttf", 9, 'm', 1.0, 6.0)]
        [InlineData("segoeui.ttf", 12, 'm', 1.0, 10.0)]
        [InlineData("segoeui.ttf", 12, 'e', 1.0, 5.0)]
        [InlineData("georgia.ttf", 16, '1', 2.0, 7.0)]
        [InlineData("georgia.ttf", 16, 'H', 1.0, 11.0)]
        public void Bi_Level_Hinting_Matches_FreeType_V35_Mono(string fontFile, int pixelsPerEm, char character,
            double expectedLeft, double expectedRight)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Relies on the Windows-shipped fonts.");

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fontFile);

            Assert.SkipWhen(!File.Exists(path), $"{fontFile} is not installed.");

            var typeface = SyntheticFont.FromBytes(File.ReadAllBytes(path)).CreateGlyphTypeface();
            var glyph = typeface.CharacterToGlyphMap[character];
            var hinter = typeface.GetTrueTypeHinter(GlyphMaskKey.QuantizeScale(pixelsPerEm), GlyphMaskMode.Aliased);

            Assert.NotNull(hinter);

            var rented = hinter!.Rent();

            try
            {
                Assert.True(rented.TryHint(glyph, backwardCompatibility: 0));

                var zone = rented.Zone!;
                var left = int.MaxValue;
                var right = int.MinValue;

                for (var i = 0; i < zone.PointCount - 4; i++)
                {
                    if ((zone.Tags[i] & 1) != 0)
                    {
                        left = Math.Min(left, zone.CurX[i]);
                        right = Math.Max(right, zone.CurX[i]);
                    }
                }

                Assert.Equal(expectedLeft, left / 64.0, 3);
                Assert.Equal(expectedRight, right / 64.0, 3);
            }
            finally
            {
                hinter.Return(rented);
            }
        }
    }
}
