using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Metadata exposure and round-tripping through
    /// <see cref="Avalonia.Platform.IMetadataSource"/> and encoder metadata.
    /// </summary>
    public abstract class MetadataContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why these tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs metadata-bearing fixtures (EXIF orientation, XMP); the generated PNG/BMP " +
            "fixtures carry none. Backend test projects supply them together with their " +
            "metadata codec support.";

        [Fact(Skip = SkipReason)]
        public void DeclaredMetadataFormats_ExposeMetadataSource()
        {
        }

        [Fact(Skip = SkipReason)]
        public void MetadataQueries_ReadWellKnownPaths()
        {
        }

        [Fact(Skip = SkipReason)]
        public void Metadata_RoundTripsThroughEncode()
        {
        }
    }
}
