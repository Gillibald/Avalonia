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

        [Fact]
        public void Colors_Should_Not_Be_Writable_Through_A_Cast()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White });

            Assert.False(palette.Colors is Color[]);
        }
    }
}
