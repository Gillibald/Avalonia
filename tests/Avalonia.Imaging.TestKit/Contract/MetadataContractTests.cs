using System;
using System.Linq;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Metadata exposure through <see cref="IMetadataStoreSource"/>: the store holds canonical
    /// paths, decoded metadata is read-only, and the core query engine reads it with WIC
    /// spellings. EXIF specifics are covered per backend.
    /// </summary>
    public abstract class MetadataContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngMetadata = "The manifest does not declare PNG metadata support.";

        private static string TextQuery => "/text/{str=" + FixtureImages.PngTextKeyword + "}";

        [Fact]
        public void DeclaredMetadata_ExposesAReadOnlyStore()
        {
            RequirePngMetadata();

            using var decoder = CreateDecoder(FixtureImages.PngWithTextMetadata);
            using var frame = ReadFirstFrame(decoder);

            var store = Assert.IsAssignableFrom<IMetadataStoreSource>(frame).Metadata;

            Assert.True(store.IsReadOnly);
            Assert.Throws<InvalidOperationException>(() =>
                store.Write(TextQuery, new MetadataValue(MetadataValueType.Ascii, new byte[] { 0x41 })));
        }

        [Fact]
        public void QueryPath_ReadsBack()
        {
            RequirePngMetadata();

            var metadata = ReadPngMetadata();

            Assert.Equal(FixtureImages.PngFormatName, metadata.Format);
            Assert.True(metadata.ContainsQuery(TextQuery));
            Assert.Equal(FixtureImages.PngTextValue, metadata.GetQuery(TextQuery));

            Assert.False(metadata.ContainsQuery("/text/{str=Missing}"));
            Assert.Null(metadata.GetQuery("/text/{str=Missing}"));
        }

        [Fact]
        public void WicSpelling_ReadsTheSameValue()
        {
            RequirePngMetadata();

            var metadata = ReadPngMetadata();

            Assert.Equal(FixtureImages.PngTextValue, metadata.GetQuery("/tEXt/{str=" + FixtureImages.PngTextKeyword + "}"));
        }

        [Fact]
        public void StorePaths_AreCanonicalAndReadable()
        {
            RequirePngMetadata();

            using var decoder = CreateDecoder(FixtureImages.PngWithTextMetadata);
            using var frame = ReadFirstFrame(decoder);

            var store = Assert.IsAssignableFrom<IMetadataStoreSource>(frame).Metadata;
            var paths = store.EnumeratePaths().ToList();

            Assert.Contains(TextQuery, paths);

            foreach (var path in paths)
            {
                Assert.Equal(path, MetadataQuery.Canonicalize(path, BitmapContainerFormats.Png));
                Assert.True(store.TryRead(path, out _), $"The store lists '{path}' but cannot read it.");
            }
        }

        [Fact]
        public void Clone_IsWritableAndIndependent()
        {
            RequirePngMetadata();

            var metadata = ReadPngMetadata();
            var copy = metadata.Clone();

            Assert.True(metadata.IsReadOnly);
            Assert.False(copy.IsReadOnly);

            copy.SetQuery(TextQuery, "Changed");

            Assert.Equal("Changed", copy.GetQuery(TextQuery));
            Assert.Equal(FixtureImages.PngTextValue, metadata.GetQuery(TextQuery));
        }

        [Fact]
        public void MissingMetadata_TryGetFalse()
        {
            RequirePngMetadata();

            using var decoder = CreateDecoder(FixtureImages.Rgb4x4Png);
            using var frame = ReadFirstFrame(decoder);

            // A plain image exposes no metadata: either the capability interface is
            // absent, or its store holds no text.
            if (frame is IMetadataStoreSource source)
                Assert.False(source.Metadata.TryRead(TextQuery, out _));
        }

        private BitmapMetadata ReadPngMetadata()
        {
            using var decoder = CreateDecoder(FixtureImages.PngWithTextMetadata);
            using var frame = ReadFirstFrame(decoder);

            var store = Assert.IsAssignableFrom<IMetadataStoreSource>(frame).Metadata;

            return new BitmapMetadata(store, BitmapContainerFormats.Png);
        }

        private void RequirePngMetadata()
            => Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true, Metadata: true },
                NoPngMetadata);
    }
}
