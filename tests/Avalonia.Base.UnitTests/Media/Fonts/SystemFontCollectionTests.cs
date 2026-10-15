using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
        private const string InterVariableFontUri = "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";

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
        public void Variable_Face_Should_Reach_Requested_Weight_Along_Its_Axes()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Test Variable", FontStyle.Normal, FontWeight.SemiBold,
                FontStretch.Normal, out var glyphTypeface));

            Assert.Equal(FontSimulations.None, glyphTypeface.FontSimulations);
            Assert.Equal(FontWeight.SemiBold, glyphTypeface.Weight);
            Assert.False(glyphTypeface.VariationPosition.IsDefault);

            // The varied face is cached under the requested key.
            Assert.True(collection.TryGetGlyphTypeface("Test Variable", FontStyle.Normal, FontWeight.SemiBold,
                FontStretch.Normal, out var other));

            Assert.Same(glyphTypeface, other);
            Assert.Equal(1, provider.TryMatchFamilyCount);
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
        public void Unknown_Family_Should_Not_Be_Listed()
        {
            // A picker over the collection binds to every listed family; a listed name that does
            // not resolve fails to create its typeface.
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.False(collection.TryGetGlyphTypeface("Unknown", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.DoesNotContain(collection, x => x.Name == "Unknown");
            Assert.Equal(new[] { "Test Sans" }, collection.Select(x => x.Name));
        }

        [Fact]
        public void Family_Found_After_A_Miss_Should_Be_Listed()
        {
            var provider = new FakeSystemFontProvider { MatchFromCall = 2 };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.False(collection.TryGetGlyphTypeface("Late Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));
            Assert.True(collection.TryGetGlyphTypeface("Late Sans", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out _));

            Assert.Contains(collection, x => x.Name == "Late Sans");
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
        public void Character_Match_Should_Simulate_Styles_The_Matched_Face_Lacks()
        {
            var provider = new FakeSystemFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            // The fake places Basic Latin in its only face, Inter Regular.
            Assert.True(collection.TryMatchCharacter('A', FontStyle.Italic, FontWeight.Bold, FontStretch.Normal,
                null, null, out var match));

            Assert.Equal(FontStyle.Italic, match.Style);
            Assert.Equal(FontWeight.Bold, match.Weight);

            // Drawing the match resolves its typeface through the collection: the regular face with
            // bold and oblique simulation, over the same font data.
            Assert.True(collection.TryGetGlyphTypeface("Inter", match.Style, match.Weight, match.Stretch,
                out var simulated));
            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, simulated.FontSimulations);

            Assert.True(collection.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var regularMatch));
            Assert.True(collection.TryGetGlyphTypeface("Inter", regularMatch.Style, regularMatch.Weight,
                regularMatch.Stretch, out var regular));
            Assert.Equal(FontSimulations.None, regular.FontSimulations);

            Assert.NotSame(regular, simulated);
            FontFileSharingTests.AssertSameFileData(new[]
            {
                Assert.IsType<SfntFace>(regular.FontMemory),
                Assert.IsType<SfntFace>(simulated.FontMemory)
            });
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

        [Fact]
        public void Typefaces_Of_One_Collection_File_Share_One_File_Data()
        {
            // Faces share file data through the font manager of the application loading them.
            using var application = UnitTestApplication.Start();
            using var fixture = new FontFileFixture();

            var path = fixture.Write("collection.ttc", FontFileFixture.BuildCollection(
                FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri),
                FontFileFixture.ReadAsset(FontFileFixture.InterBoldUri)));

            var collection = new SystemFontCollection(FontManager.SystemFontsKey, new CollectionFileProvider(path));

            try
            {
                Assert.True(collection.TryGetGlyphTypeface("Collection Sans", FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal, out var regular));
                Assert.True(collection.TryGetGlyphTypeface("Collection Sans", FontStyle.Normal, FontWeight.Bold,
                    FontStretch.Normal, out var bold));

                Assert.NotSame(regular, bold);
                Assert.Equal(FontSimulations.None, bold.FontSimulations);

                var faces = new[]
                {
                    Assert.IsType<SfntFace>(regular.FontMemory),
                    Assert.IsType<SfntFace>(bold.FontMemory)
                };

                Assert.Equal(0, faces[0].FaceIndex);
                Assert.Equal(1, faces[1].FaceIndex);

                FontFileSharingTests.AssertSameFileData(faces);
            }
            finally
            {
                ((IDisposable)collection).Dispose();
            }
        }

        [Fact]
        public void Typeface_Lost_To_A_Concurrent_First_Request_Should_Be_Disposed()
        {
            var provider = new BarrierSystemFontProvider(2);
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            var results = new GlyphTypeface?[2];

            // Both requests miss the cache, wait for each other inside the provider, then race to
            // publish their typeface.
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                collection.TryGetGlyphTypeface("Test Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    out results[i]);
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.NotNull(results[0]);
            Assert.Same(results[0], results[1]);
            Assert.Equal(2, provider.OpenedFaces.Count);

            // Only the published typeface keeps its font data; the loser's was released.
            var alive = provider.OpenedFaces.Where(f => f.TryGetTable(new OpenTypeTag('n', 'a', 'm', 'e'), out _));

            Assert.Same(results[0]!.FontMemory, Assert.Single(alive));
        }

        /// <summary>
        /// Holds every family match until the given number of requests arrived, and records the
        /// faces it opens.
        /// </summary>
        private sealed class BarrierSystemFontProvider : ISystemFontProvider
        {
            private readonly Barrier _barrier;
            private readonly byte[] _fontData = FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri);

            public BarrierSystemFontProvider(int participants) => _barrier = new Barrier(participants);

            public ConcurrentBag<SfntFace> OpenedFaces { get; } = new();

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = null;

                return false;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new[] { "Test Sans" };

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                _barrier.SignalAndWait(TimeSpan.FromSeconds(10));

                match = new RecordingFontFace(this);

                return true;
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

            public void Dispose() => _barrier.Dispose();

            private sealed class RecordingFontFace : SystemFontFace
            {
                private readonly BarrierSystemFontProvider _owner;

                public RecordingFontFace(BarrierSystemFontProvider owner)
                    : base("Test Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)
                {
                    _owner = owner;
                }

                public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
                {
                    fontMemory = null;

                    if (!SfntFace.TryLoad(new MemoryStream(_owner._fontData), out var face))
                    {
                        return false;
                    }

                    _owner.OpenedFaces.Add(face);

                    fontMemory = face;

                    return true;
                }
            }
        }

        /// <summary>
        /// Serves the regular and bold faces of one two-face collection file by path.
        /// </summary>
        private sealed class CollectionFileProvider : ISystemFontProvider
        {
            private const string FamilyName = "Collection Sans";

            private readonly string _path;

            public CollectionFileProvider(string path) => _path = path;

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = CreateFace(FontWeight.Normal);

                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new[] { FamilyName };

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase)
                    ? CreateFace(weight >= FontWeight.SemiBold ? FontWeight.Bold : FontWeight.Normal)
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

            private SystemFontFace CreateFace(FontWeight weight) =>
                new(FamilyName, FontStyle.Normal, weight, FontStretch.Normal, _path,
                    weight == FontWeight.Bold ? 1 : 0);
        }

        internal class FakeSystemFontProvider : ISystemFontProvider
        {
            private byte[]? _fontData;
            private byte[]? _variableFontData;

            public int GetFontFamilyNamesCount { get; private set; }

            public int TryMatchFamilyCount { get; private set; }

            public int TryMatchCharacterCount { get; private set; }

            public bool IsDisposed { get; private set; }

            /// <summary>
            /// From this call on, "Late Sans" resolves as well (0: never).
            /// </summary>
            public int MatchFromCall { get; set; }

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

                if (string.Equals(familyName, "Test Variable", StringComparison.OrdinalIgnoreCase))
                {
                    // The default instance of a variable face.
                    match = new StreamSystemFontFace(GetVariableFontData(), "Test Variable", FontStyle.Normal,
                        FontWeight.Normal, FontStretch.Normal);

                    return true;
                }

                var isLate = string.Equals(familyName, "Late Sans", StringComparison.OrdinalIgnoreCase) &&
                             MatchFromCall > 0 && TryMatchFamilyCount >= MatchFromCall;

                if (!isLate && !string.Equals(familyName, "Test Sans", StringComparison.OrdinalIgnoreCase))
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

            private byte[] GetFontData() => _fontData ??= ReadAsset(InterFontUri);

            private byte[] GetVariableFontData() => _variableFontData ??= ReadAsset(InterVariableFontUri);

            private static byte[] ReadAsset(string uri)
            {
                using var stream = SfntFaceTestHelper.OpenAsset(uri);
                using var ms = new MemoryStream();

                stream.CopyTo(ms);

                return ms.ToArray();
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
