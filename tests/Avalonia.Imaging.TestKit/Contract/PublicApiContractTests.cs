using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// The public BitmapDecoder/BitmapFrame/Bitmap surface over the backend under test,
    /// with the kit's fake render interface standing in for a render backend. Only the
    /// framebuffer-install bridge is exercised; entry points that go through other
    /// render-interface members (Bitmap(stream), DecodeToWidth) stay per backend.
    /// </summary>
    public abstract class PublicApiContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngDecode = "The manifest does not declare PNG decoding.";
        private const string NoPngEncodeDecode = "The manifest does not declare PNG encode plus decode.";
        private const string NoPngMetadata = "The manifest does not declare PNG metadata support.";

        [Fact]
        public void Identify_PublicSurface()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.Rgb4x4Png;

            using var scope = BindPlatform();

            using (var stream = fixture.OpenRead())
            {
                var info = BitmapDecoder.Identify(stream);

                Assert.Equal(fixture.ExpectedFormatName, info.FormatName);
                Assert.Equal(fixture.ExpectedSize, info.PixelSize);
                Assert.Equal(0, stream.Position);
            }

            // Forward-only streams throw by default and sniff via the explicit opt-in.
            using (var forwardOnly = new NonSeekableStream(fixture.OpenRead()))
            {
                Assert.Throws<ArgumentException>(() => BitmapDecoder.TryIdentify(forwardOnly, out _));
            }

            using (var forwardOnly = new NonSeekableStream(fixture.OpenRead()))
            {
                Assert.True(BitmapDecoder.TryIdentify(forwardOnly, out var sniffed,
                    IdentifyStreamBehavior.ConsumePrefix));
                Assert.Equal(fixture.ExpectedSize, sniffed.PixelSize);
            }

            // The opt-in never consumes a seekable stream.
            using (var seekable = fixture.OpenRead())
            {
                Assert.True(BitmapDecoder.TryIdentify(seekable, out _, IdentifyStreamBehavior.ConsumePrefix));
                Assert.Equal(0, seekable.Position);
            }
        }

        [Fact]
        public void DecodePlan_AppliesThroughPublicDecode()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var stream = FixtureImages.Gradient16Png.OpenRead();

            using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions
            {
                TargetSize = new PixelSize(8, 8),
                TargetFormat = PixelFormats.Rgb565,
            });

            Assert.Equal(new PixelSize(8, 8), bitmap.PixelSize);
            Assert.Equal(PixelFormats.Rgb565, bitmap.Format);

            // The plan output is what got installed, zero-copy.
            Assert.Equal(new PixelSize(8, 8), Fixture.RenderInstall.Size);
            Assert.Equal(PixelFormats.Rgb565, Fixture.RenderInstall.Format);
        }

        [Fact]
        public void SaveWithEncoder_RoundTrips()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Encode: true },
                NoPngEncodeDecode);

            var fixture = FixtureImages.Rgb4x4Png;

            using var scope = BindPlatform();
            using var source = fixture.OpenRead();
            using var original = Bitmap.Decode(source);
            using var encoded = new MemoryStream();

            original.Save(encoded, new PngBitmapEncoder());

            Assert.True(encoded.Length > 0, "Encoding produced no bytes.");

            encoded.Position = 0;

            using var roundTripped = Bitmap.Decode(encoded);

            Assert.Equal(original.PixelSize, roundTripped.PixelSize);
            Assert.Equal(ReadInstalledRgba(original), ReadInstalledRgba(roundTripped));
        }

        [Fact]
        public void LegacySave_ProducesPng()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Encode: true },
                NoPngEncodeDecode);

            var fixture = FixtureImages.Rgb4x4Png;

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(fixture.OpenRead(), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;
            using var bitmap = frame.ToBitmap();
            using var saved = new MemoryStream();

            // The legacy overload routes through the active backend and stays PNG.
            bitmap.Save(saved);

            Assert.True(saved.Length > 0, "The legacy save produced no bytes.");

            saved.Position = 0;

            using var decoded = BitmapDecoder.Create(saved);

            Assert.Equal(FixtureImages.PngFormatName, decoded.CodecInfo.FormatName);
            Assert.Equal(fixture.ExpectedSize, decoded.Info.PixelSize);
        }

        [Fact]
        public void EncodeTransform_ThroughPublicSave()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Encode: true },
                NoPngEncodeDecode);

            using var scope = BindPlatform();
            using var stream = FixtureImages.Gradient16Png.OpenRead();

            // A non-square source (the upper half of the gradient) makes the quarter
            // turn observable in the dimensions.
            using var bitmap = Bitmap.Decode(stream, new BitmapDecodeOptions
            {
                SourceRegion = new PixelRect(0, 0, 16, 8),
            });

            Assert.Equal(new PixelSize(16, 8), bitmap.PixelSize);

            using var saved = new MemoryStream();

            bitmap.Save(saved, new PngBitmapEncoder
            {
                Transform = new BitmapTransform(BitmapRotation.Rotate90, FlipHorizontal: false, FlipVertical: false),
            });

            saved.Position = 0;

            using var rotated = Bitmap.Decode(saved);

            Assert.Equal(new PixelSize(8, 16), rotated.PixelSize);
        }

        [Fact]
        public void PixelBufferToBitmap_RoundTrips()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.Rgb4x4Png;

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(fixture.OpenRead(), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            var buffer = frame.ToPixelBuffer();

            Assert.Equal(fixture.ExpectedSize, buffer.Size);

            using var bitmap = buffer.ToBitmap();

            Assert.Equal(fixture.ExpectedSize, bitmap.PixelSize);

            var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(bitmap.PlatformImpl.Item);

            using var view = readable.Lock();

            AssertRgbaMatchesReference(fixture, FramebufferPixels.ReadRgba(view), view.AlphaFormat);
        }

        [Fact]
        public async Task CreateAsync_HonorsCancellation()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.Rgb4x4Png;

            using var scope = BindPlatform();

            using (var decoder = await BitmapDecoder.CreateAsync(fixture.OpenRead(), ownsStream: true))
            {
                Assert.Equal(fixture.ExpectedSize, decoder.Info.PixelSize);
            }

            using var cancelled = new CancellationTokenSource();

            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BitmapDecoder.CreateAsync(fixture.OpenRead(), ownsStream: true,
                    cancellationToken: cancelled.Token));
        }

        [Fact]
        public void Metadata_ExposedWhenDeclared()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Metadata: true },
                NoPngMetadata);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(FixtureImages.PngWithTextMetadata.OpenRead(), ownsStream: true);
            using var frame = decoder.ReadNextFrame()!;

            Assert.True(frame.TryGetMetadata(out var metadata));
            Assert.Equal(FixtureImages.PngTextValue,
                metadata.GetQuery("/text/{str=" + FixtureImages.PngTextKeyword + "}"));

            // The metadata rides along onto the realized bitmap.
            using var bitmap = frame.ToBitmap();

            Assert.NotNull(bitmap.Metadata);
        }

        private IDisposable BindPlatform() =>
            LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));

        private static byte[] ReadInstalledRgba(Bitmap bitmap)
        {
            var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(bitmap.PlatformImpl.Item);

            using var framebuffer = readable.Lock();

            return FramebufferPixels.ReadRgba(framebuffer);
        }
    }
}
