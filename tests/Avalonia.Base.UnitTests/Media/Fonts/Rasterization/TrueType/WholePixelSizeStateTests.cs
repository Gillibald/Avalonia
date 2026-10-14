using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The size state of a font that sets head.flags bit 3 ("force ppem to integer values") is
    /// the rounded ppem's, so its programs run against the outline and control values of the
    /// size MPPEM reports. Without the bit the size state keeps the fractional scale.
    /// </summary>
    public class WholePixelSizeStateTests
    {
        private const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private static GlyphTypeface CreateNoto(bool forcesIntegerPpem)
        {
            var font = SyntheticFont.FromAsset(NotoMonoUri);
            var flags = (ushort)(font.GetTable("head")[16] << 8 | font.GetTable("head")[17]);

            Assert.NotEqual(0, flags & 8);

            if (!forcesIntegerPpem)
            {
                font.PatchUInt16("head", 16, (ushort)(flags & ~8));
            }

            return font.CreateGlyphTypeface();
        }

        [Fact]
        public void Every_Eighth_Of_One_Rounded_Ppem_Shares_The_Rounded_Size_State()
        {
            var typeface = CreateNoto(forcesIntegerPpem: true);
            var whole = typeface.GetTrueTypeHinter(128, GlyphMaskMode.Antialiased);

            Assert.NotNull(whole);

            // 16 px at 2048 units per em: 16 * 64 * 65536 / 2048.
            Assert.Equal(32768, whole!.State.Scale);

            // 15.5 px rounds up and 16.375 px rounds down to 16 px.
            for (ushort scaleQ = 124; scaleQ < 132; scaleQ++)
            {
                Assert.Equal(128, typeface.GetHintedScaleQ(scaleQ));
                Assert.Same(whole, typeface.GetTrueTypeHinter(scaleQ, GlyphMaskMode.Antialiased));
            }

            Assert.Equal(120, typeface.GetHintedScaleQ(123));
        }

        [Fact]
        public void A_Font_Without_The_Flag_Hints_At_The_Fractional_Size()
        {
            var typeface = CreateNoto(forcesIntegerPpem: false);

            Assert.True(typeface.HasTrueTypeHinting);
            Assert.False(typeface.HintsAtWholePixelsPerEm);
            Assert.Equal(123, typeface.GetHintedScaleQ(123));

            var hinter = typeface.GetTrueTypeHinter(123, GlyphMaskMode.Antialiased);

            Assert.NotNull(hinter);

            // 15.375 px: 984 / 64 * 65536 / 2048.
            Assert.Equal(31488, hinter!.State.Scale);
            Assert.NotSame(hinter, typeface.GetTrueTypeHinter(120, GlyphMaskMode.Antialiased));
        }

        [Theory]
        [InlineData(1, 8)]
        [InlineData(3, 8)]
        [InlineData(4, 8)]
        [InlineData(11, 8)]
        [InlineData(12, 16)]
        [InlineData(65535, 65528)]
        public void Rounding_Follows_The_Integer_Ppem_Rounding(int scaleQ, int expected)
        {
            Assert.Equal(expected, GlyphTypeface.RoundToWholePixelsPerEm((ushort)scaleQ));
        }
    }
}
