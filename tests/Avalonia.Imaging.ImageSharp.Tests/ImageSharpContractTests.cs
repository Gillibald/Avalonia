using System.IO;
using Avalonia.Imaging.TestKit.Contract;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

// Backend-specific tests bind the imaging backend through the process-global locator,
// so the suite runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Avalonia.Imaging.ImageSharp.Tests
{
    /// <summary>
    /// Runs the backend-agnostic imaging contract suite against the ImageSharp backend.
    /// </summary>
    public sealed class ImageSharpCodecFixture : CodecBackendFixture
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
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("JPEG")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    FusedParts = FusedDecodeParts.Scale,
                    CopyBudget = 0,
                    PixelTolerance = 3,
                },
                new FormatManifest("GIF")
                {
                    Decode = true,
                    Encode = true,
                    SeekableFrames = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("BMP")
                {
                    Decode = true,
                    Encode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("TIFF")
                {
                    Decode = true,
                    Encode = true,
                    Metadata = true,
                    SeekableFrames = true,
                    MultiFrameEncode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("WebP")
                {
                    Decode = true,
                    Encode = true,
                    CopyBudget = 0,
                    PixelTolerance = 3,
                },
            },
            SupportsCancellation = false,
        };

        public override IImagingBackend CreateBackend() => new ImageSharpImagingBackend(Allocator);

        public override OrientedImageFixture? CreateOrientedFixture()
        {
            // Raw 8x4 grayscale halves tagged EXIF orientation 6: the upright display
            // is 4x8 with the dark half on top. Quality 100 keeps the step edge nearly
            // exact through the JPEG round trip.
            using var source = new Image<Rgb24>(8, 4);

            source.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);

                    for (var x = 0; x < row.Length; x++)
                        row[x] = x < 4 ? new Rgb24(10, 10, 10) : new Rgb24(240, 240, 240);
                }
            });

            var exif = new ExifProfile();

            exif.SetValue(ExifTag.Orientation, (ushort)6);
            source.Metadata.ExifProfile = exif;

            using var stream = new MemoryStream();

            source.Save(stream, new JpegEncoder { Quality = 100 });

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

            return new OrientedImageFixture("JPEG", stream.ToArray(), new PixelSize(8, 4),
                orientedRgba, pixelTolerance: 24);
        }

        public override MultiFrameImageFixture? CreateMultiFrameFixture()
        {
            using var first = new Image<Rgba32>(4, 4, new Rgba32(255, 0, 0));
            using var second = new Image<Rgba32>(4, 4, new Rgba32(0, 255, 0));

            first.Frames.AddFrame(second.Frames.RootFrame);

            using var stream = new MemoryStream();

            first.Save(stream, new TiffEncoder());

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

    public class ImageSharpDecoderDiscoveryContractTests : DecoderDiscoveryContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpIdentifyContractTests : IdentifyContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpPixelAccessContractTests : PixelAccessContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpStreamContractTests : StreamContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpRobustnessContractTests : RobustnessContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpEncoderContractTests : EncoderContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpCapabilityContractTests : CapabilityContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpFrameCursorContractTests : FrameCursorContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpMetadataContractTests : MetadataContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpPaletteContractTests : PaletteContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpDecodePlanContractTests : DecodePlanContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpThreadingContractTests : ThreadingContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpLifetimeContractTests : LifetimeContractTests<ImageSharpCodecFixture> { }

    public class ImageSharpPoolContractTests : PoolContractTests<ImageSharpCodecFixture> { }
}
