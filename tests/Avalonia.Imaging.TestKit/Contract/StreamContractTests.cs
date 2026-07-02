using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Stream handling: non-seekable and partial-read streams decode where declared,
    /// and stream ownership is honored exactly.
    /// </summary>
    public abstract class StreamContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void NonSeekable_DecodesStreamableFormats()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                if (TryGetFormat(fixture.ExpectedFormatName) is not { NonSeekableDecode: true })
                    continue;

                tested++;

                using var stream = new NonSeekableStream(fixture.OpenRead());
                using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
                using var frame = ReadFirstFrame(decoder);

                AssertFrameMatchesReference(fixture, frame);
            }

            Assert.SkipWhen(tested == 0, "The manifest declares no generated format decodable from a non-seekable stream.");
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
