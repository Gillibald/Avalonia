using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class PixelBufferTests
    {
        private static byte[] CreateSourcePixels() => new byte[]
        {
            // 2x2 Bgra8888, stride 8.
            1, 2, 3, 255, 4, 5, 6, 255,
            7, 8, 9, 255, 10, 11, 12, 255,
        };

        private static PixelBuffer CopyFromPinned(byte[] pixels, Action<ILockedFramebuffer>? use = null)
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                var framebuffer = new LockedFramebuffer(
                    handle.AddrOfPinnedObject(),
                    new PixelSize(2, 2),
                    rowBytes: 8,
                    new Vector(120, 120),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul,
                    onDispose: null);

                use?.Invoke(framebuffer);

                return PixelBuffer.CopyFrom(framebuffer);
            }
            finally
            {
                handle.Free();
            }
        }

        [Fact]
        public void CopyFrom_Captures_Pixels_And_Descriptor()
        {
            var source = CreateSourcePixels();

            var buffer = CopyFromPinned(source);

            Assert.Equal(new PixelSize(2, 2), buffer.Size);
            Assert.Equal(8, buffer.Stride);
            Assert.Equal(PixelFormat.Bgra8888, buffer.Format);
            Assert.Equal(AlphaFormat.Premul, buffer.AlphaFormat);
            Assert.Equal(new Vector(120, 120), buffer.Dpi);
            Assert.Equal(source, buffer.Pixels.ToArray());
        }

        [Fact]
        public void Buffer_Is_Independent_Of_The_Source_Memory()
        {
            var source = CreateSourcePixels();

            var buffer = CopyFromPinned(source);

            source[0] = 99;

            Assert.Equal(1, buffer.Pixels.Span[0]);
        }

        [Fact]
        public void Lock_Exposes_The_Same_Pixels_And_Can_Be_Taken_Repeatedly()
        {
            var source = CreateSourcePixels();
            var buffer = CopyFromPinned(source);

            using (var framebuffer = buffer.Lock())
            {
                Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
                Assert.Equal(buffer.Size, framebuffer.Size);
                Assert.Equal(buffer.Stride, framebuffer.RowBytes);

                var roundTripped = new byte[source.Length];

                Marshal.Copy(framebuffer.Address, roundTripped, 0, roundTripped.Length);

                Assert.Equal(source, roundTripped);
            }

            using (var framebuffer = buffer.Lock())
            {
                Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
            }
        }

        [Fact]
        public void CopyFrom_Rejects_Invalid_Sources()
        {
            Assert.Throws<ArgumentNullException>(() => PixelBuffer.CopyFrom(null!));

            var zeroAddress = new LockedFramebuffer(IntPtr.Zero, new PixelSize(1, 1), 4,
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul, null);

            Assert.Throws<ArgumentException>(() => PixelBuffer.CopyFrom(zeroAddress));

            var pixels = new byte[4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                var tooSmallStride = new LockedFramebuffer(handle.AddrOfPinnedObject(),
                    new PixelSize(2, 1), rowBytes: 4, new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul, null);

                Assert.Throws<ArgumentException>(() => PixelBuffer.CopyFrom(tooSmallStride));
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
