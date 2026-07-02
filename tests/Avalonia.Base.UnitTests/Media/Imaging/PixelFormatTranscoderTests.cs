using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class PixelFormatTranscoderTests
    {
        [Fact]
        public void Should_Transcode()
        {
            var sourceMemory = CreateBitmapMemory();

            var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Opaque, sourceMemory.Size);

            PixelFormatTranscoder.Transcode(
                sourceMemory.Address,
                sourceMemory.Size,
                sourceMemory.RowBytes,
                sourceMemory.Format,
                sourceMemory.AlphaFormat,
                destMemory.Address,
                destMemory.RowBytes,
                destMemory.Format,
                destMemory.AlphaFormat);

            var reader = new PixelFormatReader.Bgra8888PixelFormatReader();

            reader.Reset(destMemory.Address);

            Assert.Equal(new Rgba8888Pixel(255, 0, 0, 0), reader.ReadNext());
            Assert.Equal(new Rgba8888Pixel(0, 255, 0, 0), reader.ReadNext());
            Assert.Equal(new Rgba8888Pixel(0, 0, 255, 0), reader.ReadNext());
        }

        [Fact]
        public void Should_Transcode_Rows_Larger_Than_The_Stack_Staging_Buffer()
        {
            var size = new PixelSize(300, 3);

            using var sourceMemory = new BitmapMemory(PixelFormat.Rgba8888, AlphaFormat.Unpremul, size);
            using var destMemory = new BitmapMemory(PixelFormat.Bgra8888, AlphaFormat.Unpremul, size);

            var row = new Rgba8888Pixel[size.Width];

            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    row[x] = new Rgba8888Pixel((byte)x, (byte)y, (byte)(x ^ y), 255);
                }

                PixelFormatTranscoder.WriteRow(row, sourceMemory.Address + y * sourceMemory.RowBytes,
                    sourceMemory.Format, sourceMemory.AlphaFormat, sourceMemory.AlphaFormat);
            }

            PixelFormatTranscoder.Transcode(
                sourceMemory.Address,
                size,
                sourceMemory.RowBytes,
                sourceMemory.Format,
                sourceMemory.AlphaFormat,
                destMemory.Address,
                destMemory.RowBytes,
                destMemory.Format,
                destMemory.AlphaFormat);

            for (var y = 0; y < size.Height; y++)
            {
                PixelFormatTranscoder.ReadRow(destMemory.Address + y * destMemory.RowBytes, destMemory.Format, row);

                for (var x = 0; x < size.Width; x++)
                {
                    Assert.Equal(new Rgba8888Pixel((byte)x, (byte)y, (byte)(x ^ y), 255), row[x]);
                }
            }
        }

        [Fact]
        public void Should_Start_Each_SubByte_Destination_Row_At_A_Fresh_Byte()
        {
            var size = new PixelSize(3, 2);

            using var sourceMemory = new BitmapMemory(PixelFormat.Rgba8888, AlphaFormat.Opaque, size);
            using var destMemory = new BitmapMemory(new PixelFormat(PixelFormatEnum.Gray2), AlphaFormat.Opaque, size);

            // The Gray2 writer merges into existing bytes, so the destination must be zeroed.
            Marshal.Copy(new byte[destMemory.RowBytes * size.Height], 0, destMemory.Address, destMemory.RowBytes * size.Height);

            SetGrayRow(sourceMemory, 0, 0x55, 0xAA, 0xFF);
            SetGrayRow(sourceMemory, 1, 0xFF, 0xAA, 0x55);

            PixelFormatTranscoder.Transcode(
                sourceMemory.Address,
                size,
                sourceMemory.RowBytes,
                sourceMemory.Format,
                sourceMemory.AlphaFormat,
                destMemory.Address,
                destMemory.RowBytes,
                destMemory.Format,
                destMemory.AlphaFormat);

            // Width 3 uses six of the eight bits in the first byte of each row. Rows are
            // byte-aligned, so the second row's first pixel lands in the top bits of a
            // fresh byte instead of continuing the bit phase of the previous row.
            Assert.Equal(0x6C, Marshal.ReadByte(destMemory.Address, 0));
            Assert.Equal(0xE4, Marshal.ReadByte(destMemory.Address, destMemory.RowBytes));

            static void SetGrayRow(BitmapMemory memory, int y, params byte[] values)
            {
                var row = new Rgba8888Pixel[values.Length];

                for (var x = 0; x < values.Length; x++)
                {
                    row[x] = new Rgba8888Pixel(values[x], values[x], values[x], 255);
                }

                PixelFormatTranscoder.WriteRow(row, memory.Address + y * memory.RowBytes,
                    memory.Format, memory.AlphaFormat, memory.AlphaFormat);
            }
        }

        private BitmapMemory CreateBitmapMemory()
        {
            var bitmapMemory = new BitmapMemory(PixelFormat.Rgba8888, AlphaFormat.Opaque, new PixelSize(3, 1));

            var sourceWriter = new PixelFormatWriter.Rgba8888PixelFormatWriter();

            sourceWriter.Reset(bitmapMemory.Address);

            sourceWriter.WriteNext(new Rgba8888Pixel { R = 255 });
            sourceWriter.WriteNext(new Rgba8888Pixel { G = 255 });
            sourceWriter.WriteNext(new Rgba8888Pixel { B = 255 });

            return bitmapMemory;
        }
    }
}
