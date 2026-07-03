using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia.Imaging;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Imaging
{
    /// <summary>
    /// Exercises the public BitmapDecoder/BitmapFrame/Bitmap surface over the SkiaSharp
    /// backend with a real Skia render interface bound.
    /// </summary>
    public class PublicImagingApiTests
    {
        private static IDisposable BindPlatform()
        {
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>()
                .ToConstant(new Avalonia.Skia.PlatformRenderInterface(null));

            ImagingBackend.Register(new SkiaImagingBackend());

            return scope;
        }

        private static MemoryStream CreatePng(int width, int height)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)(x * 40), (byte)(y * 40), (byte)((x + y) * 20)));
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return new MemoryStream(data.ToArray());
        }

        [Fact]
        public void Identify_Reports_Header_Facts()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(6, 3);

            var info = BitmapDecoder.Identify(stream);

            Assert.Equal("PNG", info.FormatName);
            Assert.Equal(new PixelSize(6, 3), info.PixelSize);
            Assert.Equal(0, stream.Position);
        }

        private sealed class ForwardOnlyStream : Stream
        {
            private readonly Stream _inner;

            public ForwardOnlyStream(Stream inner) => _inner = inner;

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

        [Fact]
        public void Identify_On_A_ForwardOnly_Stream_Throws_By_Default()
        {
            using var scope = BindPlatform();
            using var encoded = CreatePng(4, 4);
            using var stream = new ForwardOnlyStream(encoded);

            Assert.Throws<ArgumentException>(() => BitmapDecoder.TryIdentify(stream, out _));
        }

        [Fact]
        public void Identify_ConsumePrefix_Opts_In_To_Sniffing_A_ForwardOnly_Stream()
        {
            using var scope = BindPlatform();
            using var encoded = CreatePng(6, 3);
            using var stream = new ForwardOnlyStream(encoded);

            Assert.True(BitmapDecoder.TryIdentify(stream, out var info, IdentifyStreamBehavior.ConsumePrefix));

            Assert.Equal("PNG", info.FormatName);
            Assert.Equal(new PixelSize(6, 3), info.PixelSize);
        }

        [Fact]
        public void Identify_ConsumePrefix_Does_Not_Consume_A_Seekable_Stream()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(6, 3);

            Assert.True(BitmapDecoder.TryIdentify(stream, out _, IdentifyStreamBehavior.ConsumePrefix));
            Assert.Equal(0, stream.Position);
        }

        [Fact]
        public void Decoder_Exposes_CodecInfo_And_Frames()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);

            Assert.Equal("PNG", decoder.CodecInfo.FormatName);
            Assert.True((decoder.CodecInfo.Capabilities & BitmapCodecCapabilities.Decode) != 0);
            Assert.False(decoder.CanSeekFrames);

            var count = 0;

            foreach (var frame in decoder.Frames)
            {
                using (frame)
                {
                    Assert.Equal(new PixelSize(4, 4), frame.PixelSize);
                    count++;
                }
            }

            Assert.Equal(1, count);
        }

        [Fact]
        public void Frames_Can_Only_Be_Enumerated_Once()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);

            _ = decoder.Frames;

            Assert.Throws<InvalidOperationException>(() => decoder.Frames);
        }

        [Fact]
        public void GetFrame_On_A_Forward_Only_Decoder_Throws()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);

            Assert.Throws<NotSupportedException>(() => decoder.GetFrame(0));
        }

        [Fact]
        public void ToBitmap_Realizes_Once_And_Shares_The_Render_Bitmap()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            using var first = frame.ToBitmap();
            using var second = frame.ToBitmap();

            Assert.Same(first.PlatformImpl.Item, second.PlatformImpl.Item);
            Assert.Equal(new PixelSize(4, 4), first.PixelSize);
        }

        [Fact]
        public void Realized_Bitmap_Outlives_The_Frame()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);

            Bitmap bitmap;

            using (var frame = decoder.ReadNextFrame()!)
            {
                bitmap = frame.ToBitmap();
            }

            using (bitmap)
            {
                var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(bitmap.PlatformImpl.Item);

                using var framebuffer = readable.Lock();

                Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
                Assert.Equal(new PixelSize(4, 4), framebuffer.Size);
            }
        }

        [Fact]
        public void ToPixelBuffer_And_Back_Round_Trips()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var decoder = BitmapDecoder.Create(stream);
            using var frame = decoder.ReadNextFrame()!;

            var buffer = frame.ToPixelBuffer();

            Assert.Equal(new PixelSize(4, 4), buffer.Size);
            Assert.Equal(PixelFormats.Bgra8888, buffer.Format);

            using var bitmap = buffer.ToBitmap();

            Assert.Equal(new PixelSize(4, 4), bitmap.PixelSize);
        }

        [Fact]
        public void Bitmap_Decode_Applies_The_Plan()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(8, 8);

            using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions
            {
                TargetSize = new PixelSize(4, 4),
                TargetFormat = PixelFormats.Rgb565,
            });

            Assert.Equal(new PixelSize(4, 4), bitmap.PixelSize);
            Assert.Equal(PixelFormats.Rgb565, bitmap.Format);
        }

        [Fact]
        public async Task DecodeAsync_Works_And_Honors_A_Cancelled_Token()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);

            using var bitmap = await Bitmap.DecodeAsync(stream,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(new PixelSize(4, 4), bitmap.PixelSize);

            using var cancelled = new CancellationTokenSource();

            cancelled.Cancel();

            using var secondStream = CreatePng(4, 4);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Bitmap.DecodeAsync(secondStream, cancellationToken: cancelled.Token));
        }

        [Fact]
        public void Bitmap_Save_With_Encoder_Round_Trips()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var original = Bitmap.Decode(stream);
            using var encoded = new MemoryStream();

            original.Save(encoded, new PngBitmapEncoder());

            Assert.NotEqual(0, encoded.Length);

            encoded.Position = 0;

            using var roundTripped = Bitmap.Decode(encoded);

            Assert.Equal(original.PixelSize, roundTripped.PixelSize);

            var originalPixels = ReadPixels(original);
            var roundTrippedPixels = ReadPixels(roundTripped);

            Assert.Equal(originalPixels, roundTrippedPixels);
        }

        [Fact]
        public void Legacy_Save_Routes_Through_The_Backend_And_Round_Trips()
        {
            using var scope = BindPlatform();
            using var source = CreatePng(4, 4);
            using var original = Bitmap.Decode(source);
            using var saved = new MemoryStream();

            original.Save(saved);

            saved.Position = 0;

            using var roundTripped = Bitmap.Decode(saved);

            Assert.Equal(original.PixelSize, roundTripped.PixelSize);
            Assert.Equal(ReadPixels(original), ReadPixels(roundTripped));
        }

        [Fact]
        public void Legacy_Save_Without_A_Backend_Falls_Back_To_The_Platform_Path()
        {
            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>()
                .ToConstant(new Avalonia.Skia.PlatformRenderInterface(null));

            using var source = CreatePng(4, 4);
            using var bitmap = new Bitmap(source);
            using var saved = new MemoryStream();

            bitmap.Save(saved);

            Assert.NotEqual(0, saved.Length);
        }

        [Fact]
        public void Save_With_Transform_Rotates_On_Every_Backend()
        {
            using var scope = BindPlatform();
            using var source = CreatePng(4, 2);
            using var bitmap = Bitmap.Decode(source);
            using var saved = new MemoryStream();

            // The Skia encoder has no native transform; the shared software path
            // guarantees the semantics anyway.
            bitmap.Save(saved, new PngBitmapEncoder
            {
                Transform = new BitmapTransform(BitmapRotation.Rotate90, false, false),
            });

            saved.Position = 0;

            using var rotated = Bitmap.Decode(saved);

            Assert.Equal(new PixelSize(2, 4), rotated.PixelSize);
        }

        [Fact]
        public void Bitmap_Save_With_Unsupported_Encoder_Names_The_Backend()
        {
            using var scope = BindPlatform();
            using var stream = CreatePng(4, 4);
            using var bitmap = Bitmap.Decode(stream);
            using var target = new MemoryStream();

            var exception = Assert.Throws<NotSupportedException>(
                () => bitmap.Save(target, new GifBitmapEncoder()));

            Assert.Contains("SkiaSharp", exception.Message);
        }

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(bitmap.PlatformImpl.Item);

            using var framebuffer = readable.Lock();

            var bytes = new byte[framebuffer.RowBytes * framebuffer.Size.Height];

            Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);

            return bytes;
        }
    }
}
