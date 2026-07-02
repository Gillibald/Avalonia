using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Failure behavior: corrupt and hostile inputs fail deterministically, before
    /// frame-sized memory is rented, and never leak rentals.
    /// </summary>
    public abstract class RobustnessContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void Truncated_ThrowsDeterministicType_PoolBalanced()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            var first = DecodeAttempt(FixtureImages.TruncatedPng);
            var second = DecodeAttempt(FixtureImages.TruncatedPng);

            Assert.True(first is not null, "Decoding a truncated stream must fail.");
            Assert.True(second is not null, "Decoding a truncated stream must fail on retry too.");
            Assert.Equal(first!.GetType(), second!.GetType());

            Allocator.AssertBalanced();
        }

        [Fact]
        public void CorruptCrc_Deterministic()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            var first = DecodeAttempt(FixtureImages.CorruptCrcPng);
            var second = DecodeAttempt(FixtureImages.CorruptCrcPng);

            // A backend may treat the wrong checksum as fatal or ignore it, but it must
            // do the same thing every time.
            Assert.Equal(first?.GetType(), second?.GetType());

            Allocator.AssertBalanced();
        }

        [Fact]
        public void MaxPixels_RejectsHugeImageBeforeFrameMemory()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            var huge = FixtureImages.Huge16kPng;
            var options = new BitmapDecodeOptions { MaxPixels = 1_000_000 };

            var failure = DecodeAttempt(huge, options);

            Assert.True(failure is not null, "A frame beyond MaxPixels must be rejected.");
            Assert.Equal(0L, Allocator.FrameSizedRents(FrameSizedThreshold(huge)));
            Allocator.AssertBalanced();
        }

        [Fact]
        public void FailureLeavesNoLiveNativeMemory()
        {
            DecodeAttempt(FixtureImages.TruncatedPng);
            DecodeAttempt(FixtureImages.CorruptCrcPng);
            DecodeAttempt(FixtureImages.NotAnImage);

            Allocator.AssertBalanced();
        }

        /// <summary>
        /// Runs a full decode of the fixture and returns the exception it failed with,
        /// or null when the decode succeeded.
        /// </summary>
        private Exception? DecodeAttempt(ImageFixture fixture, BitmapDecodeOptions? options = null)
        {
            try
            {
                using var decoder = CreateDecoder(fixture, options);
                using var frame = decoder.ReadNextFrame();

                if (frame is null)
                    Assert.Fail($"{fixture.Name}: the decoder returned no frame instead of failing.");

                using (frame.Lock())
                {
                }

                return null;
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                return exception;
            }
        }
    }
}
