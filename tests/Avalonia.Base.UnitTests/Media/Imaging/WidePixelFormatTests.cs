using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class WidePixelFormatTests
    {
        public static TheoryData<string> ColorFormats { get; } = new()
        {
            nameof(PixelFormats.Rgb48),
            nameof(PixelFormats.Rgba64),
            nameof(PixelFormats.Bgra64),
            nameof(PixelFormats.Rgb48Half),
            nameof(PixelFormats.Rgba64Half),
            nameof(PixelFormats.Rgb128Float),
            nameof(PixelFormats.Rgba128Float),
            nameof(PixelFormats.Bgr101010),
            nameof(PixelFormats.Bgr48Linear),
            nameof(PixelFormats.Bgra64Linear)
        };

        [Theory]
        [MemberData(nameof(ColorFormats))]
        public void Every_8Bit_Value_Should_Survive_A_Round_Trip(string formatName)
        {
            var format = GetFormat(formatName);
            var source = new Rgba8888Pixel[256];

            for (var i = 0; i < source.Length; i++)
            {
                var alpha = format.HasAlpha ? (byte)(255 - i) : (byte)255;

                source[i] = new Rgba8888Pixel((byte)i, (byte)(255 - i), (byte)(i ^ 0x5A), alpha);
            }

            Assert.Equal(source, RoundTrip(source, format, AlphaFormat.Unpremul));
        }

        [Theory]
        [InlineData(nameof(PixelFormats.Gray16))]
        [InlineData(nameof(PixelFormats.Gray32Float))]
        public void Every_8Bit_Gray_Value_Should_Survive_A_Round_Trip(string formatName)
        {
            var source = new Rgba8888Pixel[256];

            for (var i = 0; i < source.Length; i++)
                source[i] = new Rgba8888Pixel((byte)i, (byte)i, (byte)i, 255);

            Assert.Equal(source, RoundTrip(source, GetFormat(formatName), AlphaFormat.Opaque));
        }

        // Expected values are what WIC's format converter produces from Bgra32.
        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 257)]
        [InlineData(128, 32896)]
        [InlineData(254, 65278)]
        [InlineData(255, 65535)]
        public void Widening_To_16_Bits_Should_Replicate_The_Byte(byte value, int expected)
        {
            var words = Transcode<ushort>(new Rgba8888Pixel(value, value, value, 255), PixelFormats.Rgb48, AlphaFormat.Opaque);

            Assert.Equal(new[] { (ushort)expected, (ushort)expected, (ushort)expected }, words);
        }

        // Expected values are what WIC's format converter produces from Bgra32.
        [Theory]
        [InlineData(1, 0.0003035269910469651)]
        [InlineData(128, 0.2158605009317398)]
        [InlineData(129, 0.21952618658542633)]
        [InlineData(254, 0.9911020994186401)]
        [InlineData(255, 1.0)]
        public void Floating_Point_Formats_Should_Hold_Linear_Light(byte value, double expected)
        {
            var channels = Transcode<float>(new Rgba8888Pixel(value, 0, 0, 255), PixelFormats.Rgba128Float, AlphaFormat.Unpremul);

            Assert.Equal(expected, channels[0], 6);
            Assert.Equal(0, channels[1]);
            Assert.Equal(1, channels[3]);
        }

        // Expected values are what WIC's format converter produces from Bgra32: each ten-bit
        // channel replicates the byte's top bits, and the two unused bits are set.
        [Theory]
        [InlineData(0, 0u)]
        [InlineData(43, 172u)]
        [InlineData(128, 514u)]
        [InlineData(255, 1023u)]
        public void Bgr101010_Should_Replicate_The_Byte_Like_Wic(byte value, uint expected)
        {
            var word = Transcode<uint>(new Rgba8888Pixel(value, 0, 0, 255), PixelFormats.Bgr101010, AlphaFormat.Opaque)[0];

            Assert.Equal(expected, (word >> 20) & 0x3FF);
            Assert.Equal(0u, word & 0xFFFFF);
            Assert.Equal(3u, word >> 30);
        }

        [Theory]
        [InlineData(255, 8192)]
        [InlineData(128, 1768)]
        [InlineData(1, 2)]
        [InlineData(0, 0)]
        public void Linear_Fixed_Point_Should_Hold_Linear_Light_With_8192_For_One(byte value, short expected)
        {
            var samples = Transcode<short>(new Rgba8888Pixel(value, 0, 0, 255), PixelFormats.Bgra64Linear, AlphaFormat.Unpremul);

            // Blue, green, red, alpha.
            Assert.Equal(new short[] { 0, 0, expected, 8192 }, samples);
        }

        [Fact]
        public void Premultiplied_Linear_Fixed_Point_Should_Associate_Alpha_In_Linear_Light()
        {
            var samples = Transcode<short>(new Rgba8888Pixel(255, 0, 0, 128), PixelFormats.Bgra64Linear, AlphaFormat.Premul);

            Assert.Equal(new short[] { 0, 0, 4112, 4112 }, samples);
        }

        [Theory]
        [InlineData(255, 0, 0, 255, 0xFC00)]
        [InlineData(255, 0, 0, 0, 0x7C00)]
        [InlineData(0, 255, 0, 128, 0x83E0)]
        [InlineData(0, 0, 255, 127, 0x001F)]
        public void Bgra5551_Should_Pack_Five_Bit_Color_And_One_Bit_Alpha(byte r, byte g, byte b, byte a, int expected)
        {
            var word = Transcode<ushort>(new Rgba8888Pixel(r, g, b, a), PixelFormats.Bgra5551, AlphaFormat.Unpremul)[0];

            Assert.Equal((ushort)expected, word);
            Assert.Equal(new[] { new Rgba8888Pixel(r, g, b, a >= 128 ? (byte)255 : (byte)0) },
                RoundTrip(new[] { new Rgba8888Pixel(r, g, b, a) }, PixelFormats.Bgra5551, AlphaFormat.Unpremul));
        }

        [Fact]
        public void Premultiplied_Floating_Point_Should_Associate_Alpha_In_Linear_Light()
        {
            var channels = Transcode<float>(new Rgba8888Pixel(255, 128, 0, 102), PixelFormats.Rgba128Float, AlphaFormat.Premul);

            Assert.Equal(0.4f, channels[0], 5);
            Assert.Equal(0.2158605f * 0.4f, channels[1], 5);
            Assert.Equal(0, channels[2]);
            Assert.Equal(0.4f, channels[3], 5);
        }

        [Fact]
        public void Premultiplied_16_Bit_Should_Round_Trip_Straight_Pixels()
        {
            var source = new[] { new Rgba8888Pixel(200, 100, 50, 128), new Rgba8888Pixel(10, 20, 30, 0) };

            var result = RoundTrip(source, PixelFormats.Rgba64, AlphaFormat.Premul);

            Assert.Equal(source[0], result[0]);
            Assert.Equal(new Rgba8888Pixel(0, 0, 0, 0), result[1]);
        }

        [Theory]
        [InlineData(255, 255, 255, 0, 0, 0, 0)]
        [InlineData(0, 0, 0, 0, 0, 0, 255)]
        [InlineData(255, 0, 0, 0, 255, 255, 0)]
        [InlineData(128, 64, 0, 0, 128, 255, 127)]
        public void Cmyk_Should_Use_The_Device_Independent_Complement(byte red, byte green, byte blue,
            byte cyan, byte magenta, byte yellow, byte black)
        {
            var ink = Transcode<byte>(new Rgba8888Pixel(red, green, blue, 255), PixelFormats.Cmyk32, AlphaFormat.Opaque);

            Assert.Equal(new[] { cyan, magenta, yellow, black }, ink);
            Assert.Equal(new[] { new Rgba8888Pixel(red, green, blue, 255) },
                RoundTrip(new[] { new Rgba8888Pixel(red, green, blue, 255) }, PixelFormats.Cmyk32, AlphaFormat.Opaque));
        }

        [Fact]
        public void The_8Bit_Row_Should_Read_And_Write_Wide_Formats()
        {
            var size = new PixelSize(2, 1);

            using var memory = new BitmapMemory(PixelFormats.Rgb48, AlphaFormat.Opaque, size);

            var row = new[] { new Rgba8888Pixel(1, 2, 3, 255), new Rgba8888Pixel(250, 251, 252, 255) };

            PixelFormatTranscoder.WriteRow(row, memory.Address, PixelFormats.Rgb48, AlphaFormat.Opaque, AlphaFormat.Opaque);

            Assert.Equal(257, Marshal.ReadInt16(memory.Address));

            var result = new Rgba8888Pixel[2];

            PixelFormatTranscoder.ReadRow(memory.Address, PixelFormats.Rgb48, result);

            Assert.Equal(row, result);
        }

        private static Rgba8888Pixel[] RoundTrip(Rgba8888Pixel[] source, PixelFormat format, AlphaFormat alphaFormat)
        {
            var size = new PixelSize(source.Length, 1);

            using var straight = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);
            using var wide = new BitmapMemory(format, alphaFormat, size);
            using var back = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);

            PixelFormatTranscoder.WriteRow(source, straight.Address, straight.Format, AlphaFormat.Unpremul, AlphaFormat.Unpremul);

            Transcode(straight, wide);
            Transcode(wide, back);

            var result = new Rgba8888Pixel[source.Length];

            PixelFormatTranscoder.ReadRow(back.Address, back.Format, result);

            return result;
        }

        private static T[] Transcode<T>(Rgba8888Pixel pixel, PixelFormat format, AlphaFormat alphaFormat) where T : unmanaged
        {
            var size = new PixelSize(1, 1);

            using var straight = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);
            using var target = new BitmapMemory(format, alphaFormat, size);

            PixelFormatTranscoder.WriteRow(new[] { pixel }, straight.Address, straight.Format, AlphaFormat.Unpremul, AlphaFormat.Unpremul);

            Transcode(straight, target);

            var count = format.BitsPerPixel / 8 / Marshal.SizeOf<T>();
            var result = new T[count];

            unsafe
            {
                new ReadOnlySpan<T>((void*)target.Address, count).CopyTo(result);
            }

            return result;
        }

        private static void Transcode(BitmapMemory source, BitmapMemory destination)
            => PixelFormatTranscoder.Transcode(source.Address, source.Size, source.RowBytes, source.Format,
                source.AlphaFormat, destination.Address, destination.RowBytes, destination.Format, destination.AlphaFormat);

        private static PixelFormat GetFormat(string name)
            => (PixelFormat)typeof(PixelFormats).GetProperty(name)!.GetValue(null)!;
    }
}
