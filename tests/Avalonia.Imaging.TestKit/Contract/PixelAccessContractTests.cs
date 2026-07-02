using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Pixel access: decoded pixels match the fixture references, locks are stable and
    /// correctly strided, and pixel snapshots outlive their frame.
    /// </summary>
    public abstract class PixelAccessContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void DecodedPixels_MatchReference()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using var decoder = CreateDecoder(fixture);
                using var frame = ReadFirstFrame(decoder);

                Assert.Equal(fixture.ExpectedSize, frame.PixelSize);
                AssertFrameMatchesReference(fixture, frame);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void Lock_SecondCallSameAddress()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            using var decoder = CreateDecoder(fixture);
            using var frame = ReadFirstFrame(decoder);

            IntPtr first;

            using (var view = frame.Lock())
                first = view.Address;

            using (var view = frame.Lock())
                Assert.Equal(first, view.Address);
        }

        [Fact]
        public void StrideAtLeastMinRowBytes()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                using var decoder = CreateDecoder(fixture);
                using var frame = ReadFirstFrame(decoder);
                using var view = frame.Lock();

                var minRowBytes = (view.Size.Width * view.Format.BitsPerPixel + 7) / 8;

                Assert.True(view.RowBytes >= minRowBytes,
                    $"{fixture.Name}: stride {view.RowBytes} is smaller than one row of pixels ({minRowBytes}).");
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }

        [Fact]
        public void PixelBuffer_SurvivesFrameDispose()
        {
            var tested = 0;

            foreach (var fixture in DecodableReferenceFixtures())
            {
                tested++;

                PixelBuffer buffer;

                using (var decoder = CreateDecoder(fixture))
                using (var frame = ReadFirstFrame(decoder))
                using (var view = frame.Lock())
                {
                    buffer = PixelBuffer.CopyFrom(view);
                }

                using var pinned = buffer.Lock();

                AssertRgbaMatchesReference(fixture, FramebufferPixels.ReadRgba(pinned), buffer.AlphaFormat);
            }

            Assert.SkipWhen(tested == 0, NoDecodableReferenceFixtures);
        }
    }
}
