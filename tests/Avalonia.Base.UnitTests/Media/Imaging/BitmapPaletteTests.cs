using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapPaletteTests
    {
        [Fact]
        public void Changing_The_Source_List_Should_Not_Change_The_Palette()
        {
            var colors = new List<Color> { Colors.Black, Colors.White };
            var palette = new BitmapPalette(colors);

            colors[0] = Colors.Red;
            colors.Add(Colors.Blue);

            Assert.Equal(new[] { Colors.Black, Colors.White }, palette.Colors);
        }

        // Counts and entries are what WIC's IWICPalette::InitializePredefined produces.
        [Theory]
        [InlineData(WellKnownPaletteType.BlackWhite, false, 2)]
        [InlineData(WellKnownPaletteType.BlackWhite, true, 2)]
        [InlineData(WellKnownPaletteType.Halftone8, false, 16)]
        [InlineData(WellKnownPaletteType.Halftone8, true, 17)]
        [InlineData(WellKnownPaletteType.Halftone27, false, 28)]
        [InlineData(WellKnownPaletteType.Halftone64, false, 72)]
        [InlineData(WellKnownPaletteType.Halftone125, false, 126)]
        [InlineData(WellKnownPaletteType.Halftone216, false, 224)]
        [InlineData(WellKnownPaletteType.Halftone252, true, 253)]
        [InlineData(WellKnownPaletteType.Halftone256, true, 256)]
        [InlineData(WellKnownPaletteType.Gray4, true, 4)]
        [InlineData(WellKnownPaletteType.Gray16, false, 16)]
        [InlineData(WellKnownPaletteType.Gray256, false, 256)]
        public void Fixed_Palettes_Should_Have_The_Wic_Entry_Count(WellKnownPaletteType type, bool addTransparentColor, int count)
        {
            var palette = BitmapPalette.CreateWellKnown(type, addTransparentColor);

            Assert.Equal(count, palette.Colors.Count);
            Assert.Equal(type, palette.Type);
        }

        [Fact]
        public void Halftone_Palettes_Should_Order_The_Cube_Red_Slowest_And_Blue_Fastest()
        {
            var halftone252 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone252);

            Assert.Equal(Color.FromRgb(0, 0, 51), halftone252.Colors[1]);
            Assert.Equal(Color.FromRgb(0, 43, 0), halftone252.Colors[6]);
            Assert.Equal(Color.FromRgb(51, 0, 0), halftone252.Colors[42]);
            Assert.Equal(Colors.White, halftone252.Colors[251]);
        }

        [Fact]
        public void Halftone_Palettes_Should_Append_System_Colors_Or_Silver()
        {
            var halftone64 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone64);
            var halftone125 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone125);

            Assert.Equal(Color.FromRgb(192, 192, 192), halftone64.Colors[64]);
            Assert.Equal(Color.FromRgb(0, 128, 128), halftone64.Colors[71]);
            Assert.Equal(Color.FromRgb(192, 192, 192), halftone125.Colors[125]);
            Assert.Equal(Color.FromRgb(64, 0, 0), halftone125.Colors[25]);
        }

        [Fact]
        public void A_Transparent_Entry_Should_Replace_The_Last_Color_Of_A_Full_Palette()
        {
            var halftone256 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone256, true);
            var halftone8 = BitmapPalette.CreateWellKnown(WellKnownPaletteType.Halftone8, true);

            Assert.Equal(default, halftone256.Colors[255]);
            Assert.Equal(Color.FromRgb(255, 255, 170), halftone256.Colors[254]);
            Assert.Equal(default, halftone8.Colors[16]);
            Assert.True(halftone8.HasTransparency);
            Assert.False(BitmapPalette.CreateWellKnown(WellKnownPaletteType.Gray16, true).HasTransparency);
        }

        [Fact]
        public void Only_Fixed_Palettes_Should_Be_Created_By_Type()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => BitmapPalette.CreateWellKnown(WellKnownPaletteType.Custom));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => BitmapPalette.CreateWellKnown(WellKnownPaletteType.Optimal));
        }

        [Fact]
        public void Colors_Should_Not_Be_Writable_Through_A_Cast()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White });

            Assert.False(palette.Colors is Color[]);
        }
    }
}
