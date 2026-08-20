using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class SystemFontCollectionTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Enumeration_Should_Be_Deferred_Until_First_Query()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal(0, provider.GetFontFamilyNamesCount);

            Assert.Equal(1, collection.Count);
            Assert.Equal(1, provider.GetFontFamilyNamesCount);

            _ = collection.Count;
            Assert.Equal(1, provider.GetFontFamilyNamesCount);
        }

        [Fact]
        public void Should_Match_Family_Through_Provider()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var glyphTypeface));

            Assert.Equal("Inter", glyphTypeface.FamilyName);
            Assert.Equal(1, provider.TryMatchFamilyCount);

            // Subsequent lookups are served from the cache.
            Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var other));

            Assert.Same(glyphTypeface, other);
            Assert.Equal(1, provider.TryMatchFamilyCount);
        }

        [Fact]
        public void Should_Apply_Simulation_Policy_On_Designed_Properties()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Italic, FontWeight.Bold,
                FontStretch.Normal, out var glyphTypeface));

            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, glyphTypeface.FontSimulations);
            Assert.Equal(FontWeight.Bold, glyphTypeface.Weight);
            Assert.Equal(FontStyle.Italic, glyphTypeface.Style);
        }

        [Fact]
        public void Unknown_Family_Should_Be_Negative_Cached()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.False(collection.TryGetGlyphTypeface("Unknown", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));
            Assert.False(collection.TryGetGlyphTypeface("Unknown", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal(1, provider.TryMatchFamilyCount);
        }

        [Fact]
        public void Default_Font_Family_Should_Be_Pinned()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetDefaultFontFamily(out var fontFamily));

            // Resolving the default does not force family enumeration.
            Assert.Equal(0, provider.GetFontFamilyNamesCount);

            // The provider's default face has a private name that TryMatchFamily would not serve.
            Assert.Equal(".Test UI", fontFamily.Name);

            // The pinned descriptor resolves through the cache, not through the provider.
            Assert.True(collection.TryGetGlyphTypeface(".Test UI", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var glyphTypeface));

            Assert.Equal("Inter", glyphTypeface.FamilyName);
            Assert.Equal(0, provider.TryMatchFamilyCount);
        }

        [Fact]
        public void Should_Match_Character_Through_Provider()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));

            Assert.Equal("Inter", match.FontFamily.Name);
            Assert.Equal(1, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void Uncovered_Codepoint_Should_Invoke_Platform_Once()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            // Inter has no CJK coverage and the fake provider cannot place the codepoint either.
            Assert.False(collection.TryMatchCharacter(0x4E00, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out _));
            Assert.False(collection.TryMatchCharacter(0x4E00, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out _));

            Assert.Equal(1, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void Should_Get_Family_Typefaces_Through_Provider()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetFamilyTypefaces("Test Sans", out var familyTypefaces));

            var typeface = Assert.Single(familyTypefaces);

            Assert.Equal("Test Sans", typeface.FontFamily.Name);
            Assert.Equal(FontWeight.Normal, typeface.Weight);
        }

        [Fact]
        public void Dispose_Should_Dispose_Provider()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            ((IDisposable)collection).Dispose();

            Assert.True(provider.IsDisposed);
        }

        [Fact]
        public void Default_TryOpenFontMemory_Should_Load_From_File_Path()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ttf");

            try
            {
                using (var stream = SfntFaceTestHelper.OpenAsset(InterFontUri))
                using (var file = File.Create(path))
                {
                    stream.CopyTo(file);
                }

                var face = new SystemFontFace("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    path, 0);

                Assert.True(face.TryOpenFontMemory(out var fontMemory));

                var glyphTypeface = GlyphTypeface.TryCreate(fontMemory);

                Assert.NotNull(glyphTypeface);
                Assert.Equal("Inter", glyphTypeface.FamilyName);

                glyphTypeface.Dispose();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Registered_Provider_Collection_Should_Serve_FontManager()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                var fontManager = FontManager.Current;

                fontManager.AddFontCollection(new SystemFontCollection(FontManager.SystemFontsKey,
                    new FakeSystemFontProvider()));

                Assert.True(fontManager.TryGetGlyphTypeface(new Typeface("Test Sans"), out var glyphTypeface));
                Assert.Equal("Inter", glyphTypeface.FamilyName);

                // The collection's default drives the manager's default font family.
                Assert.Equal(".Test UI", fontManager.DefaultFontFamily.Name);
            }
        }

        [Fact]
        public void Preset_Provider_Should_Serve_Default_Font_Family()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                // The test preset registers a provider-backed system font collection, whose
                // default face drives the manager's default family.
                var fontManager = FontManager.Current;

                Assert.IsType<SystemFontCollection>(fontManager.SystemFonts);
                Assert.Equal("Inter", fontManager.DefaultFontFamily.Name);
            }
        }

        [Fact]
        public void Options_Should_Override_Collection_Default()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>()
                    .ToConstant(new FontManagerOptions { DefaultFamilyName = "My Font" });

                var fontManager = FontManager.Current;

                fontManager.AddFontCollection(new SystemFontCollection(FontManager.SystemFontsKey,
                    new FakeSystemFontProvider()));

                Assert.Equal("My Font", fontManager.DefaultFontFamily.Name);
            }
        }

        internal class FakeSystemFontProvider : ISystemFontProvider
        {
            private byte[]? _fontData;

            public int GetFontFamilyNamesCount { get; private set; }

            public int TryMatchFamilyCount { get; private set; }

            public int TryMatchCharacterCount { get; private set; }

            public bool IsDisposed { get; private set; }

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = new StreamSystemFontFace(GetFontData(), ".Test UI", FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal);

                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames()
            {
                GetFontFamilyNamesCount++;

                return new[] { "Test Sans" };
            }

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                TryMatchFamilyCount++;

                match = null;

                if (!string.Equals(familyName, "Test Sans", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Designed properties only; the collection computes simulations.
                match = new StreamSystemFontFace(GetFontData(), "Test Sans", FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal);

                return true;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                TryMatchCharacterCount++;

                match = null;

                // The fake can only place Basic Latin.
                if (codepoint > 0x24F)
                {
                    return false;
                }

                match = new StreamSystemFontFace(GetFontData(), "Test Sans", FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal);

                return true;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = null;

                if (!string.Equals(familyName, "Test Sans", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                faces = new[]
                {
                    new StreamSystemFontFace(GetFontData(), "Test Sans", FontStyle.Normal, FontWeight.Normal,
                        FontStretch.Normal)
                };

                return true;
            }

            public void Dispose() => IsDisposed = true;

            private byte[] GetFontData()
            {
                if (_fontData is null)
                {
                    using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);
                    using var ms = new MemoryStream();

                    stream.CopyTo(ms);

                    _fontData = ms.ToArray();
                }

                return _fontData;
            }
        }

        private class StreamSystemFontFace : SystemFontFace
        {
            private readonly byte[] _fontData;

            public StreamSystemFontFace(byte[] fontData, string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch)
                : base(familyName, style, weight, stretch)
            {
                _fontData = fontData;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                if (!SfntFace.TryLoad(new MemoryStream(_fontData), out var face))
                {
                    return false;
                }

                fontMemory = face;

                return true;
            }
        }
    }

    public class FakeSystemFontProviderContractTests : SystemFontProviderContractTests
    {
        protected override ISystemFontProvider CreateProvider() => new SystemFontCollectionTests.FakeSystemFontProvider();

        protected override string KnownFamilyName => "Test Sans";
    }
}
