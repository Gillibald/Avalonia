using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Header-only identification: size and format come from the header, never from a
    /// pixel decode. Identify requires a seekable source or in-memory bytes; a
    /// forward-only stream answers through the decoder's Info instead.
    /// </summary>
    public abstract class IdentifyContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void SizeFormat_FromHeaderOnly()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using var stream = fixture.OpenRead();

                Assert.True(Backend.TryIdentify(stream, out var info), $"TryIdentify must recognize {fixture.Name}.");
                Assert.Equal(fixture.ExpectedFormatName, info.FormatName);
                Assert.Equal(fixture.ExpectedSize, info.PixelSize);

                if (info.HasAlpha is { } hasAlpha)
                    Assert.Equal(fixture.ExpectedHasAlpha, hasAlpha);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void SpanIdentify_ReturnsDescriptor()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                Assert.True(Backend.TryIdentify(fixture.EncodedBytes.Span, out var info),
                    $"TryIdentify must recognize {fixture.Name} from bytes.");
                Assert.Equal(fixture.ExpectedFormatName, info.FormatName);
                Assert.Equal(fixture.ExpectedSize, info.PixelSize);

                if (info.HasAlpha is { } hasAlpha)
                    Assert.Equal(fixture.ExpectedHasAlpha, hasAlpha);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void HugeDims_InfoWithoutDecode()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            var huge = FixtureImages.Huge16kPng;

            using var stream = huge.OpenRead();

            Assert.True(Backend.TryIdentify(stream, out var info), "TryIdentify must recognize the huge PNG.");
            Assert.Equal(huge.ExpectedSize, info.PixelSize);
            Assert.Equal(0L, Allocator.FrameSizedRents(FrameSizedThreshold(huge)));
        }

        [Fact]
        public void NonSeekable_IdentifyThrows_DecoderInfoAnswers()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            var fixture = FixtureImages.Rgb4x4Png;

            using var stream = new NonSeekableStream(fixture.OpenRead());

            // Identify cannot read a forward-only stream without consuming it.
            Assert.Throws<ArgumentException>(() => Backend.TryIdentify(stream, out _));

            // The header facts come from a decoder instead, without advancing the frame
            // cursor: frame 0 still decodes afterwards.
            using var decoder = Backend.CreateDecoder(stream, ownsStream: false);

            Assert.Equal(fixture.ExpectedFormatName, decoder.Info.FormatName);
            Assert.Equal(fixture.ExpectedSize, decoder.Info.PixelSize);

            using var frame = ReadFirstFrame(decoder);

            AssertFrameMatchesReference(fixture, frame);
        }
    }
}
