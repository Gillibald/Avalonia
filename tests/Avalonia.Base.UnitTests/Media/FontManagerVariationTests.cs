using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Base.UnitTests.Media.Fonts.Tables;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media
{
    /// <summary>
    /// FontManager is the seam where a Typeface's user-space FontVariations are applied
    /// to the resolved GlyphTypeface. These tests pin that application: the resolved
    /// typeface is bound to the normalized position, equal settings share one cached
    /// instance, and settings-free lookups stay at the default instance.
    /// </summary>
    public class FontManagerVariationTests
    {
        private const string InterVariableAsset =
            "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";

        private static readonly OpenTypeTag s_wghtTag = OpenTypeTag.Parse("wght");

        [Fact]
        public void TryGetGlyphTypeface_Applies_FontVariations()
        {
            using (Start())
            {
                var typeface = new Typeface("Inter Variable",
                    FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, FontVariationSettings.Parse("wght=700"));

                Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));

                // wght=700 on Inter Variable normalizes to 0.54 (through avar) — the same
                // value CreateNormalizedPosition produces, proving the settings reached
                // the variation seam and were not dropped during resolution.
                Assert.True(glyphTypeface.VariationPosition.TryGetCoordinate(s_wghtTag, out var wght));
                Assert.Equal(0.54f, wght, precision: 4);
            }
        }

        [Fact]
        public void TryGetGlyphTypeface_Without_Variations_Resolves_Default_Instance()
        {
            using (Start())
            {
                var typeface = new Typeface("Inter Variable");

                Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));

                Assert.True(glyphTypeface.VariationPosition.IsDefault);
            }
        }

        [Fact]
        public void Equal_Variations_Resolve_To_The_Same_Instance()
        {
            using (Start())
            {
                var a = new Typeface("Inter Variable",
                    FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, FontVariationSettings.Parse("wght=700"));
                var b = new Typeface("Inter Variable",
                    FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, FontVariationSettings.Parse("wght=700"));

                Assert.True(FontManager.Current.TryGetGlyphTypeface(a, out var first));
                Assert.True(FontManager.Current.TryGetGlyphTypeface(b, out var second));

                // The base typeface is cached by the font collection and the varied clone
                // is cached on the base, so equal settings must not allocate per lookup.
                Assert.Same(first, second);

                Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface("Inter Variable"), out var plain));
                Assert.NotSame(plain, first);
            }
        }

        [Fact]
        public void FontVariations_Override_Only_Their_Axes_Of_A_Resolved_Varied_Face()
        {
            using (Start())
            {
                // A collection that resolves SemiBold to the wght=600 position of the variable
                // font, the way font matching picks a named instance or axis value.
                var assetLoader = new StandardAssetLoader();
                using var stream = assetLoader.Open(new Uri(InterVariableAsset));
                var root = new GlyphTypeface(new CustomPlatformTypeface(stream));
                var semiBold = root.WithVariations(FontVariationSettings.Parse("wght=600"));

                var collection = new PresetFontCollection(new Uri("fonts:preset", UriKind.Absolute));
                Assert.True(collection.TryAddGlyphTypeface(semiBold,
                    new FontCollectionKey(FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal)));
                FontManager.Current.AddFontCollection(collection);

                var typeface = new Typeface(new FontFamily("fonts:preset#Inter Variable"),
                    FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, FontVariationSettings.Parse("opsz=32"));

                Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));

                var position = glyphTypeface.VariationPosition;

                Assert.Equal(semiBold.VariationPosition.GetCoordinateOrDefault(s_wghtTag),
                    position.GetCoordinateOrDefault(s_wghtTag));
                Assert.Equal(1f, position.GetCoordinateOrDefault(OpenTypeTag.Parse("opsz")));
                Assert.Equal(FontWeight.SemiBold, glyphTypeface.Weight);
            }
        }

        private sealed class PresetFontCollection(Uri key) : FontCollectionBase
        {
            public override Uri Key { get; } = key;
        }

        private static IDisposable Start() =>
            UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(systemFontProvider: new VariableFontProvider()));

        /// <summary>
        /// Serves the embedded Inter Variable font as the only system font, so the
        /// resolution pipeline (FontManager, SystemFontCollection, provider) runs for real
        /// against a variable font without depending on system fonts.
        /// </summary>
        private sealed class VariableFontProvider : ISystemFontProvider
        {
            private StaticFontProvider? _inner;

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
                => GetInner().TryGetDefaultFontFace(out face);

            public IReadOnlyList<string> GetFontFamilyNames() => GetInner().GetFontFamilyNames();

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch, [NotNullWhen(true)] out SystemFontFace? match)
                => GetInner().TryMatchFamily(familyName, style, weight, stretch, out match);

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight,
                FontStretch stretch, string? familyName, CultureInfo? culture,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = null;
                return false;
            }

            public bool TryGetFamilyFaces(string familyName,
                [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
                => GetInner().TryGetFamilyFaces(familyName, out faces);

            public void Dispose() => _inner?.Dispose();

            // Loaded on first use so the font is parsed inside the test's service scope.
            private StaticFontProvider GetInner()
            {
                if (_inner is null)
                {
                    _inner = new StaticFontProvider { DefaultFamilyName = "Inter Variable" };

                    var assetLoader = new StandardAssetLoader();
                    using var stream = assetLoader.Open(new Uri(InterVariableAsset));

                    _inner.AddFont(stream);
                }

                return _inner;
            }
        }
    }
}
