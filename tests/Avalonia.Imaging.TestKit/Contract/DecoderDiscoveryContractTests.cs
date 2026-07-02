using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Format discovery: unknown data is rejected naming the backend, every declared
    /// decode format is detected, and discovery itself never pays for pixels.
    /// </summary>
    public abstract class DecoderDiscoveryContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void UnknownData_ReturnsNoDecoder()
        {
            using (var stream = FixtureImages.NotAnImage.OpenRead())
                Assert.False(Backend.TryIdentify(stream, out _), "TryIdentify must return false for unknown data.");

            using var retry = FixtureImages.NotAnImage.OpenRead();

            var exception = Assert.Throws<NotSupportedException>(() => Backend.CreateDecoder(retry, ownsStream: false));

            Assert.Contains(Backend.Name, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void EachManifestDecodeFormat_IsDiscovered()
        {
            var tested = 0;

            foreach (var fixture in new[] { FixtureImages.Rgb4x4Png, FixtureImages.Rgb4x4Bmp })
            {
                if (TryGetFormat(fixture.ExpectedFormatName) is not { Decode: true })
                    continue;

                tested++;

                using (var stream = fixture.OpenRead())
                {
                    Assert.True(Backend.TryIdentify(stream, out var info), $"TryIdentify must recognize {fixture.Name}.");
                    Assert.Equal(fixture.ExpectedFormatName, info.FormatName);
                }

                using var decoder = CreateDecoder(fixture);

                Assert.Equal(fixture.ExpectedFormatName, decoder.CodecInfo.FormatName);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void Discovery_AllocatesNoFrameMemory()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using (var stream = fixture.OpenRead())
                    Backend.TryIdentify(stream, out _);

                using (CreateDecoder(fixture))
                {
                }

                Assert.Equal(0L, Allocator.FrameSizedRents(FrameSizedThreshold(fixture)));
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }
    }
}
