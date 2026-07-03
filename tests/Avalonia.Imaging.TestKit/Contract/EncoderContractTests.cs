using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Encoding: declared encode formats round-trip pixels, unsupported formats fail
    /// fast naming the backend, and frame-set validation runs before any byte is written.
    /// </summary>
    public abstract class EncoderContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private static readonly EncoderFormat[] s_encoderFormats =
        {
            new(FixtureImages.PngFormatName, BitmapContainerFormats.Png, () => new PngBitmapEncoder(), FixtureImages.Rgb4x4Png),
            new(FixtureImages.BmpFormatName, BitmapContainerFormats.Bmp, () => new BmpBitmapEncoder(), FixtureImages.Rgb4x4Bmp),
            new("JPEG", BitmapContainerFormats.Jpeg, () => new JpegBitmapEncoder(), null),
            new("GIF", BitmapContainerFormats.Gif, () => new GifBitmapEncoder(), null),
            new("TIFF", BitmapContainerFormats.Tiff, () => new TiffBitmapEncoder(), null),
            new("WEBP", BitmapContainerFormats.Webp, () => new WebpBitmapEncoder(), null),
        };

        [Fact]
        public void EncodeDecode_RoundTripsPixels()
        {
            var tested = 0;

            foreach (var entry in s_encoderFormats)
            {
                if (entry.RoundTripReference is null)
                    continue;

                if (TryGetFormat(entry.FormatName) is not { Encode: true, Decode: true })
                    continue;

                tested++;

                var reference = entry.RoundTripReference;
                var encoder = entry.Create();

                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(reference) });

                using var stream = new MemoryStream();

                Backend.CreateEncoder(entry.ContainerFormat).Encode(encoder, stream);

                Assert.True(stream.Length > 0, $"Encoding {entry.FormatName} produced no bytes.");

                stream.Position = 0;

                using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
                using var frame = ReadFirstFrame(decoder);

                AssertFrameMatchesReference(reference, frame);
            }

            Assert.SkipWhen(tested == 0, "The manifest declares no lossless encode format with a generated reference.");
        }

        [Theory]
        [InlineData(BitmapRotation.Rotate90, false, false)]
        [InlineData(BitmapRotation.None, true, false)]
        public void EncodeTransform_TransformsPixels(BitmapRotation rotation, bool flipHorizontal, bool flipVertical)
        {
            // Transform-on-encode is guaranteed on every backend: natively where the
            // codec can, through the shared software path otherwise. PNG keeps the
            // comparison lossless.
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Encode: true, Decode: true },
                "The manifest does not declare PNG encode plus decode.");

            var source = FixtureImages.Rgb4x4Png;
            var transform = new BitmapTransform(rotation, flipHorizontal, flipVertical);
            var encoder = new PngBitmapEncoder { Transform = transform };

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = CreatePixelBuffer(source) });

            using var scope = LocatorScope.With(Backend);
            using var stream = new MemoryStream();

            encoder.Save(stream);

            stream.Position = 0;

            using var decoder = Backend.CreateDecoder(stream, ownsStream: false);
            using var frame = ReadFirstFrame(decoder);
            using var view = frame.Lock();

            var expected = TransformReference(source.ExpectedRgba.Span, source.ExpectedSize,
                rotation, flipHorizontal, flipVertical, out var expectedSize);

            Assert.Equal(expectedSize, view.Size);

            var actual = FramebufferPixels.ReadRgba(view);

            Assert.Equal(expected, actual);
        }

        private static byte[] TransformReference(ReadOnlySpan<byte> rgba, PixelSize size,
            BitmapRotation rotation, bool flipHorizontal, bool flipVertical, out PixelSize resultSize)
        {
            // Reference implementation of the documented order: rotation, then the
            // horizontal flip, then the vertical flip.
            var (width, height, pixels) = (size.Width, size.Height, rgba.ToArray());

            for (var steps = (int)rotation / 90; steps > 0; steps--)
            {
                var rotated = new byte[pixels.Length];

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        // (x, y) lands at (height - 1 - y, x) in the rotated image.
                        Array.Copy(pixels, (y * width + x) * 4, rotated, ((x * height) + (height - 1 - y)) * 4, 4);
                    }
                }

                pixels = rotated;
                (width, height) = (height, width);
            }

            if (flipHorizontal)
            {
                var flipped = new byte[pixels.Length];

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Array.Copy(pixels, (y * width + x) * 4, flipped, (y * width + (width - 1 - x)) * 4, 4);
                    }
                }

                pixels = flipped;
            }

            if (flipVertical)
            {
                var flipped = new byte[pixels.Length];

                for (var y = 0; y < height; y++)
                {
                    Array.Copy(pixels, y * width * 4, flipped, (height - 1 - y) * width * 4, width * 4);
                }

                pixels = flipped;
            }

            resultSize = new PixelSize(width, height);

            return pixels;
        }

        [Fact]
        public void UnsupportedEncode_FailsFastNamingBackend()
        {
            // A fixed container GUID no backend knows.
            var unknown = new Guid("f31d4bf6-6d54-4f0f-a2a3-7a4c56313f5e");
            var exception = Assert.Throws<NotSupportedException>(() => Backend.CreateEncoder(unknown));

            Assert.Contains(Backend.Name, exception.Message, StringComparison.Ordinal);

            // Formats the manifest declares without encode support must fail the same way.
            foreach (var entry in s_encoderFormats)
            {
                if (TryGetFormat(entry.FormatName) is not { Encode: false })
                    continue;

                var declared = Assert.Throws<NotSupportedException>(() => Backend.CreateEncoder(entry.ContainerFormat));

                Assert.Contains(Backend.Name, declared.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Save_ZeroFrames_Throws()
        {
            using var scope = LocatorScope.With(Backend);

            var encoder = new PngBitmapEncoder();

            using var stream = new MemoryStream();

            Assert.Throws<InvalidOperationException>(() => encoder.Save(stream));
            Assert.Equal(0L, stream.Length);
        }

        [Fact]
        public void MultiFrame_WithoutMultiFrameEncode_Throws()
        {
            EncoderFormat? singleFrameOnly = null;

            foreach (var entry in s_encoderFormats)
            {
                if (TryGetFormat(entry.FormatName) is { Encode: true, MultiFrameEncode: false })
                {
                    singleFrameOnly = entry;
                    break;
                }
            }

            Assert.SkipWhen(singleFrameOnly is null, "The manifest declares no single-frame-only encode format.");

            var pixels = CreatePixelBuffer(FixtureImages.Rgb4x4Png);
            var encoder = singleFrameOnly!.Create();

            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });
            encoder.Frames.Add(new BitmapEncoderFrame { Pixels = pixels });

            using var scope = LocatorScope.With(Backend);
            using var stream = new MemoryStream();

            Assert.Throws<NotSupportedException>(() => encoder.Save(stream));
            Assert.Equal(0L, stream.Length);
        }

        private static PixelBuffer CreatePixelBuffer(ImageFixture reference)
        {
            var pixels = reference.ExpectedRgba.ToArray();
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                using var framebuffer = new RgbaFramebuffer(handle.AddrOfPinnedObject(), reference.ExpectedSize);

                return PixelBuffer.CopyFrom(framebuffer);
            }
            finally
            {
                handle.Free();
            }
        }

        private sealed class RgbaFramebuffer : ILockedFramebuffer
        {
            public RgbaFramebuffer(IntPtr address, PixelSize size)
            {
                Address = address;
                Size = size;
            }

            public IntPtr Address { get; }

            public PixelSize Size { get; }

            public int RowBytes => Size.Width * 4;

            public Vector Dpi => new(96, 96);

            public PixelFormat Format => PixelFormats.Rgba8888;

            public AlphaFormat AlphaFormat => AlphaFormat.Unpremul;

            public void Dispose()
            {
            }
        }

        private sealed record EncoderFormat(
            string FormatName,
            Guid ContainerFormat,
            Func<BitmapEncoder> Create,
            ImageFixture? RoundTripReference);
    }
}
