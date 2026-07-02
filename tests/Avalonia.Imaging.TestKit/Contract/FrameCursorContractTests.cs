using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Frame cursor semantics of multi-frame containers: enumeration to exhaustion,
    /// random access through <see cref="ISeekableFrameDecoder"/> agreeing with the
    /// cursor, and frame independence.
    /// </summary>
    public abstract class FrameCursorContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoMultiFrameFixture = "The backend fixture supplies no multi-frame fixture.";

        [Fact]
        public void ReadNextFrame_YieldsAllFramesThenNull()
        {
            var fixture = RequireMultiFrameFixture(requireSeekable: false);

            using var decoder = Backend.CreateDecoder(fixture.OpenRead(), ownsStream: true);

            for (var i = 0; i < fixture.FrameCount; i++)
            {
                using var frame = decoder.ReadNextFrame();

                Assert.True(frame is not null, $"Frame {i} of {fixture.FrameCount} must exist.");
            }

            // The exhausted cursor answers null, and stays null.
            Assert.Null(decoder.ReadNextFrame());
            Assert.Null(decoder.ReadNextFrame());
        }

        [Fact]
        public void GetFrame_MatchesCursorFrame()
        {
            var fixture = RequireMultiFrameFixture(requireSeekable: true);

            using var decoder = Backend.CreateDecoder(fixture.OpenRead(), ownsStream: true);
            var seekable = Assert.IsAssignableFrom<ISeekableFrameDecoder>(decoder);

            Assert.Equal(fixture.FrameCount, seekable.FrameCount);

            using (var page = seekable.GetFrame(1))
                AssertFrameMatchesFixture(fixture, 1, page);

            // The cursor agrees with random access.
            using (var first = decoder.ReadNextFrame()!)
            using (var second = decoder.ReadNextFrame()!)
            {
                AssertFrameMatchesFixture(fixture, 0, first);
                AssertFrameMatchesFixture(fixture, 1, second);
            }
        }

        [Fact]
        public void GetFrame_OutOfRange_Throws()
        {
            var fixture = RequireMultiFrameFixture(requireSeekable: true);

            using var decoder = Backend.CreateDecoder(fixture.OpenRead(), ownsStream: true);
            var seekable = Assert.IsAssignableFrom<ISeekableFrameDecoder>(decoder);

            Assert.Throws<ArgumentOutOfRangeException>(() => seekable.GetFrame(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => seekable.GetFrame(fixture.FrameCount));
        }

        [Fact]
        public void Frames_IndependentAcrossReads()
        {
            var fixture = RequireMultiFrameFixture(requireSeekable: true);

            using var decoder = Backend.CreateDecoder(fixture.OpenRead(), ownsStream: true);
            var seekable = Assert.IsAssignableFrom<ISeekableFrameDecoder>(decoder);

            var first = seekable.GetFrame(0);

            try
            {
                using (var second = seekable.GetFrame(1))
                    AssertFrameMatchesFixture(fixture, 1, second);

                // Page 0 still locks and decodes after page 1 was read and disposed.
                AssertFrameMatchesFixture(fixture, 0, first);
            }
            finally
            {
                first.Dispose();
            }
        }

        private MultiFrameImageFixture RequireMultiFrameFixture(bool requireSeekable)
        {
            var fixture = Fixture.CreateMultiFrameFixture();

            Assert.SkipWhen(fixture is null, NoMultiFrameFixture);

            var format = TryGetFormat(fixture!.FormatName);

            Assert.SkipWhen(format is not { Decode: true },
                $"The manifest does not declare {fixture.FormatName} decoding.");

            if (requireSeekable)
            {
                Assert.SkipWhen(!format!.SeekableFrames,
                    $"The manifest does not declare seekable {fixture.FormatName} frames.");
            }

            return fixture;
        }

        private void AssertFrameMatchesFixture(MultiFrameImageFixture fixture, int frameIndex,
            IBitmapFrameSource frame)
        {
            Assert.Equal(fixture.FrameSize, frame.PixelSize);

            using var view = frame.Lock();

            var actual = FramebufferPixels.ReadRgba(view);
            var expected = fixture.GetFrameRgba(frameIndex).Span;
            var tolerance = TryGetFormat(fixture.FormatName)?.PixelTolerance ?? 0;

            Assert.Equal(expected.Length, actual.Length);

            for (var offset = 0; offset < expected.Length; offset++)
            {
                if (Math.Abs(actual[offset] - expected[offset]) > tolerance)
                {
                    Assert.Fail(
                        $"Frame {frameIndex} byte {offset}: expected {expected[offset]}, " +
                        $"was {actual[offset]}, tolerance {tolerance}.");
                }
            }
        }
    }
}
