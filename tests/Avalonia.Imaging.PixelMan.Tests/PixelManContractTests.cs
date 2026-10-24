using System.IO;
using Avalonia.Imaging.PixelMan;
using Avalonia.Imaging.TestKit.Contract;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.PixelMan;
using Avalonia.Platform;
using Xunit;

// Backend-specific tests bind the imaging backend through the process-global locator,
// so the suite runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Avalonia.Imaging.PixelMan.Tests
{
    /// <summary>
    /// Runs the backend-agnostic imaging contract suite against the PixelMan backend.
    /// </summary>
    public sealed class PixelManCodecFixture : CodecBackendFixture
    {
        public override BackendManifest Manifest { get; } = new()
        {
            Formats = new[]
            {
                new FormatManifest("PNG")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    Palette = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                    FusedParts = FusedDecodeParts.Scale,
                },
                new FormatManifest("JPEG")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    CopyBudget = 0,
                    PixelTolerance = 3,
                    FusedParts = FusedDecodeParts.Scale,
                },
                new FormatManifest("GIF")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    Palette = true,
                    Animation = true,
                    MultiFrameEncode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("BMP")
                {
                    Decode = true,
                    Encode = true,
                    Palette = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("TIFF")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    Palette = true,
                    SeekableFrames = true,
                    MultiFrameEncode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
            },
            SupportsCancellation = true,
        };

        public override IImagingBackend CreateBackend() => new PixelManImagingBackend(Allocator);

        public override OrientedImageFixture? CreateOrientedFixture()
        {
            // Raw 8x4 grayscale halves tagged EXIF orientation 6: the upright display is 4x8
            // with the dark half on top. Quality 100 keeps the step edge nearly exact.
            var pixels = new byte[8 * 4 * 3];

            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var value = (byte)(x < 4 ? 10 : 240);
                    var offset = (y * 8 + x) * 3;

                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                }
            }

            using var frame = PmBitmapFrame.CreateFromCopy(PmPixelFormat.Rgb24, 8, 4, 24, pixels, null);
            using var metadata = PmMetadata.Create();

            metadata.Orientation = 6;
            frame.SetMetadata(metadata);

            using var stream = new MemoryStream();

            frame.Encode(stream, PmCodecKind.Jpeg, new PmEncodeOptions(PmCompatibilityMode.Normal) { JpegQuality = 100 }, null, default);

            var orientedRgba = new byte[4 * 8 * 4];

            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var value = (byte)(y < 4 ? 10 : 240);
                    var offset = (y * 4 + x) * 4;

                    orientedRgba[offset] = value;
                    orientedRgba[offset + 1] = value;
                    orientedRgba[offset + 2] = value;
                    orientedRgba[offset + 3] = 255;
                }
            }

            return new OrientedImageFixture("JPEG", stream.ToArray(), new PixelSize(8, 4), orientedRgba, pixelTolerance: 24);
        }

        public override MultiFrameImageFixture? CreateMultiFrameFixture()
        {
            using var first = PmBitmapFrame.CreateFromCopy(PmPixelFormat.Rgba32, 4, 4, 16, SolidRgba(255, 0, 0), null);
            using var second = PmBitmapFrame.CreateFromCopy(PmPixelFormat.Rgba32, 4, 4, 16, SolidRgba(0, 255, 0), null);
            using var stream = new MemoryStream();

            PmBitmapFrame.EncodeFrames(new[] { first, second }, stream, PmCodecKind.Tiff, null, null, default);

            return new MultiFrameImageFixture("TIFF", stream.ToArray(), new PixelSize(4, 4), new[]
            {
                SolidRgba(255, 0, 0),
                SolidRgba(0, 255, 0),
            });
        }

        private static byte[] SolidRgba(byte r, byte g, byte b)
        {
            var bytes = new byte[4 * 4 * 4];

            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                bytes[offset] = r;
                bytes[offset + 1] = g;
                bytes[offset + 2] = b;
                bytes[offset + 3] = 255;
            }

            return bytes;
        }
    }

    public class PixelManDecoderDiscoveryContractTests : DecoderDiscoveryContractTests<PixelManCodecFixture> { }

    public class PixelManIdentifyContractTests : IdentifyContractTests<PixelManCodecFixture> { }

    public class PixelManPixelAccessContractTests : PixelAccessContractTests<PixelManCodecFixture> { }

    public class PixelManStreamContractTests : StreamContractTests<PixelManCodecFixture> { }

    public class PixelManRobustnessContractTests : RobustnessContractTests<PixelManCodecFixture> { }

    public class PixelManEncoderContractTests : EncoderContractTests<PixelManCodecFixture> { }

    public class PixelManCapabilityContractTests : CapabilityContractTests<PixelManCodecFixture> { }

    public class PixelManFrameCursorContractTests : FrameCursorContractTests<PixelManCodecFixture> { }

    public class PixelManMetadataContractTests : MetadataContractTests<PixelManCodecFixture> { }

    public class PixelManPaletteContractTests : PaletteContractTests<PixelManCodecFixture> { }

    public class PixelManStoredFormatContractTests : StoredFormatContractTests<PixelManCodecFixture> { }

    public class PixelManColorContractTests : ColorContractTests<PixelManCodecFixture> { }

    public class PixelManDecodePlanContractTests : DecodePlanContractTests<PixelManCodecFixture> { }

    public class PixelManThreadingContractTests : ThreadingContractTests<PixelManCodecFixture> { }

    public class PixelManLifetimeContractTests : LifetimeContractTests<PixelManCodecFixture> { }

    public class PixelManPoolContractTests : PoolContractTests<PixelManCodecFixture> { }

    public class PixelManCopyBudgetContractTests : CopyBudgetContractTests<PixelManCodecFixture> { }

    public class PixelManPublicApiContractTests : PublicApiContractTests<PixelManCodecFixture> { }

    public class PixelManFormatProfileContractTests : FormatProfileContractTests<PixelManCodecFixture> { }
}
