using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Stream handling: the decoder secures its own stable encoded source at creation,
    /// so non-seekable and partial-read streams decode universally, the caller's stream
    /// is free once CreateDecoder returns, and ownership is honored exactly.
    /// </summary>
    public abstract class StreamContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void NonSeekable_Decodes()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using var stream = new NonSeekableStream(fixture.OpenRead());
                using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
                using var frame = ReadFirstFrame(decoder);

                AssertFrameMatchesReference(fixture, frame);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AsyncOnly_DecodesThroughTheAsyncEntryPoints(bool seekable)
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            using var scope = LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));

            using (var decoder = await BitmapDecoder.CreateAsync(new AsyncOnlyStream(fixture.OpenRead(), seekable),
                       ownsStream: true, cancellationToken: TestContext.Current.CancellationToken))
            using (var frame = decoder.ReadNextFrame()!)
            using (var view = frame.Lock())
            {
                AssertRgbaMatchesReference(fixture, FramebufferPixels.ReadRgba(view), view.AlphaFormat);
            }

            using var stream = new AsyncOnlyStream(fixture.OpenRead(), seekable);
            using var bitmap = await Bitmap.DecodeAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(fixture.ExpectedSize, bitmap.PixelSize);
        }

        [Fact]
        public async Task AsyncOnly_EncodesThroughSaveAsync()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Encode: true },
                "The manifest does not declare PNG encoding.");

            var fixture = RequireAnyDecodableReferenceFixture();

            using var scope = LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));
            using var source = BitmapDecoder.Create(fixture.OpenRead());
            using var frame = source.ReadNextFrame()!;

            var encoder = new PngBitmapEncoder();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = frame.ToPixelBuffer() });

            using var target = new MemoryStream();

            using (var stream = new AsyncOnlyStream(target, seekable: false))
                await encoder.SaveAsync(stream, TestContext.Current.CancellationToken);

            using var decoded = BitmapDecoder.Create(new MemoryStream(target.ToArray()));

            Assert.Equal(fixture.ExpectedSize, decoded.ReadNextFrame()!.PixelSize);
        }

        [Fact]
        public async Task AsyncOnly_EndlessStreamStopsAtTheDecodeBudget()
        {
            using var scope = LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));

            // The buffer the async entry points fill is capped by the decode limit.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BitmapDecoder.CreateAsync(
                AsyncOnlyStream.Endless(), new BitmapDecodeOptions { MaxPixels = 1 }, ownsStream: true,
                TestContext.Current.CancellationToken));

            Assert.Contains("decode limit", error.Message);
        }

        [Fact]
        public void Trickle_PartialReadsHandled()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using var stream = new TrickleStream(fixture.OpenRead());
                using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
                using var frame = ReadFirstFrame(decoder);

                AssertFrameMatchesReference(fixture, frame);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void EncodedOwnership_SeekableCallerStreamFreeAfterCreate()
        {
            var fixture = RequireAnyDecodableReferenceFixture();
            var stream = fixture.OpenRead();

            using var decoder = Backend.CreateDecoder(stream, ownsStream: false);

            // The decoder owns a stable copy of the encoded data; the caller's stream
            // is entirely free once CreateDecoder returns.
            stream.Dispose();

            using var frame = ReadFirstFrame(decoder);

            AssertFrameMatchesReference(fixture, frame);
        }

        [Fact]
        public void EncodedOwnership_NonSeekableCallerStreamFreeAfterCreate()
        {
            var fixture = RequireAnyDecodableReferenceFixture();
            var stream = new NonSeekableStream(fixture.OpenRead());

            using var decoder = Backend.CreateDecoder(stream, ownsStream: false);

            stream.Dispose();

            using var frame = ReadFirstFrame(decoder);

            AssertFrameMatchesReference(fixture, frame);
        }

        [Fact]
        public void OwnsStream_DisposedExactlyWhenTold()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            using (var notOwned = new DisposalTrackingStream(fixture.OpenRead()))
            {
                using (var decoder = Backend.CreateDecoder(notOwned, ownsStream: false))
                using (var frame = ReadFirstFrame(decoder))
                using (frame.Lock())
                {
                }

                Assert.False(notOwned.IsDisposed, "A decoder must not dispose a stream it does not own.");
            }

            var owned = new DisposalTrackingStream(fixture.OpenRead());

            using (var decoder = Backend.CreateDecoder(owned, ownsStream: true))
            using (var frame = ReadFirstFrame(decoder))
            using (frame.Lock())
            {
            }

            Assert.True(owned.IsDisposed, "Disposing a decoder that owns its stream must dispose the stream.");
        }
    }
}
