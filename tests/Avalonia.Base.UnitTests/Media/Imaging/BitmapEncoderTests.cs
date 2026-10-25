using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapEncoderTests
    {
        private static readonly Guid s_container = BitmapContainerFormats.Png;

        private sealed class TestEncoder : BitmapEncoder
        {
            public TestEncoder(BitmapCodecCapabilities capabilities) => DeclaredCapabilities = capabilities;

            public BitmapCodecCapabilities DeclaredCapabilities { get; }

            protected override Guid ContainerFormat => s_container;

            protected override BitmapCodecCapabilities Capabilities => DeclaredCapabilities;
        }

        private sealed class RecordingEncoderImpl : IBitmapEncoderImpl
        {
            public BitmapEncoder? Encoded { get; private set; }

            public CancellationToken Token { get; private set; }

            public bool StreamCanSeek { get; private set; }

            public void Encode(BitmapEncoder encoder, Stream stream, CancellationToken cancellationToken)
            {
                Encoded = encoder;
                Token = cancellationToken;
                StreamCanSeek = stream.CanSeek;
                stream.WriteByte(42);
            }
        }

        private sealed class CodecInfo : IBitmapCodecInfo
        {
            public CodecInfo(BitmapCodecCapabilities capabilities) => Capabilities = capabilities;

            public string FormatName => "PNG";

            public IReadOnlyList<string> MimeTypes { get; } = new[] { "image/png" };

            public IReadOnlyList<string> FileExtensions { get; } = new[] { "png" };

            public Guid ContainerFormat => s_container;

            public BitmapCodecCapabilities Capabilities { get; }
        }

        private sealed class FakeBackend : IImagingBackend
        {
            private readonly bool _canEncode;

            public FakeBackend(BitmapCodecCapabilities codecCapabilities, bool canEncode = true)
            {
                _canEncode = canEncode;
                SupportedCodecs = new[] { new CodecInfo(codecCapabilities) };
            }

            public RecordingEncoderImpl EncoderImpl { get; } = new();

            public string Name => "Fake";

            public IReadOnlyList<IBitmapCodecInfo> SupportedCodecs { get; }

            public int IdentifyPrefixLength => 4096;

            public bool TryIdentify(Stream stream, out BitmapImageInfo info)
            {
                info = default;
                return false;
            }

            public bool TryIdentify(ReadOnlySpan<byte> data, out BitmapImageInfo info)
            {
                info = default;
                return false;
            }

            public IBitmapDecoder CreateDecoder(Stream stream, bool ownsStream, BitmapDecodeOptions? options = null)
                => throw new NotSupportedException();

            public IBitmapEncoderImpl CreateEncoder(Guid containerFormat)
                => _canEncode
                    ? EncoderImpl
                    : throw new NotSupportedException($"The '{Name}' imaging backend cannot encode this format.");
        }

        private static BitmapEncoderFrame CreateFrame()
        {
            var pixels = PixelBuffer.TakeOwnership(new byte[4], new PixelSize(1, 1), 4,
                PixelFormat.Bgra8888, AlphaFormat.Premul, new Vector(96, 96));

            return new BitmapEncoderFrame { Pixels = pixels };
        }

        private static IDisposable BindBackend(FakeBackend backend)
        {
            var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(backend);

            return scope;
        }

        [Fact]
        public void An_Empty_Color_Profile_Should_Mean_None()
        {
            byte[]? missing = null;

            // A null array converts to an empty memory, not to a null one.
            var frame = new BitmapEncoderFrame { ColorProfile = missing };
            var encoder = new BmpBitmapEncoder { ColorProfile = Array.Empty<byte>() };

            Assert.Null(frame.ColorProfile);
            Assert.Null(encoder.ColorProfile);
        }

        [Fact]
        public void Save_Without_Frames_Throws_And_Writes_Nothing()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void MultiFrame_Without_Encoder_Capability_Throws()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());
            encoder.Frames.Add(CreateFrame());

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void MultiFrame_Without_Backend_Capability_Throws_Naming_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(
                BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.MultiFrameEncode);

            encoder.Frames.Add(CreateFrame());
            encoder.Frames.Add(CreateFrame());

            var exception = Assert.Throws<NotSupportedException>(() => encoder.Save(stream));

            Assert.Contains("Fake", exception.Message);
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Metadata_Without_Capability_Throws()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Metadata);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode)
            {
                Metadata = new BitmapMetadata(BitmapContainerFormats.Png),
            };

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Frame_ColorProfile_Without_Backend_Capability_Throws_Naming_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.ColorProfile);
            var frame = CreateFrame();

            frame.ColorProfile = new byte[128];
            encoder.Frames.Add(frame);

            var exception = Assert.Throws<NotSupportedException>(() => encoder.Save(stream));

            Assert.Contains("Fake", exception.Message);
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Unsupported_Encode_Fails_Fast_Naming_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Decode, canEncode: false);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());

            var exception = Assert.Throws<NotSupportedException>(() => encoder.Save(stream));

            Assert.Contains("Fake", exception.Message);
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Valid_Single_Frame_Dispatches_To_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());
            encoder.Save(stream, TestContext.Current.CancellationToken);

            Assert.Same(encoder, backend.EncoderImpl.Encoded);
            Assert.Equal(1, stream.Length);
        }

        [Fact]
        public void Palette_Without_Capability_Throws()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Palette);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);
            var frame = CreateFrame();

            frame.Palette = new BitmapPalette(new[] { Avalonia.Media.Colors.Black });

            encoder.Frames.Add(frame);

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Palette_Without_Backend_Capability_Throws_Naming_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Palette);
            var frame = CreateFrame();

            frame.Palette = new BitmapPalette(new[] { Avalonia.Media.Colors.Black });

            encoder.Frames.Add(frame);

            var exception = Assert.Throws<NotSupportedException>(() => encoder.Save(stream));

            Assert.Contains("Fake", exception.Message);
            Assert.Equal(0, stream.Length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void Jpeg_Quality_Out_Of_Range_Throws_Before_Writing(int quality)
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new JpegBitmapEncoder { Quality = quality };

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(10)]
        public void Png_CompressionLevel_Out_Of_Range_Throws_Before_Writing(int level)
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new PngBitmapEncoder { CompressionLevel = level };

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Save_With_Canceled_Token_Throws_Before_Writing()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<OperationCanceledException>(() => encoder.Save(stream, new CancellationToken(true)));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Save_Forwards_The_Token_To_The_Backend()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();
            using var cts = new CancellationTokenSource();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());
            encoder.Save(stream, cts.Token);

            Assert.Equal(cts.Token, backend.EncoderImpl.Token);
        }

        [Fact]
        public void Frame_Without_A_Pixel_Source_Throws_Before_Writing()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(new BitmapEncoderFrame());

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Frame_With_Both_Pixel_Sources_Throws_Before_Writing()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();
            using var frame = new BitmapFrame(new FakeFrameSource(), BitmapContainerFormats.Png);

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(new BitmapEncoderFrame
            {
                Pixels = PixelBuffer.TakeOwnership(new byte[4], new PixelSize(1, 1), 4,
                    PixelFormat.Bgra8888, AlphaFormat.Premul, new Vector(96, 96)),
                Frame = frame,
            });

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Frame_Origin_On_A_Format_Without_Animation_Throws()
        {
            using var scope = BindBackend(new FakeBackend(BitmapCodecCapabilities.Encode));
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);
            var frame = CreateFrame();

            frame.Origin = new PixelPoint(1, 0);
            encoder.Frames.Add(frame);

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void Gif_Screen_Defaults_To_The_Extent_Of_All_Frames()
        {
            var encoder = new GifBitmapEncoder();

            encoder.Frames.Add(CreateFrame(4, 3));
            encoder.Frames.Add(WithOrigin(CreateFrame(2, 2), 5, 1));

            Assert.Equal(new PixelSize(7, 3), encoder.GetScreenSize());

            encoder.ScreenSize = new PixelSize(10, 10);

            Assert.Equal(new PixelSize(10, 10), encoder.GetScreenSize());
        }

        [Theory]
        [InlineData(-1, 0, 0, 0)]
        [InlineData(3, 0, 0, 0)]
        [InlineData(0, 0, -10, 0)]
        [InlineData(0, 0, 700000, 0)]
        [InlineData(0, 0, 0, 70000)]
        public void Invalid_Gif_Layout_Throws_Before_Writing(int originX, int originY, int delayMilliseconds, int loopCount)
        {
            using var scope = BindBackend(new FakeBackend(BitmapCodecCapabilities.Encode));
            using var stream = new MemoryStream();

            var encoder = new GifBitmapEncoder { ScreenSize = new PixelSize(4, 4), LoopCount = loopCount };
            var frame = WithOrigin(CreateFrame(2, 2), originX, originY);

            frame.Delay = TimeSpan.FromMilliseconds(delayMilliseconds);
            encoder.Frames.Add(frame);

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void A_Frame_Palette_Should_Map_The_Pixels_Before_The_Backend_Sees_Them()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.Red, Colors.Lime, Colors.Blue });
            var pixels = PixelBuffer.Create(new byte[] { 0, 0, 250, 255, 5, 240, 0, 255, 255, 0, 0, 255 },
                new PixelSize(3, 1), 12, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Palette);

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels, Palette = palette });

            using var view = encoder.LockFramePixels(0, out var resolved);

            Assert.Same(palette, resolved);
            Assert.Equal(PixelFormats.Indexed2, view.Format);
            Assert.Equal(0b01_10_11_00, System.Runtime.InteropServices.Marshal.ReadByte(view.Address));
        }

        [Fact]
        public void An_Indexed_Source_Should_Keep_Its_Palette_Without_A_Frame_Palette()
        {
            var palette = new BitmapPalette(new[] { Colors.Black, Colors.White });
            var pixels = PixelBuffer.Create(new byte[] { 0b1000_0000 }, new PixelSize(2, 1), 1,
                PixelFormats.Indexed1, AlphaFormat.Opaque, palette: palette);
            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });

            using (var view = encoder.LockFramePixels(0, out var resolved))
            {
                Assert.Same(pixels.Palette, resolved);
                Assert.Equal(PixelFormats.Indexed1, view.Format);
            }

            using var expanded = encoder.LockFramePixelsExpanded(0);

            Assert.Equal(PixelFormats.Bgra8888, expanded.Format);
            Assert.Equal(unchecked((int)0xFFFFFFFF), System.Runtime.InteropServices.Marshal.ReadInt32(expanded.Address));
        }

        [Fact]
        public void Gif_Frames_Without_A_Palette_Should_Use_The_Global_Palette()
        {
            var global = new BitmapPalette(new[] { Colors.Black, Colors.White });
            var local = new BitmapPalette(new[] { Colors.Red, Colors.Blue, Colors.Lime });
            var encoder = new GifBitmapEncoder { GlobalPalette = global };

            encoder.Frames.Add(CreateFrame());
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreateFrame().Pixels, Palette = local });

            using (encoder.LockFramePixels(0, out var first))
                Assert.Same(global, first);

            using (encoder.LockFramePixels(1, out var second))
                Assert.Same(local, second);
        }

        [Fact]
        public void A_Frame_Palette_Without_Backend_Capability_Throws()
        {
            var backend = new FakeBackend(BitmapCodecCapabilities.Encode);

            using var scope = BindBackend(backend);
            using var stream = new MemoryStream();

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode | BitmapCodecCapabilities.Palette);
            var frame = CreateFrame();

            frame.Palette = new BitmapPalette(new[] { Colors.Black, Colors.White });
            encoder.Frames.Add(frame);

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void A_Target_That_Cannot_Seek_Should_Be_Buffered_Only_When_The_Codec_Requires_It(
            bool requiresSeek, bool expectBackendSeesTarget)
        {
            var capabilities = BitmapCodecCapabilities.Encode |
                (requiresSeek ? BitmapCodecCapabilities.EncodeRequiresSeekableStream : 0);
            var backend = new FakeBackend(capabilities);

            using var scope = BindBackend(backend);
            using var target = new MemoryStream();
            using var stream = new ForwardOnlyStream(target);

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());
            encoder.Save(stream, TestContext.Current.CancellationToken);

            Assert.Equal(!expectBackendSeesTarget, backend.EncoderImpl.StreamCanSeek);
            Assert.Equal(new byte[] { 42 }, target.ToArray());
        }

        [Fact]
        public void A_Target_That_Cannot_Write_Is_Rejected()
        {
            using var scope = BindBackend(new FakeBackend(BitmapCodecCapabilities.Encode));
            using var stream = new MemoryStream(new byte[16], writable: false);

            var encoder = new TestEncoder(BitmapCodecCapabilities.Encode);

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<ArgumentException>(() => encoder.Save(stream));
        }

        private static BitmapEncoderFrame CreateFrame(int width, int height) => new()
        {
            Pixels = PixelBuffer.Create(new byte[width * height * 4], new PixelSize(width, height), width * 4,
                PixelFormats.Bgra8888, AlphaFormat.Unpremul),
        };

        private static BitmapEncoderFrame WithOrigin(BitmapEncoderFrame frame, int x, int y)
        {
            frame.Origin = new PixelPoint(x, y);

            return frame;
        }

        private sealed class ForwardOnlyStream : Stream
        {
            private readonly Stream _inner;

            public ForwardOnlyStream(Stream inner) => _inner = inner;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        }

        private sealed class FakeFrameSource : IBitmapFrameSource
        {
            public PixelSize PixelSize => new(1, 1);

            public Vector Dpi => new(96, 96);

            public PixelFormat PixelFormat => PixelFormat.Bgra8888;

            public AlphaFormat AlphaFormat => AlphaFormat.Premul;

            public ILockedFramebuffer Lock() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
