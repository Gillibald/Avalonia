using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public unsafe class PixelLayoutTests
    {
        [Theory]
        [InlineData(1, 3, 7, new byte[] { 0b1010_1100, 0b1111_0000 }, new byte[] { 0b0110_0110 })]
        [InlineData(2, 1, 5, new byte[] { 0b00_01_10_11, 0b11_10_01_00 }, new byte[] { 0b01_10_11_11, 0b10_00_00_00 })]
        [InlineData(4, 1, 2, new byte[] { 0x12, 0x34 }, new byte[] { 0x23 })]
        [InlineData(4, 0, 3, new byte[] { 0x12, 0x34 }, new byte[] { 0x12, 0x30 })]
        [InlineData(8, 1, 2, new byte[] { 1, 2, 3 }, new byte[] { 2, 3 })]
        [InlineData(24, 1, 1, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 4, 5, 6 })]
        public void CopyRow_Should_Shift_Pixels_To_The_Row_Start_And_Zero_The_Tail(int bitsPerPixel, int sourceX,
            int width, byte[] source, byte[] expected)
        {
            var dest = new byte[expected.Length];

            Array.Fill(dest, (byte)0xFF);

            fixed (byte* src = &source[0])
            fixed (byte* dst = dest)
                PixelLayout.CopyRow(src, sourceX, dst, width, bitsPerPixel);

            Assert.Equal(expected, dest);
        }

        // A 3x2 image of distinct values 1 2 3 / 4 5 6; orientations are EXIF values.
        [Theory]
        [InlineData(1 /* Normal */, 3, new byte[] { 1, 2, 3, 4, 5, 6 })]
        [InlineData(2 /* FlipHorizontal */, 3, new byte[] { 3, 2, 1, 6, 5, 4 })]
        [InlineData(4 /* FlipVertical */, 3, new byte[] { 4, 5, 6, 1, 2, 3 })]
        [InlineData(3 /* Rotate180 */, 3, new byte[] { 6, 5, 4, 3, 2, 1 })]
        [InlineData(6 /* Rotate90 */, 2, new byte[] { 4, 1, 5, 2, 6, 3 })]
        [InlineData(8 /* Rotate270 */, 2, new byte[] { 3, 6, 2, 5, 1, 4 })]
        [InlineData(5 /* Transpose */, 2, new byte[] { 1, 4, 2, 5, 3, 6 })]
        [InlineData(7 /* Transverse */, 2, new byte[] { 6, 3, 5, 2, 4, 1 })]
        public void Orient_Should_Move_Every_Pixel(int exifOrientation, int width, byte[] expected)
        {
            Assert.Equal(expected, Orient(new byte[] { 1, 2, 3, 4, 5, 6 }, 3, 2, PixelFormats.Gray8, (PixelOrientation)exifOrientation, width));
        }

        [Fact]
        public void Orient_Should_Move_Sub_Byte_Pixels()
        {
            // Indexed4 rows 1 2 3 / 4 5 6, one padding nibble per row.
            var rotated = Orient(new byte[] { 0x12, 0x30, 0x45, 0x60 }, 3, 2, PixelFormats.Indexed4,
                PixelOrientation.Rotate90, 2);

            Assert.Equal(new byte[] { 0x41, 0x52, 0x63 }, rotated);
        }

        [Fact]
        public void Orient_Should_Copy_Whole_Pixels_Of_Wide_Formats()
        {
            var source = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            var rotated = Orient(source, 2, 1, PixelFormats.Rgb48, PixelOrientation.Rotate90, 1);

            Assert.Equal(source, rotated);
        }

        private static byte[] Orient(byte[] source, int width, int height, PixelFormat format,
            PixelOrientation orientation, int orientedWidth)
        {
            var sourceStride = source.Length / height;
            var orientedHeight = width * height / orientedWidth;
            var destStride = (orientedWidth * format.BitsPerPixel + 7) / 8;
            var dest = new byte[destStride * orientedHeight];
            var handle = GCHandle.Alloc(source, GCHandleType.Pinned);

            try
            {
                using var framebuffer = new LockedFramebuffer(handle.AddrOfPinnedObject(), new PixelSize(width, height),
                    sourceStride, new Vector(96, 96), format, AlphaFormat.Opaque, null);

                fixed (byte* dst = dest)
                    PixelLayout.Orient(framebuffer, orientation, dst, destStride);
            }
            finally
            {
                handle.Free();
            }

            return dest;
        }
    }
}
