using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Fontconfig.UnitTests
{
    public class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsLinux())
            {
                Skip = "Requires fontconfig on Linux.";
            }
        }
    }

    public class FontconfigFontProviderTests
    {
        [LinuxFact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new FontconfigFontProvider();

            var names = provider.GetFontFamilyNames();

            Assert.NotEmpty(names);
        }

        [LinuxFact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));
            Assert.True(File.Exists(face.FilePath));

            // The default face loads through the managed loader end to end.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.False(string.IsNullOrEmpty(glyphTypeface.FamilyName));
            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [LinuxFact]
        public void Should_Match_Generic_Aliases()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryMatchFamily("sans-serif", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var sansSerif));
            Assert.True(File.Exists(sansSerif.FilePath));

            Assert.True(provider.TryMatchFamily("monospace", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var monospace));
            Assert.True(File.Exists(monospace.FilePath));
        }

        [LinuxFact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new FontconfigFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [LinuxFact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));
            Assert.True(File.Exists(match.FilePath));

            // Plane-16 private-use codepoints are not covered by any installed font; the charset
            // verification must reject the matcher's unconditional fallback.
            Assert.False(provider.TryMatchCharacter(0x10FF00, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, null, out _));
        }

        [LinuxFact]
        public void Should_Get_Family_Faces()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var defaultFace));
            Assert.True(provider.TryGetFamilyFaces(defaultFace.FamilyName, out var faces));
            Assert.NotEmpty(faces);

            foreach (var face in faces)
            {
                Assert.True(File.Exists(face.FilePath));
            }
        }

        [LinuxFact]
        public void Should_Match_Bold_Face_When_Available()
        {
            using var provider = new FontconfigFontProvider();

            Assert.SkipUnless(provider.TryMatchFamily("DejaVu Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _), "DejaVu Sans is not installed.");

            Assert.True(provider.TryMatchFamily("DejaVu Sans", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            // Designed weight of the matched face, not a simulation.
            Assert.Equal(FontWeight.Bold, bold.Weight);
        }
    }

    public class FcMappingTests
    {
        [Theory]
        [InlineData(100, 0)]
        [InlineData(400, 80)]
        [InlineData(450, 90)]
        [InlineData(600, 180)]
        [InlineData(650, 190)]
        [InlineData(700, 200)]
        [InlineData(1000, 215)]
        public void Should_Map_OpenType_Weight_To_Fontconfig(int openType, int fontconfig)
        {
            Assert.Equal(fontconfig, FcMapping.WeightFromOpenType(openType));
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(80, 400)]
        [InlineData(90, 450)]
        [InlineData(180, 600)]
        [InlineData(200, 700)]
        [InlineData(215, 1000)]
        public void Should_Map_Fontconfig_Weight_To_OpenType(int fontconfig, int openType)
        {
            Assert.Equal(openType, FcMapping.WeightToOpenType(fontconfig));
        }

        [Theory]
        [InlineData(FontStyle.Normal, 0)]
        [InlineData(FontStyle.Italic, 100)]
        [InlineData(FontStyle.Oblique, 110)]
        public void Should_Map_Style_Round_Trip(FontStyle style, int slant)
        {
            Assert.Equal(slant, FcMapping.SlantFromFontStyle(style));
            Assert.Equal(style, FcMapping.SlantToFontStyle(slant));
        }

        [Theory]
        [InlineData(FontStretch.UltraCondensed, 50)]
        [InlineData(FontStretch.Condensed, 75)]
        [InlineData(FontStretch.Normal, 100)]
        [InlineData(FontStretch.Expanded, 125)]
        [InlineData(FontStretch.UltraExpanded, 200)]
        public void Should_Map_Stretch_Round_Trip(FontStretch stretch, int width)
        {
            Assert.Equal(width, FcMapping.WidthFromFontStretch(stretch));
            Assert.Equal(stretch, FcMapping.WidthToFontStretch(width));
        }

        [Fact]
        public void Should_Map_Width_To_Nearest_Stretch()
        {
            Assert.Equal(FontStretch.Expanded, FcMapping.WidthToFontStretch(122));
            Assert.Equal(FontStretch.SemiExpanded, FcMapping.WidthToFontStretch(110));
        }
    }

    public class FontconfigProviderContractTests : SystemFontProviderContractTests
    {
        protected override bool IsSupported => OperatingSystem.IsLinux();

        protected override ISystemFontProvider CreateProvider() => new FontconfigFontProvider();

        protected override string KnownFamilyName
        {
            get
            {
                using var provider = new FontconfigFontProvider();

                return provider.TryGetDefaultFontFace(out var face) ? face.FamilyName : "sans-serif";
            }
        }
    }
}
