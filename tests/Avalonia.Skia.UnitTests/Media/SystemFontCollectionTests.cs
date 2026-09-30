#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class SystemFontCollectionTests
    {
        private const string FamilyName = "Noto Mono";

        private static readonly FontCollectionKey s_regularKey =
            new(FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);

        [Fact]
        public void Should_Dispose_Platform_Typeface_When_Nearest_Match_Is_Used()
        {
            var fontManager = new TrackingFontManagerImpl();

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                Assert.True(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

                // The platform answers the black request with the regular face, which the collection
                // then discards in favour of its own nearest match.
                Assert.True(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Black, FontStretch.Normal, out _));

                Assert.Equal(2, fontManager.Created.Count);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        [Fact]
        public void Should_Dispose_Platform_Typeface_When_Glyph_Typeface_Cannot_Be_Created()
        {
            var fontManager = new TrackingFontManagerImpl { Malformed = true };

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                Assert.False(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

                Assert.Single(fontManager.Created);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        [Fact]
        public void Should_Dispose_Platform_Typeface_When_Another_Caller_Registered_The_Face_First()
        {
            var fontManager = new TrackingFontManagerImpl();

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                // A concurrent caller resolves and registers the same face while this caller is
                // inside the platform call, so every registration of this caller's copy loses.
                fontManager.DuringPlatformCall = () => Assert.True(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

                Assert.True(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var glyphTypeface));

                Assert.Equal(2, fontManager.Created.Count);
                Assert.Same(fontCollection.GetCached(FamilyName, s_regularKey), glyphTypeface);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        [Fact]
        public void Should_Dispose_Platform_Typeface_When_Character_Match_Is_Already_Cached()
        {
            var fontManager = new TrackingFontManagerImpl();

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                Assert.True(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var cached));

                Assert.True(fontCollection.MatchCharacterFromPlatform('A', s_regularKey, out var match));

                Assert.Same(cached, match);
                Assert.Equal(2, fontManager.Created.Count);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        [Fact]
        public void Should_Dispose_Platform_Typeface_When_Character_Match_Cannot_Be_Created()
        {
            var fontManager = new TrackingFontManagerImpl { Malformed = true };

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                Assert.False(fontCollection.MatchCharacterFromPlatform('A', s_regularKey, out _));

                Assert.Single(fontManager.Created);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        [Fact]
        public void Should_Dispose_Character_Match_That_Could_Not_Be_Registered()
        {
            var fontManager = new TrackingFontManagerImpl { Unavailable = true };

            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface.With(fontManagerImpl: fontManager)))
            {
                var fontCollection = new TestSystemFontCollection(fontManager);

                // A failed family lookup leaves a negative entry under the face's own names, so the
                // character match that follows cannot be registered anywhere.
                Assert.False(fontCollection.TryGetGlyphTypeface(
                    FamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

                fontManager.Unavailable = false;

                fontCollection.MatchCharacterFromPlatform('A', s_regularKey, out _);

                Assert.Single(fontManager.Created);
                AssertEveryPlatformTypefaceIsCachedOrDisposed(fontCollection, fontManager);
            }
        }

        private static void AssertEveryPlatformTypefaceIsCachedOrDisposed(
            TestSystemFontCollection fontCollection, TrackingFontManagerImpl fontManager)
        {
            var cached = fontCollection.GlyphTypefaceCache.Values
                .SelectMany(x => x.Values)
                .Where(x => x is not null)
                .Select(x => x!.PlatformTypeface)
                .ToHashSet(ReferenceEqualityComparer.Instance);

            foreach (var platformTypeface in fontManager.Created)
            {
                if (cached.Contains(platformTypeface))
                {
                    Assert.False(platformTypeface.IsDisposed, "A cached platform typeface was disposed.");
                }
                else
                {
                    Assert.True(platformTypeface.IsDisposed, "A platform typeface was neither cached nor disposed.");
                }
            }
        }

        private sealed class TestSystemFontCollection : SystemFontCollection
        {
            public TestSystemFontCollection(IFontManagerImpl platformImpl) : base(platformImpl)
            {
            }

            public IDictionary<string, ConcurrentDictionary<FontCollectionKey, GlyphTypeface?>> GlyphTypefaceCache
                => _glyphTypefaceCache;

            public GlyphTypeface? GetCached(string familyName, FontCollectionKey key)
                => _glyphTypefaceCache.TryGetValue(familyName, out var map) && map.TryGetValue(key, out var value)
                    ? value
                    : null;

            public bool MatchCharacterFromPlatform(int codepoint, FontCollectionKey key,
                [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
                => TryMatchCharacterFromPlatform(codepoint, key, null, null, out glyphTypeface);
        }

        /// <summary>
        /// Font manager that resolves every family and character request to the embedded Noto Mono
        /// regular face and records each platform typeface it hands out, so a test can check that
        /// the collection either keeps or disposes every one of them.
        /// </summary>
        private sealed class TrackingFontManagerImpl : IFontManagerImpl
        {
            private const string BackingFontUri =
                "resm:Avalonia.Skia.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Skia.UnitTests";

            private readonly IFontManagerImpl _inner = new FontManagerImpl();

            public List<TrackingPlatformTypeface> Created { get; } = new();

            /// <summary>Hands out typefaces without any tables, so glyph typeface creation fails.</summary>
            public bool Malformed { get; set; }

            /// <summary>Makes family requests fail as if the family were not installed.</summary>
            public bool Unavailable { get; set; }

            /// <summary>Runs once inside the next platform call, standing in for a concurrent caller.</summary>
            public Action? DuringPlatformCall { get; set; }

            public string GetDefaultFontFamilyName() => FamilyName;

            public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false) => Array.Empty<string>();

            public bool TryCreateGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
                FontStretch stretch, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
            {
                if (Unavailable)
                {
                    platformTypeface = null;

                    return false;
                }

                return TryCreateTracked(out platformTypeface);
            }

            public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight,
                FontStretch fontStretch, string? familyName, CultureInfo? culture,
                [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
                => TryCreateTracked(out platformTypeface);

            public bool TryCreateGlyphTypeface(Stream stream, FontSimulations fontSimulations,
                [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
                => _inner.TryCreateGlyphTypeface(stream, fontSimulations, out platformTypeface);

            public bool TryGetFamilyTypefaces(string familyName,
                [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
            {
                familyTypefaces = null;

                return false;
            }

            private bool TryCreateTracked([NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
            {
                var duringPlatformCall = DuringPlatformCall;
                DuringPlatformCall = null;
                duringPlatformCall?.Invoke();

                var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();

                using var stream = assetLoader.Open(new Uri(BackingFontUri, UriKind.Absolute));

                if (!_inner.TryCreateGlyphTypeface(stream, FontSimulations.None, out var inner))
                {
                    platformTypeface = null;

                    return false;
                }

                var tracked = new TrackingPlatformTypeface(inner, Malformed);
                Created.Add(tracked);
                platformTypeface = tracked;

                return true;
            }
        }

        private sealed class TrackingPlatformTypeface : IPlatformTypeface
        {
            private readonly IPlatformTypeface _inner;
            private readonly bool _malformed;

            public TrackingPlatformTypeface(IPlatformTypeface inner, bool malformed)
            {
                _inner = inner;
                _malformed = malformed;
            }

            public bool IsDisposed { get; private set; }

            public string FamilyName => _inner.FamilyName;

            public FontWeight Weight => _inner.Weight;

            public FontStyle Style => _inner.Style;

            public FontStretch Stretch => _inner.Stretch;

            public FontSimulations FontSimulations => _inner.FontSimulations;

            public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
            {
                if (_malformed)
                {
                    table = default;

                    return false;
                }

                return _inner.TryGetTable(tag, out table);
            }

            public bool TryGetStream([NotNullWhen(true)] out Stream? stream) => _inner.TryGetStream(out stream);

            public void Dispose()
            {
                IsDisposed = true;
                _inner.Dispose();
            }
        }
    }
}
