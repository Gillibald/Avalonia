using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
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

        private class TestFontCollection : FontCollectionBase
        {
            public override Uri Key => new Uri("fonts:TestFonts");
        }
    }
}
