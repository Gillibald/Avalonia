using Avalonia.Imaging.TestKit.Contract;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Avalonia.Skia.Imaging;

namespace Avalonia.Skia.UnitTests.Imaging
{
    /// <summary>
    /// Runs the backend-agnostic imaging contract suite against the SkiaSharp backend.
    /// </summary>
    public sealed class SkiaCodecFixture : CodecBackendFixture
    {
        public override BackendManifest Manifest { get; } = new()
        {
            Formats = new[]
            {
                new FormatManifest("PNG")
                {
                    Decode = true,
                    Encode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("BMP")
                {
                    Decode = true,
                    CopyBudget = 0,
                    PixelTolerance = 0,
                },
                new FormatManifest("JPEG")
                {
                    Decode = true,
                    Encode = true,
                    FusedParts = FusedDecodeParts.Scale,
                    CopyBudget = 0,
                    PixelTolerance = 3,
                },
                new FormatManifest("WebP")
                {
                    Decode = true,
                    Encode = true,
                    FusedParts = FusedDecodeParts.Scale,
                    CopyBudget = 0,
                    PixelTolerance = 3,
                },
            },
            SupportsCancellation = false,
        };

        public override IImagingBackend CreateBackend() => new SkiaImagingBackend(Allocator);
    }

    public class SkiaDecoderDiscoveryContractTests : DecoderDiscoveryContractTests<SkiaCodecFixture> { }

    public class SkiaIdentifyContractTests : IdentifyContractTests<SkiaCodecFixture> { }

    public class SkiaPixelAccessContractTests : PixelAccessContractTests<SkiaCodecFixture> { }

    public class SkiaStreamContractTests : StreamContractTests<SkiaCodecFixture> { }

    public class SkiaRobustnessContractTests : RobustnessContractTests<SkiaCodecFixture> { }

    public class SkiaEncoderContractTests : EncoderContractTests<SkiaCodecFixture> { }

    public class SkiaCapabilityContractTests : CapabilityContractTests<SkiaCodecFixture> { }

    public class SkiaFrameCursorContractTests : FrameCursorContractTests<SkiaCodecFixture> { }

    public class SkiaMetadataContractTests : MetadataContractTests<SkiaCodecFixture> { }

    public class SkiaPaletteContractTests : PaletteContractTests<SkiaCodecFixture> { }

    public class SkiaDecodePlanContractTests : DecodePlanContractTests<SkiaCodecFixture> { }

    public class SkiaThreadingContractTests : ThreadingContractTests<SkiaCodecFixture> { }

    public class SkiaLifetimeContractTests : LifetimeContractTests<SkiaCodecFixture> { }

    public class SkiaPoolContractTests : PoolContractTests<SkiaCodecFixture> { }
}
