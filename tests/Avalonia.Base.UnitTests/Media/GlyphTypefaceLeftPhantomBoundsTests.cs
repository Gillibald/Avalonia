using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    /// <summary>
    /// glyf ink bounds follow the outline, which is placed so that its left phantom point lands on
    /// the origin: an hmtx left side bearing that differs from xMin, or a gvar delta on the left
    /// phantom point, moves the box.
    /// </summary>
    public class GlyphTypefaceLeftPhantomBoundsTests
    {
        private static readonly OpenTypeTag s_wght = OpenTypeTag.Parse("wght");

        [Fact]
        public void Bounds_Are_Shifted_By_A_Left_Side_Bearing_That_Differs_From_xMin()
        {
            var original = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);
            var glyph = original.CreateGlyphTypeface().CharacterToGlyphMap['H'];

            var shifted = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).AddToLeftSideBearing(glyph, 40);

            AssertShiftedBy(40, original.CreateGlyphTypeface(), shifted.CreateGlyphTypeface(), glyph);
        }

        [Fact]
        public void Composite_Bounds_Are_Shifted_By_Its_Use_My_Metrics_Component_Left_Phantom_Point()
        {
            var original = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular);
            var map = original.CreateGlyphTypeface().CharacterToGlyphMap;

            var shifted = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).AddToLeftSideBearing(map['A'], 40);

            AssertShiftedBy(40, original.CreateGlyphTypeface(), shifted.CreateGlyphTypeface(), map['Á']);
        }

        [Fact]
        public void Varied_Bounds_Are_Shifted_By_The_gvar_Delta_Of_Their_Left_Phantom_Point()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterVariable).Remove("HVAR");
            var glyph = font.CreateGlyphTypeface().CharacterToGlyphMap['H'];

            var typeface = font.WithLeftPhantomGvar(glyph, axis: 1, delta: -30).CreateGlyphTypeface();

            AssertShiftedBy(30, typeface, AtWeight900(typeface), glyph);
        }

        [Fact]
        public void Left_Phantom_gvar_Delta_Is_Ignored_When_The_Font_Has_Hvar()
        {
            // With HVAR, FreeType drops the gvar deltas of the horizontal phantom points.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterVariable);
            var glyph = font.CreateGlyphTypeface().CharacterToGlyphMap['H'];

            var typeface = font.WithLeftPhantomGvar(glyph, axis: 1, delta: -30).CreateGlyphTypeface();

            AssertShiftedBy(0, typeface, AtWeight900(typeface), glyph);
        }

        private static GlyphTypeface AtWeight900(GlyphTypeface typeface)
            => typeface.WithVariation(typeface.CreateNormalizedPosition(new FontVariationSettings(
                new[] { new FontVariation(s_wght, 900) })));

        private static void AssertShiftedBy(int expected, GlyphTypeface original, GlyphTypeface shifted, ushort glyph)
        {
            var originalBox = Bounds(original, glyph);
            var shiftedBox = Bounds(shifted, glyph);

            Assert.Equal(originalBox with { XMin = (short)(originalBox.XMin + expected), XMax = (short)(originalBox.XMax + expected) },
                shiftedBox);

            // Every metrics path reports the same box.
            Assert.True(shifted.TryGetGlyphMetrics(glyph, out var metrics));
            Assert.Equal(shiftedBox.XMin, metrics.XBearing);
            Assert.Equal(shiftedBox.YMax, metrics.YBearing);

            var batchMetrics = new GlyphMetrics[1];
            Assert.True(shifted.TryGetGlyphMetrics(new[] { glyph }, batchMetrics));
            Assert.Equal(shiftedBox.XMin, batchMetrics[0].XBearing);

            var batchBounds = new GlyphBounds[1];
            Assert.True(shifted.TryGetGlyphBounds(new[] { glyph }, batchBounds));
            Assert.Equal(shiftedBox, batchBounds[0]);
        }

        private static GlyphBounds Bounds(GlyphTypeface typeface, ushort glyph)
        {
            Assert.True(typeface.TryGetGlyphInkBounds(glyph, out var box));
            return box;
        }
    }
}
