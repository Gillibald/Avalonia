using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia.Imaging;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Imaging
{
    public class SkiaImagingBackendTests
    {
        private static SKBitmap CreateTestBitmap(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor(
                        (byte)(x * 50 % 256),
                        (byte)(y * 50 % 256),
                        (byte)((x + y) * 25 % 256)));
                }
            }

            return bitmap;
        }

        private static MemoryStream EncodeToStream(SKBitmap bitmap, SKEncodedImageFormat format)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, 100);

            return new MemoryStream(data.ToArray());
        }

        private static byte[] ReadPixels(ILockedFramebuffer framebuffer)
        {
            var bytes = new byte[framebuffer.RowBytes * framebuffer.Size.Height];

            Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);

            return bytes;
        }

        private static (byte B, byte G, byte R, byte A) GetBgra(byte[] pixels, int rowBytes, int x, int y)
        {
            var offset = y * rowBytes + x * 4;

            return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
        }

        private sealed class NonSeekableStream : Stream
        {
            private readonly Stream _inner;

            public NonSeekableStream(Stream inner) => _inner = inner;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] data) : base(data) { }

            public bool Disposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        [Fact]
        public void TryIdentify_Png_Returns_Info_And_Restores_Position()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(5, 3);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            Assert.True(backend.TryIdentify(stream, out var info));

            Assert.Equal("PNG", info.FormatName);
            Assert.Equal(new PixelSize(5, 3), info.PixelSize);
            Assert.Equal(1, info.FrameCount);
            Assert.Equal(0, stream.Position);
        }

        [Fact]
        public void TryIdentify_Unknown_Data_Returns_False()
        {
            var backend = new SkiaImagingBackend();

            using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.False(backend.TryIdentify(stream, out _));
        }

        [Fact]
        public void Decode_Png_Produces_Expected_Pixels()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);
            using var decoder = backend.CreateDecoder(stream, ownsStream: false);
            using var frame = decoder.ReadNextFrame();

            Assert.NotNull(frame);
            Assert.Equal(new PixelSize(4, 4), frame!.PixelSize);
            Assert.Equal(PixelFormats.Bgra8888, frame.PixelFormat);
            Assert.Equal(AlphaFormat.Opaque, frame.AlphaFormat);
            Assert.Null(decoder.ReadNextFrame());

            using var framebuffer = frame.Lock();

            var pixels = ReadPixels(framebuffer);

            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var expected = bitmap.GetPixel(x, y);
                    var actual = GetBgra(pixels, framebuffer.RowBytes, x, y);

                    Assert.Equal((expected.Blue, expected.Green, expected.Red, expected.Alpha), actual);
                }
            }
        }

        [Fact]
        public void Decode_TargetSize_Produces_Exact_Output_Size()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(8, 8);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(4, 4) };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(4, 4), frame!.PixelSize);

            using var framebuffer = frame.Lock();

            Assert.Equal(new PixelSize(4, 4), framebuffer.Size);
            Assert.Equal(16, framebuffer.RowBytes);
        }

        [Fact]
        public void Decode_TargetSize_Completes_Aspect_When_One_Dimension_Is_Zero()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(8, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(4, 0) };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(4, 2), frame!.PixelSize);
        }

        [Fact]
        public void Decode_TargetFormat_Rgb565_Is_Honored()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { TargetFormat = PixelFormats.Rgb565 };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(PixelFormats.Rgb565, frame!.PixelFormat);

            using var framebuffer = frame.Lock();

            Assert.Equal(PixelFormats.Rgb565, framebuffer.Format);
            Assert.Equal(8, framebuffer.RowBytes);
        }

        [Fact]
        public void Decode_SourceRegion_Crops()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { SourceRegion = new PixelRect(2, 2, 2, 2) };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(2, 2), frame!.PixelSize);

            using var framebuffer = frame.Lock();

            var pixels = ReadPixels(framebuffer);

            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var expected = bitmap.GetPixel(x + 2, y + 2);
                    var actual = GetBgra(pixels, framebuffer.RowBytes, x, y);

                    Assert.Equal((expected.Blue, expected.Green, expected.Red, expected.Alpha), actual);
                }
            }
        }

        [Fact]
        public void Decode_NonSeekable_Stream_Works()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);
            using var stream = new NonSeekableStream(encoded);
            using var decoder = backend.CreateDecoder(stream, ownsStream: false);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(4, 4), frame!.PixelSize);
        }

        [Fact]
        public void TryIdentify_On_A_NonSeekable_Stream_Throws()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);
            using var stream = new NonSeekableStream(encoded);

            Assert.Throws<ArgumentException>(() => backend.TryIdentify(stream, out _));
        }

        [Fact]
        public void TryIdentify_From_Bytes_Works()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(5, 3);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            Assert.True(backend.TryIdentify(encoded.ToArray(), out var info));
            Assert.Equal("PNG", info.FormatName);
            Assert.Equal(new PixelSize(5, 3), info.PixelSize);

            Assert.False(backend.TryIdentify(new byte[] { 1, 2, 3, 4 }, out _));
        }

        [Fact]
        public void Decoder_Info_Serves_Identify_For_ForwardOnly_Streams()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);
            using var stream = new NonSeekableStream(encoded);
            using var decoder = backend.CreateDecoder(stream, ownsStream: false);

            Assert.Equal("PNG", decoder.Info.FormatName);
            Assert.Equal(new PixelSize(4, 4), decoder.Info.PixelSize);

            // Reading Info did not advance the cursor: frame 0 still decodes correctly.
            using var frame = decoder.ReadNextFrame();
            using var framebuffer = frame!.Lock();

            var expected = bitmap.GetPixel(1, 2);
            var actual = GetBgra(ReadPixels(framebuffer), framebuffer.RowBytes, 1, 2);

            Assert.Equal((expected.Blue, expected.Green, expected.Red, expected.Alpha), actual);
        }

        [Fact]
        public void Decoder_Info_Reports_PrePlan_Source_Values()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(8, 8);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { TargetSize = new PixelSize(4, 4) };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(8, 8), decoder.Info.PixelSize);
            Assert.Equal(new PixelSize(4, 4), frame!.PixelSize);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Decoder_Owns_Its_Encoded_Data_After_Create(bool seekable)
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var callerStream = seekable
                ? (Stream)new MemoryStream(encoded.ToArray())
                : new NonSeekableStream(new MemoryStream(encoded.ToArray()));

            using var decoder = backend.CreateDecoder(callerStream, ownsStream: false);

            // The decoder secured its own copy; the caller's stream is free immediately.
            callerStream.Dispose();

            using var frame = decoder.ReadNextFrame();
            using var framebuffer = frame!.Lock();

            var expected = bitmap.GetPixel(2, 1);
            var actual = GetBgra(ReadPixels(framebuffer), framebuffer.RowBytes, 2, 1);

            Assert.Equal((expected.Blue, expected.Green, expected.Red, expected.Alpha), actual);
        }

        [Fact]
        public void MaxPixels_Rejects_Before_Decoding()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var options = new BitmapDecodeOptions { MaxPixels = 8 };

            Assert.Throws<InvalidOperationException>(() => backend.CreateDecoder(stream, ownsStream: false, options));
        }

        [Fact]
        public void JpegOptions_On_Png_Stream_Are_Rejected()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            Assert.Throws<ArgumentException>(() =>
                backend.CreateDecoder(stream, ownsStream: false, new JpegDecodeOptions()));
        }

        [Fact]
        public void OwnsStream_Disposes_The_Stream()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var encoded = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var stream = new TrackingStream(encoded.ToArray());

            using var decoder = backend.CreateDecoder(stream, ownsStream: true);

            Assert.True(stream.Disposed);
        }

        [Fact]
        public void Frame_Survives_Decoder_Dispose()
        {
            var backend = new SkiaImagingBackend();

            using var bitmap = CreateTestBitmap(4, 4);
            using var stream = EncodeToStream(bitmap, SKEncodedImageFormat.Png);

            var decoder = backend.CreateDecoder(stream, ownsStream: false);
            var frame = decoder.ReadNextFrame();

            decoder.Dispose();

            using (var framebuffer = frame!.Lock())
            {
                Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
            }

            frame.Dispose();
        }

        /// <summary>
        /// Inserts a minimal EXIF APP1 segment (one IFD entry: orientation) after the
        /// JPEG SOI marker, since SkiaSharp cannot write EXIF itself.
        /// </summary>
        private static byte[] InjectExifOrientation(byte[] jpeg, byte orientation)
        {
            var app1 = new byte[]
            {
                0xFF, 0xE1, 0x00, 0x22,                                 // APP1, length 34
                (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00, // Exif\0\0
                0x49, 0x49, 0x2A, 0x00,                                 // TIFF header, little endian
                0x08, 0x00, 0x00, 0x00,                                 // IFD0 offset
                0x01, 0x00,                                             // one directory entry
                0x12, 0x01, 0x03, 0x00,                                 // tag 0x0112, type SHORT
                0x01, 0x00, 0x00, 0x00,                                 // count 1
                orientation, 0x00, 0x00, 0x00,                          // value
                0x00, 0x00, 0x00, 0x00,                                 // next IFD offset
            };

            var result = new byte[jpeg.Length + app1.Length];

            result[0] = jpeg[0];
            result[1] = jpeg[1];
            app1.CopyTo(result, 2);
            Array.Copy(jpeg, 2, result, 2 + app1.Length, jpeg.Length - 2);

            return result;
        }

        private static MemoryStream CreateOrientedJpeg(byte orientation)
        {
            // 8x4, left half red, right half green; strongly distinct through JPEG loss.
            using var bitmap = new SKBitmap(new SKImageInfo(8, 4, SKColorType.Bgra8888, SKAlphaType.Opaque));

            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    bitmap.SetPixel(x, y, x < 4 ? new SKColor(255, 0, 0) : new SKColor(0, 255, 0));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);

            return new MemoryStream(InjectExifOrientation(data.ToArray(), orientation));
        }

        [Fact]
        public void Exif_Orientation_Is_Applied_By_Default()
        {
            var backend = new SkiaImagingBackend();

            using var stream = CreateOrientedJpeg(orientation: 6);   // rotate 90 clockwise
            using var decoder = backend.CreateDecoder(stream, ownsStream: false);
            using var frame = decoder.ReadNextFrame();

            // The 8x4 raw image displays as 4x8; raw left half (red) lands on top.
            Assert.Equal(new PixelSize(4, 8), frame!.PixelSize);

            using var framebuffer = frame.Lock();

            var pixels = ReadPixels(framebuffer);
            var top = GetBgra(pixels, framebuffer.RowBytes, 1, 1);
            var bottom = GetBgra(pixels, framebuffer.RowBytes, 1, 6);

            Assert.True(top.R > 200 && top.G < 80, $"expected red on top, got {top}");
            Assert.True(bottom.G > 200 && bottom.R < 80, $"expected green at the bottom, got {bottom}");
        }

        [Fact]
        public void Exif_Orientation_Can_Be_Ignored()
        {
            var backend = new SkiaImagingBackend();

            using var stream = CreateOrientedJpeg(orientation: 6);

            var options = new BitmapDecodeOptions { RespectExifOrientation = false };

            using var decoder = backend.CreateDecoder(stream, ownsStream: false, options);
            using var frame = decoder.ReadNextFrame();

            Assert.Equal(new PixelSize(8, 4), frame!.PixelSize);
        }

        [Fact]
        public void Encode_Png_Round_Trips()
        {
            var backend = new SkiaImagingBackend();

            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(backend);

            var sourcePixels = new byte[4 * 4 * 4];

            for (var i = 0; i < sourcePixels.Length; i += 4)
            {
                sourcePixels[i] = (byte)(i % 256);
                sourcePixels[i + 1] = (byte)((i * 3) % 256);
                sourcePixels[i + 2] = (byte)((i * 7) % 256);
                sourcePixels[i + 3] = 255;
            }

            var buffer = PixelBuffer.TakeOwnership(sourcePixels, new PixelSize(4, 4), 16,
                PixelFormats.Bgra8888, AlphaFormat.Opaque, new Vector(96, 96));

            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = buffer });

            using var encoded = new MemoryStream();

            encoder.Save(encoded);

            Assert.NotEqual(0, encoded.Length);

            encoded.Position = 0;

            using var decoder = backend.CreateDecoder(encoded, ownsStream: false);
            using var frame = decoder.ReadNextFrame();
            using var framebuffer = frame!.Lock();

            Assert.Equal(sourcePixels, ReadPixels(framebuffer));
        }

        [Fact]
        public void Encode_Unsupported_Format_Fails_Fast_Naming_The_Backend()
        {
            var backend = new SkiaImagingBackend();

            using var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(backend);

            var buffer = PixelBuffer.TakeOwnership(new byte[4], new PixelSize(1, 1), 4,
                PixelFormats.Bgra8888, AlphaFormat.Opaque, new Vector(96, 96));

            var encoder = new TiffBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = buffer });

            using var stream = new MemoryStream();

            var exception = Assert.Throws<NotSupportedException>(() => encoder.Save(stream));

            Assert.Contains("SkiaSharp", exception.Message);
            Assert.Equal(0, stream.Length);
        }
    }
}
