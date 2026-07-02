using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Metadata exposure through <see cref="IMetadataSource"/> and the shared PNG text
    /// query path; EXIF specifics are covered per backend.
    /// </summary>
    public abstract class MetadataContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngMetadata = "The manifest does not declare PNG metadata support.";

        private static string TextQuery => "/text/{str=" + FixtureImages.PngTextKeyword + "}";

        [Fact]
        public void DeclaredMetadata_ExposesMetadataSource()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Metadata: true },
                NoPngMetadata);

            using var decoder = CreateDecoder(FixtureImages.PngWithTextMetadata);
            using var frame = ReadFirstFrame(decoder);

            var source = Assert.IsAssignableFrom<IMetadataSource>(frame);

            Assert.Equal(FixtureImages.PngFormatName, source.Metadata.Format);
        }

        [Fact]
        public void QueryPath_ReadsBack()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Metadata: true },
                NoPngMetadata);

            using var decoder = CreateDecoder(FixtureImages.PngWithTextMetadata);
            using var frame = ReadFirstFrame(decoder);

            var metadata = Assert.IsAssignableFrom<IMetadataSource>(frame).Metadata;

            Assert.True(metadata.ContainsQuery(TextQuery));
            Assert.Equal(FixtureImages.PngTextValue, metadata.GetQuery(TextQuery));

            Assert.False(metadata.ContainsQuery("/text/{str=Missing}"));
            Assert.Null(metadata.GetQuery("/text/{str=Missing}"));
        }

        [Fact]
        public void MissingMetadata_TryGetFalse()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Metadata: true },
                NoPngMetadata);

            using var decoder = CreateDecoder(FixtureImages.Rgb4x4Png);
            using var frame = ReadFirstFrame(decoder);

            // A plain image exposes no metadata: either the capability interface is
            // absent, or its queries answer empty.
            if (frame is IMetadataSource source)
            {
                Assert.False(source.Metadata.ContainsQuery(TextQuery));
                Assert.Null(source.Metadata.GetQuery(TextQuery));
            }
        }
    }
}
