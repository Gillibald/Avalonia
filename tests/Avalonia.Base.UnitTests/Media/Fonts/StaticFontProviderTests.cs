using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class StaticFontProviderTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";
        private const string InterBoldFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Bold.ttf?assembly=Avalonia.Base.UnitTests";
        private const string NotoMonoFontUri = "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";
        private const string ManropeLightFontUri = "resm:Avalonia.Base.UnitTests.Assets.Manrope-Light.ttf?assembly=Avalonia.Base.UnitTests";
        private const string MiSansFontUri = "resm:Avalonia.Base.UnitTests.Assets.MiSans-Normal.ttf?assembly=Avalonia.Base.UnitTests";
        private const string FontAssetsUri = "resm:Avalonia.Base.UnitTests.Assets?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void AddFont_Should_Register_Face()
        {
            using var provider = CreateProvider(InterFontUri);

            var names = provider.GetFontFamilyNames();

            Assert.Equal(new[] { "Inter" }, names);
        }

        [Fact]
        public void AddFontSource_Should_Register_All_Faces()
        {
            using (UnitTestApplication.Start(TestServices.MockThreadingInterface))
            {
                using var provider = new StaticFontProvider(new Uri(FontAssetsUri));

                var names = provider.GetFontFamilyNames();

                Assert.Contains("Inter", names);
                Assert.Contains("Noto Mono", names);
            }
        }

        [Fact]
        public void Default_Should_Be_First_Registered_Face_When_Unset()
        {
            using var provider = CreateProvider(NotoMonoFontUri, InterFontUri);

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.Equal("Noto Mono", face.FamilyName);
        }

        [Fact]
        public void Explicit_Default_Family_Should_Win()
        {
            using var provider = CreateProvider(NotoMonoFontUri, InterFontUri);

            provider.DefaultFamilyName = "Inter";

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.Equal("Inter", face.FamilyName);
        }

        [Fact]
        public void Should_Match_Nearest_Weight()
        {
            using var provider = CreateProvider(InterFontUri, InterBoldFontUri);

            Assert.True(provider.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Black, FontStretch.Normal,
                out var black));
            Assert.Equal(FontWeight.Bold, black.Weight);

            Assert.True(provider.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                out var normal));
            Assert.Equal(FontWeight.Normal, normal.Weight);
        }

        [Fact]
        public void Should_Match_Typographic_Family_Name()
        {
            using var provider = CreateProvider(ManropeLightFontUri);

            Assert.True(provider.TryMatchFamily("Manrope", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                out var match));
            Assert.Equal("Manrope Light", match.FamilyName);
        }

        [Fact]
        public void Should_Match_Character_With_Family_Hint()
        {
            using var provider = CreateProvider(InterFontUri, MiSansFontUri);

            // Inter has no CJK coverage; the covering face wins.
            Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var cjk));
            Assert.Equal("MiSans Normal", cjk.FamilyName);

            // Both faces cover 'A'; the family hint decides.
            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                "MiSans", null, out var hinted));
            Assert.Equal("MiSans Normal", hinted.FamilyName);

            Assert.False(provider.TryMatchCharacter(0x10FF00, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out _));
        }

        [Fact]
        public void Should_Get_Family_Faces()
        {
            using var provider = CreateProvider(InterFontUri, InterBoldFontUri, NotoMonoFontUri);

            Assert.True(provider.TryGetFamilyFaces("Inter", out var faces));
            Assert.Equal(2, faces.Count);
        }

        [Fact]
        public void Descriptor_Should_Clone_Font_Data()
        {
            using var provider = CreateProvider(InterFontUri);

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.Equal("Inter", glyphTypeface.FamilyName);

            // Disposing the consumer's typeface leaves the provider's face intact.
            glyphTypeface.Dispose();

            Assert.True(provider.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Fact]
        public void Disposed_Provider_Should_Fail_Descriptors_Gracefully()
        {
            var provider = CreateProvider(InterFontUri);

            Assert.True(provider.TryGetDefaultFontFace(out var face));

            provider.Dispose();

            Assert.False(face.TryOpenFontMemory(out _));
            Assert.False(provider.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        }

        private static StaticFontProvider CreateProvider(params string[] fontUris)
        {
            var provider = new StaticFontProvider();

            foreach (var fontUri in fontUris)
            {
                using var stream = SfntFaceTestHelper.OpenAsset(fontUri);

                Assert.True(provider.AddFont(stream));
            }

            return provider;
        }
    }

    public class StaticFontProviderContractTests : SystemFontProviderContractTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        protected override ISystemFontProvider CreateProvider()
        {
            var provider = new StaticFontProvider();

            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            provider.AddFont(stream);

            return provider;
        }

        protected override string KnownFamilyName => "Inter";
    }
}
