using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class FontCollectionBaseTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Should_Add_GlyphTypeface_From_Stream_Without_Platform_Services()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var glyphTypeface));
            Assert.Equal("Inter", glyphTypeface.FamilyName);
            Assert.IsType<SfntFace>(glyphTypeface.FontMemory);
        }

        [Fact]
        public void Synthetic_GlyphTypeface_Should_Share_Font_File_Data()
        {
            var collection = new TestFontCollection();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(collection.TryAddGlyphTypeface(stream, out var glyphTypeface));

            Assert.True(collection.TryCreateSyntheticGlyphTypeface(glyphTypeface, FontStyle.Italic, FontWeight.Bold,
                FontStretch.Normal, out var syntheticGlyphTypeface));

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, syntheticGlyphTypeface.FontSimulations);
            Assert.Equal(FontWeight.Bold, syntheticGlyphTypeface.Weight);
            Assert.Equal(FontStyle.Italic, syntheticGlyphTypeface.Style);

            var sourceFace = Assert.IsType<SfntFace>(glyphTypeface.FontMemory);
            var syntheticFace = Assert.IsType<SfntFace>(syntheticGlyphTypeface.FontMemory);

            Assert.NotSame(sourceFace, syntheticFace);
            Assert.True(sourceFace.TryGetFontFileData(out var sourceData, out _));
            Assert.True(syntheticFace.TryGetFontFileData(out var syntheticData, out _));
            Assert.True(sourceData.Span == syntheticData.Span);
        }

        [Fact]
        public void Should_Add_Every_Face_Of_A_Collection_File()
        {
            using var fixture = new FontFileFixture();

            var path = fixture.Write("inter.ttc", BuildRegularBoldCollection());
            var collection = new TestFontCollection();

            Assert.True(collection.TryAddFontSource(new Uri(path)));

            AssertRegularAndBoldFaces(collection);
        }

        [Fact]
        public void Should_Add_Every_Face_Of_A_Collection_Asset()
        {
            var asset = new Uri("avares://Fonts.Test/Assets/inter.ttc");
            var assetLoader = new BinaryAssetLoader(asset, BuildRegularBoldCollection());

            using (UnitTestApplication.Start(new TestServices(assetLoader: assetLoader)))
            {
                var collection = new TestFontCollection();

                Assert.True(collection.TryAddFontSource(asset));

                AssertRegularAndBoldFaces(collection);
            }
        }

        private static byte[] BuildRegularBoldCollection() => FontFileFixture.BuildCollection(
            FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri),
            FontFileFixture.ReadAsset(FontFileFixture.InterBoldUri));

        private static void AssertRegularAndBoldFaces(TestFontCollection collection)
        {
            Assert.True(collection.TryGetGlyphTypeface("Inter", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var regular));
            Assert.Equal(0, Assert.IsType<SfntFace>(regular.FontMemory).FaceIndex);

            // The bold face is the collection's second face, not a simulation over the first.
            Assert.True(collection.TryGetGlyphTypeface("Inter", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));
            Assert.Equal(FontSimulations.None, bold.FontSimulations);
            Assert.Equal(1, Assert.IsType<SfntFace>(bold.FontMemory).FaceIndex);
        }

        private sealed class BinaryAssetLoader : IAssetLoader
        {
            private readonly Uri _uri;
            private readonly byte[] _data;

            public BinaryAssetLoader(Uri uri, byte[] data)
            {
                _uri = uri;
                _data = data;
            }

            public bool Exists(Uri uri, Uri? baseUri = null) => uri == _uri;

            public Stream Open(Uri uri, Uri? baseUri = null) =>
                uri == _uri ? new MemoryStream(_data, false) : throw new FileNotFoundException(uri.ToString());

            public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null) =>
                (Open(uri, baseUri), typeof(BinaryAssetLoader).Assembly);

            public Assembly? GetAssembly(Uri uri, Uri? baseUri = null) => null;

            public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri) =>
                _uri.AbsolutePath.StartsWith(uri.AbsolutePath, StringComparison.Ordinal) ? new[] { _uri } : Enumerable.Empty<Uri>();

            public void InvalidateAssemblyCache(string name)
            {
            }

            public void InvalidateAssemblyCache()
            {
            }

            public void SetDefaultAssembly(Assembly asm)
            {
            }
        }

        private class TestFontCollection : FontCollectionBase
        {
            public override Uri Key => new Uri("fonts:TestFonts");
        }
    }
}
