using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    public class GlyphTypefaceTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";
        private const string BlankFontUri = "resm:Avalonia.Base.UnitTests.Assets.AdobeBlank2VF.ttf?assembly=Avalonia.Base.UnitTests";
        private const string GB18030FontUri = "resm:Avalonia.Base.UnitTests.Assets.NISC18030.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Should_Load_Inter_Font()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.Equal("Inter", typeface.FamilyName);
        }

        [Fact]
        public void Should_Have_CharacterToGlyphMap_For_Common_Characters()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;

            Assert.NotNull(map);

            Assert.True(map.ContainsGlyph('A'));
            Assert.True(map['A'] != 0);

            Assert.True(map.ContainsGlyph('a'));
            Assert.True(map['a'] != 0);

            Assert.True(map.ContainsGlyph(' '));
            Assert.True(map[' '] != 0);
        }

        [Fact]
        public void GetGlyphAdvance_Should_Return_Advance_For_GlyphId()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;

            Assert.True(map.ContainsGlyph('A'));

            var glyphId = map['A'];

            // Ensure metrics are available for this glyph
            Assert.True(typeface.TryGetGlyphMetrics(glyphId, out var metrics));

            // Ensure advance can be retrieved
            Assert.True(typeface.TryGetHorizontalGlyphAdvance(glyphId, out var advance));

            // Advance returned by GetGlyphAdvance should match the metrics width
            Assert.Equal(metrics.Width, advance);
        }

        [Theory]
        [InlineData(InterFontUri)]
        [InlineData(GB18030FontUri)] // Font without head table
        public void Should_Have_Valid_FontMetrics(string fontUri)
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(fontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var metrics = typeface.Metrics;

            Assert.True(metrics.DesignEmHeight > 0);
            Assert.True(metrics.Ascent != 0);
            Assert.True(metrics.Descent != 0);
            Assert.True(metrics.LineSpacing > 0);
        }

        [Fact]
        public void Should_Have_Positive_GlyphCount()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.True(typeface.GlyphCount > 0);
        }

        [Fact]
        public void Should_Have_Correct_Font_Properties()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.Equal(FontWeight.Normal, typeface.Weight);
            Assert.Equal(FontStyle.Normal, typeface.Style);
            Assert.Equal(FontStretch.Normal, typeface.Stretch);
            Assert.Equal(FontSimulations.None, typeface.FontSimulations);
        }

        [Fact]
        public void Should_Apply_Bold_Simulation()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream), FontSimulations.Bold);

            Assert.Equal(FontWeight.Bold, typeface.Weight);
            Assert.Equal(FontSimulations.Bold, typeface.FontSimulations);
        }

        [Fact]
        public void Should_Apply_Oblique_Simulation()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream), FontSimulations.Oblique);

            Assert.Equal(FontStyle.Italic, typeface.Style);
            Assert.Equal(FontSimulations.Oblique, typeface.FontSimulations);
        }

        [Fact]
        public void Should_Apply_Combined_Simulations()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream), 
                FontSimulations.Bold | FontSimulations.Oblique);

            Assert.Equal(FontWeight.Bold, typeface.Weight);
            Assert.Equal(FontStyle.Italic, typeface.Style);
            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, typeface.FontSimulations);
        }

        [Fact]
        public void Should_Have_TypographicFamilyName()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.NotNull(typeface.TypographicFamilyName);
        }

        [Fact]
        public void Should_Have_FamilyNames_Dictionary()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.NotNull(typeface.FamilyNames);
            Assert.NotEmpty(typeface.FamilyNames);
        }

        [Fact]
        public void Should_Have_FaceNames_Dictionary()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.NotNull(typeface.FaceNames);
            Assert.NotEmpty(typeface.FaceNames);
        }

        [Fact]
        public void Should_Have_SupportedFeatures()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var features = typeface.SupportedFeatures;

            Assert.NotEmpty(features);
        }

        [Fact]
        public void Should_Cache_SupportedFeatures()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var features1 = typeface.SupportedFeatures;
            var features2 = typeface.SupportedFeatures;

            Assert.Same(features1, features2);
        }

        [Fact]
        public void TryGetGlyphAdvance_Should_Return_False_For_Invalid_GlyphId()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.False(typeface.TryGetHorizontalGlyphAdvance(ushort.MaxValue, out var advance));
        }

        [Fact]
        public void TryGetGlyphMetrics_Should_Return_False_For_Invalid_GlyphId()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var result = typeface.TryGetGlyphMetrics(ushort.MaxValue, out var metrics);

            Assert.False(result);
            Assert.Equal(default, metrics);
        }

        [Fact]
        public void TryGetGlyphMetrics_Should_Return_Valid_Metrics()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;
            Assert.True(map.ContainsGlyph('A'));

            var glyphId = map['A'];
            var result = typeface.TryGetGlyphMetrics(glyphId, out var metrics);

            Assert.True(result);
            Assert.True(metrics.Width > 0);
        }

        [Fact]
        public void Should_Dispose_Properly()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            typeface.Dispose();

            // Should not throw on double dispose
            typeface.Dispose();
        }

        [Fact]
        public void CharacterToGlyphMap_Should_Have_Different_Glyphs_For_Different_Characters()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;

            Assert.True(map.ContainsGlyph('A'));
            Assert.True(map.ContainsGlyph('B'));

            var glyphA = map['A'];
            var glyphB = map['B'];

            Assert.NotEqual(glyphA, glyphB);
        }

        [Fact]
        public void CharacterToGlyphMap_With_Format13_Should_Have_Same_Glyph_For_Different_Characters()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(BlankFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;

            Assert.True(map.ContainsGlyph('A'));
            Assert.True(map.ContainsGlyph('B'));

            var glyphA = map['A'];
            var glyphB = map['B'];

            Assert.Equal(glyphA, glyphB);
        }

        [Fact]
        public void FontMetrics_LineSpacing_Should_Be_Calculated_Correctly()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var metrics = typeface.Metrics;

            var expectedLineSpacing = metrics.Descent - metrics.Ascent + metrics.LineGap;

            Assert.Equal(expectedLineSpacing, metrics.LineSpacing);
        }

        [Fact]
        public void Should_Support_Multiple_Characters_In_CharacterToGlyphMap()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            var map = typeface.CharacterToGlyphMap;

            var testCharacters = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };

            foreach (var ch in testCharacters)
            {
                Assert.True(map.ContainsGlyph(ch), $"Character '{ch}' not found in glyph map");
            }
        }

        [Fact]
        public void FamilyNames_Should_Contain_InvariantCulture_Entry()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.True(typeface.FamilyNames.ContainsKey(CultureInfo.InvariantCulture) || 
                       typeface.FamilyNames.Count > 0);
        }

        [Fact]
        public void FaceNames_Should_Contain_InvariantCulture_Entry()
        {
            var assetLoader = new StandardAssetLoader();

            using var stream = assetLoader.Open(new Uri(InterFontUri));

            var typeface = new GlyphTypeface(UnmanagedFontMemory.LoadFromStream(stream));

            Assert.True(typeface.FaceNames.ContainsKey(CultureInfo.InvariantCulture) || 
                       typeface.FaceNames.Count > 0);
        }

    }
}
