using System.Linq;
using System.Reflection;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests
{
    public class PixelFormatTests
    {
        [Theory]
        [InlineData(nameof(PixelFormats.Rgb565), 16, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgba8888), 32, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgba64), 64, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgra8888), 32, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.BlackWhite), 1, 1, false, false, true, false, false)]
        [InlineData(nameof(PixelFormats.Gray2), 2, 1, false, false, true, false, false)]
        [InlineData(nameof(PixelFormats.Gray4), 4, 1, false, false, true, false, false)]
        [InlineData(nameof(PixelFormats.Gray8), 8, 1, false, false, true, false, false)]
        [InlineData(nameof(PixelFormats.Gray16), 16, 1, false, false, true, false, false)]
        [InlineData(nameof(PixelFormats.Gray32Float), 32, 1, false, false, true, true, false)]
        [InlineData(nameof(PixelFormats.Rgb24), 24, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgb32), 32, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgr24), 24, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgr32), 32, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgr555), 16, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgr565), 16, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Indexed1), 1, 1, false, true, false, false, false)]
        [InlineData(nameof(PixelFormats.Indexed2), 2, 1, false, true, false, false, false)]
        [InlineData(nameof(PixelFormats.Indexed4), 4, 1, false, true, false, false, false)]
        [InlineData(nameof(PixelFormats.Indexed8), 8, 1, false, true, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgb48), 48, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgra64), 64, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgba64Half), 64, 4, true, false, false, true, false)]
        [InlineData(nameof(PixelFormats.Rgb48Half), 48, 3, false, false, false, true, false)]
        [InlineData(nameof(PixelFormats.Rgba128Float), 128, 4, true, false, false, true, false)]
        [InlineData(nameof(PixelFormats.Rgb128Float), 128, 3, false, false, false, true, false)]
        [InlineData(nameof(PixelFormats.Cmyk32), 32, 4, false, false, false, false, true)]
        [InlineData(nameof(PixelFormats.Cmyk64), 64, 4, false, false, false, false, true)]
        [InlineData(nameof(PixelFormats.Bgr101010), 32, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgra5551), 16, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgr48Linear), 48, 3, false, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Bgra64Linear), 64, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Rgba64Linear), 64, 4, true, false, false, false, false)]
        [InlineData(nameof(PixelFormats.Cmyka40), 40, 5, true, false, false, false, true)]
        [InlineData(nameof(PixelFormats.Cmyka80), 80, 5, true, false, false, false, true)]
        [InlineData(nameof(PixelFormats.Rgb96Float), 96, 3, false, false, false, true, false)]
        public void Format_Should_Describe_Its_Layout(string name, int bitsPerPixel, int channelCount,
            bool hasAlpha, bool isIndexed, bool isGrayscale, bool isFloat, bool isCmyk)
        {
            var format = GetFormat(name);

            Assert.Equal(name, format.ToString());
            Assert.Equal(bitsPerPixel, format.BitsPerPixel);
            Assert.Equal(channelCount, format.ChannelCount);
            Assert.Equal(hasAlpha, format.HasAlpha);
            Assert.Equal(isIndexed, format.IsIndexed);
            Assert.Equal(isGrayscale, format.IsGrayscale);
            Assert.Equal(isFloat, format.IsFloat);
            Assert.Equal(isCmyk, format.IsCmyk);
        }

        [Fact]
        public void Every_Format_Should_Be_Described()
        {
            var described = typeof(PixelFormatTests)
                .GetMethod(nameof(Format_Should_Describe_Its_Layout))!
                .GetCustomAttributes<InlineDataAttribute>()
                .Select(data => (string)data.Data[0]!)
                .OrderBy(name => name);

            var declared = typeof(PixelFormats)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Select(property => property.Name)
                .OrderBy(name => name);

            Assert.Equal(declared, described);
        }

        // The remote protocol and the macOS framebuffer pass these values across process
        // and native boundaries as integers.
        [Theory]
        [InlineData(nameof(PixelFormats.Rgb565), 0)]
        [InlineData(nameof(PixelFormats.Rgba8888), 1)]
        [InlineData(nameof(PixelFormats.Bgra8888), 2)]
        [InlineData(nameof(PixelFormats.BlackWhite), 3)]
        [InlineData(nameof(PixelFormats.Gray2), 4)]
        [InlineData(nameof(PixelFormats.Gray4), 5)]
        [InlineData(nameof(PixelFormats.Gray8), 6)]
        [InlineData(nameof(PixelFormats.Gray16), 7)]
        [InlineData(nameof(PixelFormats.Gray32Float), 8)]
        [InlineData(nameof(PixelFormats.Rgba64), 9)]
        [InlineData(nameof(PixelFormats.Rgb24), 10)]
        [InlineData(nameof(PixelFormats.Rgb32), 11)]
        [InlineData(nameof(PixelFormats.Bgr24), 12)]
        [InlineData(nameof(PixelFormats.Bgr32), 13)]
        [InlineData(nameof(PixelFormats.Bgr555), 14)]
        [InlineData(nameof(PixelFormats.Bgr565), 15)]
        public void Existing_Formats_Should_Keep_Their_Numeric_Values(string name, int value)
        {
            Assert.Equal(value, (int)GetFormat(name).FormatEnum);
        }

        private static PixelFormat GetFormat(string name)
            => (PixelFormat)typeof(PixelFormats).GetProperty(name)!.GetValue(null)!;
    }
}
