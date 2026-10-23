using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class IndexedPixelFormatTests
    {
        private static readonly BitmapPalette s_palette = new(new[]
        {
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 255, 0, 0),
            Color.FromArgb(0, 0, 0, 0)
        });

        [Theory]
        [InlineData(nameof(PixelFormats.Indexed2), new byte[] { 0b00_01_10_11, 0b11_10_00_00 })]
        [InlineData(nameof(PixelFormats.Indexed4), new byte[] { 0x01, 0x23, 0x32 })]
        [InlineData(nameof(PixelFormats.Indexed8), new byte[] { 0, 1, 2, 3, 3, 2 })]
        public void Palette_Colors_Should_Map_To_Their_Own_Index_Most_Significant_Bits_First(string formatName, byte[] expected)
        {
            var format = GetFormat(formatName);
            var pixels = new[]
            {
                new Rgba8888Pixel(0, 0, 0, 255), new Rgba8888Pixel(255, 255, 255, 255), new Rgba8888Pixel(255, 0, 0, 255),
                new Rgba8888Pixel(0, 0, 0, 0), new Rgba8888Pixel(0, 0, 0, 0), new Rgba8888Pixel(255, 0, 0, 255)
            };

            var indices = ToIndexed(pixels, format, 50);

            Assert.Equal(expected, indices);
            Assert.Equal(pixels, FromIndexed(indices, pixels.Length, format));
        }

        [Fact]
        public void Indexed1_Should_Pack_Eight_Pixels_Per_Byte_And_Zero_The_Tail()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White });
            var pixels = new Rgba8888Pixel[10];

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = i % 3 == 0 ? new Rgba8888Pixel(255, 255, 255, 255) : new Rgba8888Pixel(0, 0, 0, 255);

            Assert.Equal(new byte[] { 0b1001_0010, 0b0100_0000 }, ToIndexed(pixels, PixelFormats.Indexed1, 0, palette));
        }

        [Theory]
        [InlineData(200, 30, 30, 2)]
        [InlineData(100, 100, 100, 0)]
        [InlineData(180, 180, 180, 1)]
        public void Colors_Should_Take_The_Nearest_Opaque_Entry(byte red, byte green, byte blue, int expected)
        {
            var index = ToIndexed(new[] { new Rgba8888Pixel(red, green, blue, 255) }, PixelFormats.Indexed8, 0)[0];

            Assert.Equal(expected, index);
        }

        [Fact]
        public void Transparent_Entries_Should_Count_As_Distant_From_Opaque_Colors()
        {
            // Black is as close as the transparent entry in RGB, but the transparent entry is
            // 255 away from opaque, so black wins.
            Assert.Equal(0, ToIndexed(new[] { new Rgba8888Pixel(0, 0, 0, 255) }, PixelFormats.Indexed8, 0)[0]);
        }

        [Theory]
        [InlineData(0, 127, 0)]
        [InlineData(50, 127, 3)]
        [InlineData(50, 128, 0)]
        [InlineData(100, 254, 3)]
        public void Pixels_Below_The_Alpha_Threshold_Should_Take_The_Transparent_Entry(double thresholdPercent, byte alpha, int expected)
        {
            var index = ToIndexed(new[] { new Rgba8888Pixel(0, 0, 0, alpha) }, PixelFormats.Indexed8, thresholdPercent)[0];

            Assert.Equal(expected, index);
        }

        [Fact]
        public void Ties_Should_Go_To_The_Lowest_Index()
        {
            var palette = new BitmapPalette(new[] { Color.FromRgb(0, 0, 0), Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255) });

            Assert.Equal(0, ToIndexed(new[] { new Rgba8888Pixel(10, 10, 10, 255) }, PixelFormats.Indexed8, 0, palette)[0]);
        }

        [Fact]
        public void Entries_Beyond_The_Format_Should_Not_Be_Matched()
        {
            // Indexed1 reaches only the first two entries, so red maps to the nearer of black and white.
            Assert.Equal(new byte[] { 0 }, ToIndexed(new[] { new Rgba8888Pixel(255, 0, 0, 255) }, PixelFormats.Indexed1, 0));
        }

        [Fact]
        public void An_Index_Past_The_Palette_Should_Read_As_Opaque_Black()
        {
            Assert.Equal(new[] { new Rgba8888Pixel(0, 0, 0, 255) }, FromIndexed(new byte[] { 9 }, 1, PixelFormats.Indexed8));
        }

        [Fact]
        public void Indexed_Pixels_Should_Convert_To_Wide_Formats()
        {
            using var source = new BitmapMemory(PixelFormats.Indexed8, AlphaFormat.Unpremul, new PixelSize(1, 1));
            using var target = new BitmapMemory(PixelFormats.Rgb48, AlphaFormat.Opaque, new PixelSize(1, 1));

            Marshal.WriteByte(source.Address, 2);

            PixelFormatTranscoder.Transcode(source.Address, source.Size, source.RowBytes, source.Format, source.AlphaFormat,
                target.Address, target.RowBytes, target.Format, target.AlphaFormat, s_palette, null, 0);

            Assert.Equal(-1, Marshal.ReadInt16(target.Address));
            Assert.Equal(0, Marshal.ReadInt16(target.Address, 2));
        }

        [Fact]
        public void Converting_Without_A_Palette_Should_Throw()
        {
            using var source = new BitmapMemory(PixelFormats.Indexed8, AlphaFormat.Unpremul, new PixelSize(1, 1));
            using var target = new BitmapMemory(PixelFormats.Bgra8888, AlphaFormat.Unpremul, new PixelSize(1, 1));

            Assert.Throws<ArgumentException>(() => PixelFormatTranscoder.Transcode(source.Address, source.Size,
                source.RowBytes, source.Format, source.AlphaFormat, target.Address, target.RowBytes, target.Format,
                target.AlphaFormat));

            Assert.Throws<ArgumentException>(() => PixelFormatTranscoder.Transcode(target.Address, target.Size,
                target.RowBytes, target.Format, target.AlphaFormat, source.Address, source.RowBytes, source.Format,
                source.AlphaFormat));
        }

        private static byte[] ToIndexed(Rgba8888Pixel[] pixels, PixelFormat format, double thresholdPercent,
            BitmapPalette? palette = null)
        {
            var size = new PixelSize(pixels.Length, 1);

            using var straight = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);
            using var indexed = new BitmapMemory(format, AlphaFormat.Unpremul, size);

            PixelFormatTranscoder.WriteRow(pixels, straight.Address, straight.Format, AlphaFormat.Unpremul, AlphaFormat.Unpremul);

            PixelFormatTranscoder.Transcode(straight.Address, size, straight.RowBytes, straight.Format, straight.AlphaFormat,
                indexed.Address, indexed.RowBytes, format, AlphaFormat.Unpremul, null, palette ?? s_palette, thresholdPercent);

            var bytes = new byte[(pixels.Length * format.BitsPerPixel + 7) / 8];

            Marshal.Copy(indexed.Address, bytes, 0, bytes.Length);

            return bytes;
        }

        private static Rgba8888Pixel[] FromIndexed(byte[] indices, int width, PixelFormat format)
        {
            var size = new PixelSize(width, 1);

            using var indexed = new BitmapMemory(format, AlphaFormat.Unpremul, size);
            using var straight = new BitmapMemory(PixelFormats.Rgba8888, AlphaFormat.Unpremul, size);

            Marshal.Copy(indices, 0, indexed.Address, indices.Length);

            PixelFormatTranscoder.Transcode(indexed.Address, size, indexed.RowBytes, format, AlphaFormat.Unpremul,
                straight.Address, straight.RowBytes, straight.Format, straight.AlphaFormat, s_palette, null, 0);

            var result = new Rgba8888Pixel[width];

            PixelFormatTranscoder.ReadRow(straight.Address, straight.Format, result);

            return result;
        }

        private static PixelFormat GetFormat(string name)
            => (PixelFormat)typeof(PixelFormats).GetProperty(name)!.GetValue(null)!;
    }
}
