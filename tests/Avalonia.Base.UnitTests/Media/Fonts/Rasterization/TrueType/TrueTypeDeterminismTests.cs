using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Pins hinted output bit-exactly: the committed hashes must reproduce on every
    /// platform and architecture, which is the whole determinism contract - integer 26.6
    /// math plus IEEE-exact square roots. A mismatch on another machine is a portability
    /// defect, not test flakiness; a mismatch after an engine change is a behavior change
    /// that needs deliberate re-pinning.
    /// </summary>
    public class TrueTypeDeterminismTests
    {
        [Theory]
        [InlineData('H', 9f, false, "35A0AD64E0674406")]
        [InlineData('H', 16f, false, "068D66B9833EEDA7")]
        [InlineData('o', 16f, false, "5A7A60B47595FB09")]
        [InlineData('g', 16f, true, "7D0B75262D2607C0")]
        [InlineData('x', 12f, false, "9A4742D2F54E6205")]
        [InlineData('M', 24f, true, "48596FE187BE9D5A")]
        public void Hinted_Zones_Match_The_Committed_Hashes(char character, float pixelsPerEm, bool strong, string expected)
        {
            var typeface = SyntheticFont.FromBytes(TestFontFiles.Load("NotoMono-Regular.ttf")).CreateGlyphTypeface();
            var hinter = typeface.GetTrueTypeHinter(
                GlyphMaskKey.QuantizeScale(pixelsPerEm), GlyphMaskMode.Antialiased);

            Assert.NotNull(hinter);
            Assert.True(hinter!.TryHint(
                typeface.CharacterToGlyphMap[character],
                backwardCompatibility: strong ? 0 : 4));

            var actual = HashZone(hinter.Zone!);

            Assert.True(expected == actual, $"hash for '{character}' at {pixelsPerEm}px: {actual}");
        }

        private static string HashZone(TrueTypeZone zone)
        {
            var hash = 0xCBF29CE484222325UL;

            void Mix(int value)
            {
                for (var shift = 0; shift < 32; shift += 8)
                {
                    hash = (hash ^ (byte)(value >> shift)) * 0x100000001B3UL;
                }
            }

            Mix(zone.PointCount);
            Mix(zone.ContourCount);

            for (var i = 0; i < zone.PointCount; i++)
            {
                Mix(zone.CurX[i]);
                Mix(zone.CurY[i]);
                Mix(zone.OrgX[i]);
                Mix(zone.OrgY[i]);
                Mix(zone.Tags[i]);
            }

            return hash.ToString("X16");
        }
    }
}
