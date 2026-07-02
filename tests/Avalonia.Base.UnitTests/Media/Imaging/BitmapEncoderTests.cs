using System;
using System.Collections.Generic;
using System.IO;
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

            public void Encode(BitmapEncoder encoder, Stream stream)
            {
                Encoded = encoder;
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

        private sealed class TestMetadata : BitmapMetadata
        {
            public override string Format => "PNG";

            public override object? GetQuery(string query) => null;

            public override void SetQuery(string query, object? value) { }

            public override bool ContainsQuery(string query) => false;

            public override void RemoveQuery(string query) { }

            public override BitmapMetadata Clone() => new TestMetadata();
        }

        private static IDisposable BindBackend(FakeBackend backend)
        {
            var scope = AvaloniaLocator.EnterScope();

            ImagingBackend.Register(backend);

            return scope;
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
                Metadata = new TestMetadata(),
            };

            encoder.Frames.Add(CreateFrame());

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
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
            encoder.Save(stream);

            Assert.Same(encoder, backend.EncoderImpl.Encoded);
            Assert.Equal(1, stream.Length);
        }
    }
}
