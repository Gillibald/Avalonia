using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    // Expected values are what WIC's format converter produces from Bgra32: Rec.709
    // luminance computed in linear light, then encoded back to sRGB.
    public class GrayConversionTests
    {
        [Theory]
        [InlineData(255, 0, 255, 145)]
        [InlineData(128, 128, 0, 124)]
        [InlineData(200, 100, 172, 135)]
        [InlineData(64, 192, 128, 170)]
        [InlineData(255, 255, 0, 247)]
        [InlineData(10, 20, 30, 19)]
        public void Gray8_Should_Use_Linear_Light_Rec709_Luminance(byte red, byte green, byte blue, byte expected)
        {
            Assert.Equal(new[] { expected }, Transcode<byte>(new Rgba8888Pixel(red, green, blue, 255), PixelFormats.Gray8));
        }

        [Theory]
        [InlineData(0, 255, 255, 58981)]
        [InlineData(128, 128, 0, 31774)]
        [InlineData(64, 192, 128, 43763)]
        [InlineData(255, 255, 0, 63410)]
        public void Gray16_Should_Use_Linear_Light_Rec709_Luminance(byte red, byte green, byte blue, int expected)
        {
            Assert.Equal(new[] { (ushort)expected }, Transcode<ushort>(new Rgba8888Pixel(red, green, blue, 255), PixelFormats.Gray16));
        }

        [Theory]
        [InlineData(255, 0, 255, 0.2848)]
        [InlineData(128, 128, 0, 0.20027537)]
        [InlineData(64, 192, 128, 0.40347775)]
        public void Gray32Float_Should_Hold_Linear_Rec709_Luminance(byte red, byte green, byte blue, double expected)
        {
            Assert.Equal(expected, Transcode<float>(new Rgba8888Pixel(red, green, blue, 255), PixelFormats.Gray32Float)[0], 6);
        }

        [Theory]
        [InlineData(nameof(PixelFormats.Gray4), 0x90)]
        [InlineData(nameof(PixelFormats.Gray2), 0x80)]
        [InlineData(nameof(PixelFormats.BlackWhite), 0x80)]
        public void SubByte_Gray_Should_Round_The_Gray_Level(string formatName, byte expected)
        {
            // Gray 145 is 8.53 of 15 levels and 1.71 of 3; above 128 it is white.
            var format = (PixelFormat)typeof(PixelFormats).GetProperty(formatName)!.GetValue(null)!;

            Assert.Equal(expected, Transcode<byte>(new Rgba8888Pixel(255, 0, 255, 255), format)[0]);
        }

        private static T[] Transcode<T>(Rgba8888Pixel pixel, PixelFormat format) where T : unmanaged
        {
            var size = new PixelSize(1, 1);

            using var source = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);
            using var target = new BitmapMemory(format, AlphaFormat.Opaque, size);

            Marshal.Copy(new byte[target.RowBytes], 0, target.Address, target.RowBytes);

            PixelFormatTranscoder.WriteRow(new[] { pixel }, source.Address, source.Format, AlphaFormat.Unpremul, AlphaFormat.Unpremul);
            PixelFormatTranscoder.Transcode(source.Address, size, source.RowBytes, source.Format, source.AlphaFormat,
                target.Address, target.RowBytes, target.Format, target.AlphaFormat);

            var count = Math.Max(1, format.BitsPerPixel / 8 / Marshal.SizeOf<T>());
            var result = new T[count];

            unsafe
            {
                new ReadOnlySpan<T>((void*)target.Address, count).CopyTo(result);
            }

            return result;
        }
    }
}
