using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class PixelBufferOperationsTests
    {
        private static readonly BitmapPalette s_palette = new(new[]
        {
            Colors.Black, Colors.White, Color.FromRgb(255, 0, 0), Color.FromRgb(0, 0, 255)
        });

        private static PixelBuffer Bgra(params byte[] pixels)
            => PixelBuffer.Create(pixels, new PixelSize(pixels.Length / 4, 1), pixels.Length, PixelFormats.Bgra8888,
                AlphaFormat.Unpremul);

        // Indexed4, 3x2: 0 1 2 / 3 2 1.
        private static PixelBuffer Indexed() => PixelBuffer.Create(new byte[] { 0x01, 0x20, 0x32, 0x10 },
            new PixelSize(3, 2), 2, PixelFormats.Indexed4, AlphaFormat.Unpremul, new Vector(72, 144), s_palette);

        [Fact]
        public void Indexed_Buffers_Should_Require_A_Palette()
        {
            Assert.Throws<ArgumentException>(() => PixelBuffer.Create(new byte[1], new PixelSize(1, 1), 1,
                PixelFormats.Indexed8, AlphaFormat.Unpremul));

            Assert.Same(s_palette, Indexed().Palette);
            Assert.Null(PixelBuffer.Create(new byte[4], new PixelSize(1, 1), 4, PixelFormats.Bgra8888,
                AlphaFormat.Unpremul, palette: s_palette).Palette);
        }

        [Fact]
        public void CopyPixels_To_The_Same_Format_Should_Keep_Every_Bit()
        {
            var source = new byte[] { 0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A, 0xF0, 0xDE };
            var buffer = PixelBuffer.Create(source, new PixelSize(1, 1), 8, PixelFormats.Rgba64, AlphaFormat.Unpremul);
            var dest = new byte[8];

            buffer.CopyPixels(new PixelRect(0, 0, 1, 1), dest, 8, PixelFormats.Rgba64, AlphaFormat.Unpremul);

            Assert.Equal(source, dest);
        }

        [Fact]
        public void CopyPixels_Should_Validate_Before_Writing()
        {
            var buffer = Bgra(1, 2, 3, 255, 4, 5, 6, 255);
            var dest = new byte[8];

            Array.Fill(dest, (byte)0xAA);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                buffer.CopyPixels(new PixelRect(1, 0, 2, 1), dest, 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                buffer.CopyPixels(new PixelRect(0, 0, 2, 1), dest, 4, PixelFormats.Bgra8888, AlphaFormat.Unpremul));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                buffer.CopyPixels(new PixelRect(0, 0, 2, 1), dest.AsSpan(0, 7), 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul));
            Assert.Throws<ArgumentException>(() =>
                buffer.CopyPixels(default, dest, 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul));

            Assert.All(dest, value => Assert.Equal(0xAA, value));
        }

        [Fact]
        public void CopyPixels_Should_Convert_A_Rectangle_That_Starts_Mid_Byte()
        {
            var dest = new byte[8];

            Indexed().CopyPixels(new PixelRect(1, 1, 2, 1), dest, 8, PixelFormats.Bgra8888, AlphaFormat.Unpremul);

            // Indices 2 and 1: red, then white; Bgra8888 memory order is B, G, R, A.
            Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, dest);
        }

        [Fact]
        public void Convert_To_An_Indexed_Format_Should_Carry_The_Palette()
        {
            var indexed = Bgra(0, 0, 250, 255, 250, 0, 0, 255).Convert(PixelFormats.Indexed8, AlphaFormat.Unpremul,
                new PixelConversionOptions { Palette = s_palette });

            Assert.Same(s_palette, indexed.Palette);
            Assert.Equal(new byte[] { 2, 3 }, indexed.Pixels.ToArray());

            Assert.Throws<ArgumentException>(() => Bgra(0, 0, 0, 255).Convert(PixelFormats.Indexed8, AlphaFormat.Unpremul));
        }

        [Fact]
        public void Convert_To_The_Same_Indexed_Format_Should_Remap_To_A_New_Palette()
        {
            var reversed = new BitmapPalette(new[]
            {
                Color.FromRgb(0, 0, 255), Color.FromRgb(255, 0, 0), Colors.White, Colors.Black
            });

            var remapped = Indexed().Convert(PixelFormats.Indexed4, AlphaFormat.Unpremul,
                new PixelConversionOptions { Palette = reversed });

            Assert.Same(reversed, remapped.Palette);
            Assert.Equal(new byte[] { 0x32, 0x10, 0x01, 0x20 }, remapped.Pixels.ToArray());
        }

        [Fact]
        public void Crop_Should_Keep_Indices_Palette_And_Dpi()
        {
            var cropped = Indexed().Crop(new PixelRect(1, 0, 2, 2));

            Assert.Equal(new PixelSize(2, 2), cropped.Size);
            Assert.Equal(new byte[] { 0x12, 0x21 }, cropped.Pixels.ToArray());
            Assert.Same(s_palette, cropped.Palette);
            Assert.Equal(new Vector(72, 144), cropped.Dpi);
        }

        [Fact]
        public void Transform_Should_Swap_Size_And_Dpi_For_A_Quarter_Turn()
        {
            var rotated = Indexed().Transform(new BitmapTransform(BitmapRotation.Rotate90, false, false));

            Assert.Equal(new PixelSize(2, 3), rotated.Size);
            Assert.Equal(new Vector(144, 72), rotated.Dpi);
            Assert.Equal(new byte[] { 0x30, 0x21, 0x12 }, rotated.Pixels.ToArray());
            Assert.Same(s_palette, rotated.Palette);
        }

        [Fact]
        public void Transform_Without_Effect_Should_Return_The_Same_Buffer()
        {
            var buffer = Indexed();

            Assert.Same(buffer, buffer.Transform(new BitmapTransform(BitmapRotation.Rotate180, true, true)));
        }

        [Theory]
        [InlineData(8, AlphaFormat.Unpremul)]
        [InlineData(2, AlphaFormat.Unpremul)]
        [InlineData(8, AlphaFormat.Premul)]
        [InlineData(2, AlphaFormat.Premul)]
        public void Scale_Should_Keep_A_Uniform_Translucent_Color(int size, AlphaFormat target)
        {
            // Filtering premultiplies; doing that in eight bits loses the colour of translucent
            // pixels (128 at alpha 128 premultiplies to 64 and comes back as 127).
            var pixels = new byte[4 * 4 * 4];
            Array.Fill(pixels, (byte)0x80);

            var source = PixelBuffer.Create(pixels, new PixelSize(4, 4), 16, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            var scaled = source.Scale(new PixelSize(size, size), BitmapInterpolationMode.HighQuality)
                .Convert(PixelFormats.Bgra8888, target);
            var expected = target == AlphaFormat.Premul
                ? new byte[] { 0x40, 0x40, 0x40, 0x80 }
                : new byte[] { 0x80, 0x80, 0x80, 0x80 };

            for (var i = 0; i < scaled.Pixels.Length; i += 4)
                Assert.Equal(expected, scaled.Pixels.Slice(i, 4).ToArray());
        }

        [Fact]
        public void Scale_Should_Resample_In_The_Source_Format()
        {
            var scaled = Bgra(0, 0, 0, 255, 200, 100, 50, 255).Scale(new PixelSize(1, 1));

            Assert.Equal(PixelFormats.Bgra8888, scaled.Format);
            Assert.Equal(new byte[] { 100, 50, 25, 255 }, scaled.Pixels.ToArray());
        }

        [Fact]
        public void Scale_Should_Match_Indexed_Pixels_Back_To_Their_Palette()
        {
            var scaled = Indexed().Scale(new PixelSize(6, 4), BitmapInterpolationMode.None);

            Assert.Equal(PixelFormats.Indexed4, scaled.Format);
            Assert.Same(s_palette, scaled.Palette);
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22 }, scaled.Pixels.Slice(0, 3).ToArray());
        }

        [Fact]
        public void Scale_Should_Reject_An_Empty_Size_And_Keep_An_Unchanged_One()
        {
            var buffer = Indexed();

            Assert.Throws<ArgumentException>(() => buffer.Scale(new PixelSize(0, 1)));
            Assert.Same(buffer, buffer.Scale(buffer.Size));
        }

        [Fact]
        public void Framebuffers_Should_Convert_A_Rectangle_Into_Caller_Memory()
        {
            var pixels = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var dest = new byte[3];
            var destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);

            try
            {
                using var framebuffer = new LockedFramebuffer(handle.AddrOfPinnedObject(), new PixelSize(2, 1), 8,
                    new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul, null);

                framebuffer.CopyPixels(null, new PixelRect(1, 0, 1, 1), destHandle.AddrOfPinnedObject(), 3, 3,
                    PixelFormats.Rgb24, AlphaFormat.Opaque);
            }
            finally
            {
                handle.Free();
                destHandle.Free();
            }

            Assert.Equal(new byte[] { 60, 50, 40 }, dest);
        }
    }
}
