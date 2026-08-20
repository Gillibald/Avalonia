using System;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class SkiaFontProviderTests
    {
        [Fact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new SkiaFontProvider();

            Assert.NotEmpty(provider.GetFontFamilyNames());
        }

        [Fact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));

            // The default face loads through the managed loader end to end.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [Fact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new SkiaFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Win32Fact("Requires Windows fonts")]
        public void Should_Resolve_Ttc_Face_Through_Managed_Loader()
        {
            using var provider = new SkiaFontProvider();

            // Yu Gothic UI lives inside a TrueType collection; OpenStream reports the collection
            // face index, and the managed loader must resolve exactly that face.
            Assert.True(provider.TryMatchFamily("Yu Gothic UI", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var match));

            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.Equal("Yu Gothic UI", glyphTypeface.FamilyName);

            glyphTypeface.Dispose();
        }

        [Win32Fact("Requires Windows fonts")]
        public void Should_Match_Designed_Bold_Face()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryMatchFamily("Arial", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            Assert.Equal(FontWeight.Bold, bold.Weight);
        }

        [Fact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));

            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('A', out _));

            glyphTypeface.Dispose();
        }

        [Fact]
        public void Should_Get_Family_Faces()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var defaultFace));
            Assert.True(provider.TryGetFamilyFaces(defaultFace.FamilyName, out var faces));
            Assert.NotEmpty(faces);
        }
    }

    public class SkiaFontProviderContractTests : SystemFontProviderContractTests
    {
        protected override ISystemFontProvider CreateProvider() => new SkiaFontProvider();

        protected override string KnownFamilyName
        {
            get
            {
                using var provider = new SkiaFontProvider();

                return provider.TryGetDefaultFontFace(out var face) ? face.FamilyName : "Arial";
            }
        }
    }
}
