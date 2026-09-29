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
    public class SystemFontCollectionIdentityTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Path_Less_Descriptors_Of_One_Identity_Should_Load_The_Font_Once()
        {
            var provider = new CountingProvider(identity: "inter");
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            // Every key misses the cache, so the provider hands out a new descriptor each time.
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold, FontWeight.Light, FontWeight.Black })
            {
                Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, weight,
                    FontStretch.Normal, out _));
            }

            Assert.True(provider.TryMatchFamilyCount > 1);
            Assert.Equal(1, provider.LoadCount);
        }

        [Fact]
        public void Path_Less_Descriptors_Without_Identity_Should_Not_Share_A_Root()
        {
            var provider = new CountingProvider(identity: null);
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));
            Assert.True(collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out _));

            Assert.Equal(2, provider.LoadCount);
        }

        private sealed class CountingProvider : ISystemFontProvider
        {
            private readonly object? _identity;
            private byte[]? _fontData;

            public CountingProvider(object? identity)
            {
                _identity = identity;
            }

            public int TryMatchFamilyCount { get; private set; }

            public int LoadCount { get; set; }

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = null;

                return false;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new[] { "Test Sans" };

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                TryMatchFamilyCount++;

                match = string.Equals(familyName, "Test Sans", StringComparison.OrdinalIgnoreCase)
                    ? new CountingFontFace(this, _identity)
                    : null;

                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = null;

                return false;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = null;

                return false;
            }

            public void Dispose()
            {
            }

            public byte[] GetFontData()
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

        private sealed class CountingFontFace : SystemFontFace
        {
            private readonly CountingProvider _provider;
            private readonly object? _identity;

            public CountingFontFace(CountingProvider provider, object? identity)
                : base("Test Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)
            {
                _provider = provider;
                _identity = identity;
            }

            internal override object? Identity => _identity;

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                _provider.LoadCount++;

                fontMemory = null;

                if (!SfntFace.TryLoad(new MemoryStream(_provider.GetFontData()), out var face))
                {
                    return false;
                }

                fontMemory = face;

                return true;
            }
        }
    }
}
