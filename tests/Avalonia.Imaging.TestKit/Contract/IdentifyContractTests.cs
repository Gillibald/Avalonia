using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Header-only identification: size and format come from the header, never from a
    /// pixel decode, and identification does not lose a non-seekable stream's data.
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
        public void NonSeekable_Identify()
        {
            var format = TryGetFormat(FixtureImages.PngFormatName);

            Assert.SkipWhen(format is not { Decode: true }, "The manifest does not declare PNG decoding.");
            Assert.SkipWhen(!format!.NonSeekableDecode, "The manifest does not declare non-seekable PNG decoding.");

            var fixture = FixtureImages.Rgb4x4Png;

            using var stream = new NonSeekableStream(fixture.OpenRead());

            Assert.True(Backend.TryIdentify(stream, out var info), "TryIdentify must work on a non-seekable stream.");
            Assert.Equal(fixture.ExpectedSize, info.PixelSize);

            // Identification must not consume the data: the same stream still decodes.
            using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
            using var frame = ReadFirstFrame(decoder);

            AssertFrameMatchesReference(fixture, frame);
        }
    }
}
