using Avalonia.Imaging.TestKit.Contract;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
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
