using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class MaxpTableTests
    {
        private static string s_InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Should_Load_MaxpTable_From_Inter_Font()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.NotEqual(default, maxpTable);
        }

        [Fact]
        public void MaxpTable_Should_Have_Valid_NumGlyphs()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(2547, maxpTable.NumGlyphs);
        }

        [Fact]
        public void MaxpTable_TrueType_Should_Have_Version_1_0()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(1, maxpTable.Version.Major);
            Assert.Equal(0, maxpTable.Version.Minor);
        }

        [Fact]
        public void MaxpTable_Version_1_0_Should_Have_Valid_MaxPoints()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(148, maxpTable.MaxPoints);
        }

        [Fact]
        public void MaxpTable_Version_1_0_Should_Have_Valid_MaxContours()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(12, maxpTable.MaxContours);
        }

        [Fact]
        public void MaxpTable_Version_1_0_Should_Have_Valid_MaxZones()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(1, maxpTable.MaxZones);
        }

        [Fact]
        public void MaxpTable_Should_Have_Valid_MaxCompositePoints()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(112, maxpTable.MaxCompositePoints);
        }

        [Fact]
        public void MaxpTable_Should_Have_Valid_MaxCompositeContours()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(7, maxpTable.MaxCompositeContours);
        }

        [Fact]
        public void MaxpTable_Should_Have_Valid_MaxStackElements()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(0, maxpTable.MaxStackElements);
        }

        [Fact]
        public void MaxpTable_Should_Have_Valid_MaxComponentDepth()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(1, maxpTable.MaxComponentDepth);
        }

        [Fact]
        public void MaxpTable_NumGlyphs_Should_Match_GlyphTypeface_GlyphCount()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(s_InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var maxpTable = MaxpTable.Load(typeface);

            Assert.Equal(maxpTable.NumGlyphs, typeface.GlyphCount);
        }

    }
}
