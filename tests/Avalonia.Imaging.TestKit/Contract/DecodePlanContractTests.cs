using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Decode plan semantics: target size, source region, target formats and EXIF
    /// orientation produce exactly the planned output on every backend. Whether a plan
    /// part is fused into the decode or applied by the shared pipeline is invisible.
    /// </summary>
    public abstract class DecodePlanContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngDecode = "The manifest does not declare PNG decoding.";
        private const string NoOrientedFixture = "The backend fixture supplies no oriented fixture.";

        [Fact]
        public void TargetSize_ExactOutput()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            // PNG declares no FusedDecode on any backend so far; the exact output is
            // guaranteed regardless of what a codec fuses.
            var fixture = FixtureImages.Gradient16Png;

            using (var decoder = CreateDecoder(fixture, new BitmapDecodeOptions { TargetSize = new PixelSize(8, 8) }))
            using (var frame = ReadFirstFrame(decoder))
            using (var view = frame.Lock())
            {
                Assert.Equal(new PixelSize(8, 8), frame.PixelSize);
                Assert.Equal(new PixelSize(8, 8), view.Size);
            }

            // A zero dimension completes from the aspect ratio.
            using (var decoder = CreateDecoder(fixture, new BitmapDecodeOptions { TargetSize = new PixelSize(8, 0) }))
            using (var frame = ReadFirstFrame(decoder))
            using (var view = frame.Lock())
            {
                Assert.Equal(new PixelSize(8, 8), frame.PixelSize);
                Assert.Equal(new PixelSize(8, 8), view.Size);
            }
        }

        [Fact]
        public void SourceRegion_MatchesReferenceCrop()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.Gradient16Png;
            var region = new PixelRect(4, 2, 8, 6);

            using var decoder = CreateDecoder(fixture, new BitmapDecodeOptions { SourceRegion = region });
            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(new PixelSize(8, 6), frame.PixelSize);

            using var view = frame.Lock();

            var actual = FramebufferPixels.ReadRgba(view);
            var reference = fixture.ExpectedRgba.Span;
            var tolerance = TryGetFormat(fixture.ExpectedFormatName)?.PixelTolerance ?? 0;

            for (var y = 0; y < region.Height; y++)
            {
                for (var x = 0; x < region.Width; x++)
                {
                    var expectedOffset = ((region.Y + y) * fixture.ExpectedSize.Width + region.X + x) * 4;
                    var actualOffset = (y * region.Width + x) * 4;

                    AssertChannelsClose(reference, expectedOffset, actual, actualOffset, tolerance, x, y);
                }
            }
        }

        [Fact]
        public void TargetFormat565_ProducesPacked565()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.Gradient16Png;

            using var decoder = CreateDecoder(fixture, new BitmapDecodeOptions { TargetFormat = PixelFormats.Rgb565 });
            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(PixelFormats.Rgb565, frame.PixelFormat);

            using var view = frame.Lock();

            Assert.Equal(PixelFormats.Rgb565, view.Format);
            Assert.True(view.RowBytes >= fixture.ExpectedSize.Width * 2,
                "A 565 row stores two bytes per pixel.");

            // Packing to 5/6-bit channels may shift each channel by up to one 8-bit
            // step of the coarser grid.
            const int packingTolerance = 8;

            var actual = FramebufferPixels.ReadRgba(view);
            var reference = fixture.ExpectedRgba.Span;
            var tolerance = packingTolerance + (TryGetFormat(fixture.ExpectedFormatName)?.PixelTolerance ?? 0);
            var width = fixture.ExpectedSize.Width;

            for (var offset = 0; offset < reference.Length; offset += 4)
                AssertChannelsClose(reference, offset, actual, offset, tolerance, offset / 4 % width, offset / 4 / width);
        }

        [Fact]
        public void TargetAlpha_Premultiplies()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            var fixture = FixtureImages.AlphaGradientPng;

            using var decoder = CreateDecoder(fixture, new BitmapDecodeOptions { TargetAlphaFormat = AlphaFormat.Premul });
            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(AlphaFormat.Premul, frame.AlphaFormat);

            // The shared assertion premultiplies the straight reference when the view
            // reports premultiplied alpha; the fixture levels make that integral.
            AssertFrameMatchesReference(fixture, frame);
        }

        [Fact]
        public void ExifOrientation_AppliedAndSizeSwapped()
        {
            var oriented = RequireOrientedFixture();

            using var decoder = Backend.CreateDecoder(oriented.OpenRead(), ownsStream: true);

            // Info reports the raw stored size; the frame is display-oriented.
            Assert.Equal(oriented.RawSize, decoder.Info.PixelSize);

            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(oriented.OrientedSize, frame.PixelSize);

            using var view = frame.Lock();

            var actual = FramebufferPixels.ReadRgba(view);
            var expected = oriented.OrientedRgba.Span;
            var width = oriented.OrientedSize.Width;

            Assert.Equal(expected.Length, actual.Length);

            for (var offset = 0; offset < expected.Length; offset += 4)
            {
                AssertChannelsClose(expected, offset, actual, offset, oriented.PixelTolerance,
                    offset / 4 % width, offset / 4 / width);
            }
        }

        [Fact]
        public void RespectExifOrientation_False_KeepsRawDims()
        {
            var oriented = RequireOrientedFixture();
            var options = new BitmapDecodeOptions { RespectExifOrientation = false };

            using var decoder = Backend.CreateDecoder(oriented.OpenRead(), ownsStream: true, options);

            Assert.Equal(oriented.RawSize, decoder.Info.PixelSize);

            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(oriented.RawSize, frame.PixelSize);

            using var view = frame.Lock();

            Assert.Equal(oriented.RawSize, view.Size);
        }

        [Fact]
        public void Orientation_ComposedWithTargetSize()
        {
            var oriented = RequireOrientedFixture();

            // The plan target is expressed in oriented display space.
            var target = new PixelSize(
                Math.Max(1, oriented.OrientedSize.Width / 2),
                Math.Max(1, oriented.OrientedSize.Height / 2));
            var options = new BitmapDecodeOptions { TargetSize = target };

            using var decoder = Backend.CreateDecoder(oriented.OpenRead(), ownsStream: true, options);
            using var frame = ReadFirstFrame(decoder);

            Assert.Equal(target, frame.PixelSize);

            using var view = frame.Lock();

            Assert.Equal(target, view.Size);
        }

        private OrientedImageFixture RequireOrientedFixture()
        {
            var oriented = Fixture.CreateOrientedFixture();

            Assert.SkipWhen(oriented is null, NoOrientedFixture);
            Assert.SkipWhen(TryGetFormat(oriented!.FormatName) is not { Decode: true },
                $"The manifest does not declare {oriented.FormatName} decoding.");

            return oriented;
        }

        private static void AssertChannelsClose(ReadOnlySpan<byte> expected, int expectedOffset,
            byte[] actual, int actualOffset, int tolerance, int x, int y)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                if (Math.Abs(actual[actualOffset + channel] - expected[expectedOffset + channel]) > tolerance)
                {
                    Assert.Fail(
                        $"Pixel ({x},{y}) channel {channel}: expected {expected[expectedOffset + channel]}, " +
                        $"was {actual[actualOffset + channel]}, tolerance {tolerance}.");
                }
            }
        }
    }
}
