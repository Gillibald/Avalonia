using Avalonia.Imaging.TestKit.Fixtures;
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
