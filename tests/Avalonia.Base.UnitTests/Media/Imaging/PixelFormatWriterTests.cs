using System;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class PixelFormatWriterTests
    {
        private static readonly Rgba8888Pixel s_white = new Rgba8888Pixel
        {
            A = 255,
            B = 255,
            G = 255,
            R = 255
        };

        private static readonly Rgba8888Pixel s_black = new Rgba8888Pixel
        {
            A = 255,
            B = 0,
            G = 0,
            R = 0
        };

        [Fact]
        public void Should_Write_Bgr555()
        {
            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Bgr555), 
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Bgr555PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Bgr555PixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255 });
            Assert.Equal(new Rgba8888Pixel { R = 255, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { G = 255 });
            Assert.Equal(new Rgba8888Pixel { G = 255, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { B = 255 });
            Assert.Equal(new Rgba8888Pixel { B = 255, A = 255 }, pixelReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Bgra8888()
        {
            var sourceMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Bgra8888), 
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(3, 1));

            var sourceWriter = new PixelFormatWriter.Bgra8888PixelFormatWriter();
            var sourceReader = new PixelFormatReader.Bgra8888PixelFormatReader();

            sourceWriter.Reset(sourceMemory.Address);
            sourceReader.Reset(sourceMemory.Address);

            sourceWriter.WriteNext(new Rgba8888Pixel { R = 255 });
            Assert.Equal(new Rgba8888Pixel { R = 255 }, sourceReader.ReadNext());

            sourceWriter.WriteNext(new Rgba8888Pixel { G = 255 });
            Assert.Equal(new Rgba8888Pixel { G = 255 }, sourceReader.ReadNext());

            sourceWriter.WriteNext(new Rgba8888Pixel { B = 255 });
            Assert.Equal(new Rgba8888Pixel { B = 255 }, sourceReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Rgba8888()
        {
            var sourceMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Rgba8888),
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Rgba8888PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Rgba8888PixelFormatReader();

            pixelWriter.Reset(sourceMemory.Address);
            pixelReader.Reset(sourceMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255, G = 125, B = 125, A = 125 });
            Assert.Equal(new Rgba8888Pixel { R = 255, G = 125, B = 125, A = 125 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 125, G = 255, B = 125, A = 125 });
            Assert.Equal(new Rgba8888Pixel { R = 125, G = 255, B = 125, A = 125 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 125, G = 125, B = 255, A = 125 });
            Assert.Equal(new Rgba8888Pixel { R = 125, G = 125, B = 255, A = 125 }, pixelReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Rgb24()
        {
            var sourceMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Rgb24),
                Platform.AlphaFormat.Unpremul,
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Rgb24PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Rgb24PixelFormatReader();

            pixelWriter.Reset(sourceMemory.Address);
            pixelReader.Reset(sourceMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255, G = 125, B = 125 });
            Assert.Equal(new Rgba8888Pixel { R = 255, G = 125, B = 125, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 125, G = 255, B = 125 });
            Assert.Equal(new Rgba8888Pixel { R = 125, G = 255, B = 125, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 125, G = 125, B = 255 });
            Assert.Equal(new Rgba8888Pixel { R = 125, G = 125, B = 255, A = 255 }, pixelReader.ReadNext());
        }


        [Fact]
        public void Should_Write_Rgba64()
        {
            var pixels = new[]
            {
                new Rgba8888Pixel { R = 255, G = 125, B = 125, A = 125 },
                new Rgba8888Pixel { R = 125, G = 255, B = 125, A = 125 },
                new Rgba8888Pixel { R = 125, G = 125, B = 255, A = 125 }
            };

            Assert.Equal(pixels, RoundTrip(Platform.PixelFormats.Rgba64, pixels));
        }

        [Fact]
        public void Should_Write_Bgr565()
        {
            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Bgr565),
                Platform.AlphaFormat.Unpremul,
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Bgr565PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Bgr565PixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255 });
            Assert.Equal(new Rgba8888Pixel { R = 255, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { G = 255 });
            Assert.Equal(new Rgba8888Pixel { G = 255, A = 255 }, pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { B = 255 });
            Assert.Equal(new Rgba8888Pixel { B = 255, A = 255 }, pixelReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Gray32Float()
        {
            var pixels = new[]
            {
                new Rgba8888Pixel { R = 255, G = 255, B = 255, A = 255 },
                new Rgba8888Pixel { R = 125, G = 125, B = 125, A = 255 },
                new Rgba8888Pixel { A = 255 }
            };

            Assert.Equal(pixels, RoundTrip(Platform.PixelFormats.Gray32Float, pixels));
        }

        [Fact]
        public void Should_Write_BlackWhite()
        {
            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.BlackWhite),
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.BlackWhitePixelFormatWriter();
            var pixelReader = new PixelFormatReader.BlackWhitePixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(s_white);
            Assert.Equal(s_white, pixelReader.ReadNext());

            pixelWriter.WriteNext(s_black);
            Assert.Equal(s_black, pixelReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Gray2()
        {
            var palette = new[]
            {
                s_black,
                new Rgba8888Pixel
                {
                    A = 255, B = 0x55, G = 0x55, R = 0x55
                },
                new Rgba8888Pixel
                {
                    A = 255, B = 0xAA, G = 0xAA, R = 0xAA
                },
                s_white
            };

            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Gray2),
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Gray2PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Gray2PixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(palette[0]);
            Assert.Equal(palette[0], pixelReader.ReadNext());

            pixelWriter.WriteNext(palette[1]);
            Assert.Equal(palette[1], pixelReader.ReadNext());

            pixelWriter.WriteNext(palette[2]);
            Assert.Equal(palette[2], pixelReader.ReadNext());

            pixelWriter.WriteNext(palette[3]);
            Assert.Equal(palette[3], pixelReader.ReadNext());
        }

        [Fact]
        public void Should_Write_Gray4()
        {
            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Gray4),
                Platform.AlphaFormat.Unpremul,
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Gray4PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Gray4PixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255, G = 255, B = 255 });
            Assert.Equal(GetGray4(new Rgba8888Pixel { R = 255, G = 255, B = 255 }), pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 17, G = 17, B = 17 });
            Assert.Equal(GetGray4(new Rgba8888Pixel { R = 17, G = 17, B = 17 }), pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel());
            Assert.Equal(new Rgba8888Pixel { A = 255 }, pixelReader.ReadNext());
        }

        private static Rgba8888Pixel GetGray4(Rgba8888Pixel pixel)
        {
            var level = (pixel.R * 15 + 127) / 255;
            var value = (byte)(level | (level << 4));

            return new Rgba8888Pixel(value, value, value, 255);
        }

        [Fact]
        public void Should_Write_Gray8()
        {
            var bitmapMemory = new BitmapMemory(
                new Platform.PixelFormat(Platform.PixelFormatEnum.Gray8),
                Platform.AlphaFormat.Unpremul, 
                new PixelSize(10, 10));

            var pixelWriter = new PixelFormatWriter.Gray8PixelFormatWriter();
            var pixelReader = new PixelFormatReader.Gray8PixelFormatReader();

            pixelWriter.Reset(bitmapMemory.Address);
            pixelReader.Reset(bitmapMemory.Address);

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 255, G = 255, B = 255 });
            Assert.Equal(GetGray8(new Rgba8888Pixel { R = 255, G = 255, B = 255 }), pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel { R = 120, G = 120, B = 120 });
            Assert.Equal(GetGray8(new Rgba8888Pixel { R = 120, G = 120, B = 120 }), pixelReader.ReadNext());

            pixelWriter.WriteNext(new Rgba8888Pixel());
            Assert.Equal(GetGray8(new Rgba8888Pixel { A = 255 }), pixelReader.ReadNext());
        }

        private static Rgba8888Pixel GetGray8(Rgba8888Pixel pixel)
            => new(pixel.R, pixel.R, pixel.R, 255);

        [Fact]
        public void Should_Write_Gray16()
        {
            var pixels = new[] { new Rgba8888Pixel { R = 255, G = 255, B = 255 }, new Rgba8888Pixel { R = 120, G = 120, B = 120 }, new Rgba8888Pixel() };

            var expected = new[]
            {
                GetGray16(new Rgba8888Pixel { R = 255, G = 255, B = 255 }),
                GetGray16(new Rgba8888Pixel { R = 120, G = 120, B = 120 }),
                GetGray16(new Rgba8888Pixel { A = 255 })
            };

            Assert.Equal(expected, RoundTrip(Platform.PixelFormats.Gray16, pixels));
        }

        [Fact]
        public void Unpremultiplying_A_Transparent_Pixel_Should_Produce_Transparent_Black()
        {
            var converted = PixelFormatWriter.GetConvertedPixel(new Rgba8888Pixel(10, 20, 30, 0),
                Platform.AlphaFormat.Premul, Platform.AlphaFormat.Unpremul);

            Assert.Equal(new Rgba8888Pixel(0, 0, 0, 0), converted);
        }

        [Fact]
        public void Unpremultiplying_Colour_Above_Alpha_Should_Clamp_To_Full_Intensity()
        {
            var converted = PixelFormatWriter.GetConvertedPixel(new Rgba8888Pixel(200, 150, 120, 100),
                Platform.AlphaFormat.Premul, Platform.AlphaFormat.Unpremul);

            Assert.Equal(new Rgba8888Pixel(255, 255, 255, 100), converted);
        }

        // Expected values are what WIC's format converter produces for Bgra32 to Pbgra32.
        [Theory]
        [InlineData(128, 1, 1)]
        [InlineData(229, 152, 137)]
        [InlineData(51, 128, 26)]
        [InlineData(255, 255, 255)]
        [InlineData(0, 77, 0)]
        public void Premultiplying_Should_Round_To_Nearest_Like_Wic(byte channel, byte alpha, byte expected)
        {
            var converted = PixelFormatWriter.GetConvertedPixel(new Rgba8888Pixel(channel, channel, channel, alpha),
                Platform.AlphaFormat.Unpremul, Platform.AlphaFormat.Premul);

            Assert.Equal(new Rgba8888Pixel(expected, expected, expected, alpha), converted);
        }

        // Expected values are what WIC's format converter produces for Pbgra32 to Bgra32.
        [Theory]
        [InlineData(1, 1, 255)]
        [InlineData(3, 9, 84)]
        [InlineData(1, 17, 15)]
        [InlineData(7, 7, 254)]
        [InlineData(128, 255, 128)]
        public void Unpremultiplying_Should_Match_Wic(byte channel, byte alpha, byte expected)
        {
            var converted = PixelFormatWriter.GetConvertedPixel(new Rgba8888Pixel(channel, channel, channel, alpha),
                Platform.AlphaFormat.Premul, Platform.AlphaFormat.Unpremul);

            Assert.Equal(new Rgba8888Pixel(expected, expected, expected, alpha), converted);
        }

        private static Rgba8888Pixel[] RoundTrip(Platform.PixelFormat format, Rgba8888Pixel[] pixels)
        {
            using var memory = new BitmapMemory(format, Platform.AlphaFormat.Unpremul, new PixelSize(pixels.Length, 1));

            PixelFormatTranscoder.WriteRow(pixels, memory.Address, format, Platform.AlphaFormat.Unpremul,
                Platform.AlphaFormat.Unpremul);

            var result = new Rgba8888Pixel[pixels.Length];

            PixelFormatTranscoder.ReadRow(memory.Address, format, result);

            return result;
        }

        private static Rgba8888Pixel GetGray16(Rgba8888Pixel pixel)
            => new(pixel.R, pixel.R, pixel.R, 255);
    }
}
