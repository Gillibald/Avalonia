using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Android.Platform.Fonts;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Android.UnitTests
{
    public class AndroidFontProviderContractTests : SystemFontProviderContractTests, IClassFixture<AndroidFontFixtureHolder>
    {
        private readonly AndroidFontFixture _fixture;

        public AndroidFontProviderContractTests(AndroidFontFixtureHolder holder)
        {
            _fixture = holder.Fixture;
        }

        protected override ISystemFontProvider CreateProvider() =>
            new AndroidFontProvider(_fixture.VendorConfiguration(), () => null);

        protected override string KnownFamilyName => "sans-serif";
    }

    public class AndroidFontProviderTests : IClassFixture<AndroidFontFixtureHolder>
    {
        private readonly AndroidFontFixture _fixture;

        public AndroidFontProviderTests(AndroidFontFixtureHolder holder)
        {
            _fixture = holder.Fixture;
        }

        [Fact]
        public void Constructing_The_Provider_Reads_Nothing()
        {
            var created = 0;

            using var provider = new AndroidFontProvider(_fixture.VendorConfiguration(), () =>
            {
                created++;

                return null;
            });

            Assert.Equal(0, created);

            Assert.Contains("sans-serif", provider.GetFontFamilyNames());
            Assert.Equal(1, created);

            provider.TryGetDefaultFontFace(out _);
            Assert.Equal(1, created);
        }

        [Fact]
        public void Provider_Answers_From_The_Catalog()
        {
            using var provider = new AndroidFontProvider(_fixture.VendorConfiguration(), () => null);

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.Equal(_fixture.FontPath("Inter-Regular.ttf"), face.FilePath);

            Assert.True(provider.TryMatchFamily("serif-bold", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var bold));
            Assert.Equal(_fixture.FontPath("Inter-Bold.ttf"), bold.FilePath);

            Assert.True(provider.TryMatchCharacter(0x4E2D, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null,
                CultureInfo.GetCultureInfo("ko-KR"), out var korean));
            Assert.Equal(_fixture.FontPath("CJK.ttc"), korean.FilePath);
            Assert.Equal(1, korean.FaceIndex);

            Assert.True(provider.TryGetFamilyFaces("serif", out var faces));
            Assert.Equal(2, faces.Count);
        }

        [Fact]
        public void Disposing_The_Provider_Disposes_The_Platform_Source_And_Ends_Answers()
        {
            var source = new DisposableSource();
            var provider = new AndroidFontProvider(_fixture.VendorConfiguration(), () => source);

            Assert.NotEmpty(provider.GetFontFamilyNames());

            provider.Dispose();

            Assert.True(source.IsDisposed);
            Assert.Empty(provider.GetFontFamilyNames());
            Assert.False(provider.TryGetDefaultFontFace(out _));
            Assert.False(provider.TryMatchCharacter('a', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out _));
        }

        [Fact]
        public void Every_Face_Of_The_System_Fonts_Shares_One_Mapping_Per_File()
        {
            using var provider = new AndroidFontProvider(_fixture.VendorConfiguration(), () => null);

            var memories = new List<IFontMemory>();

            try
            {
                foreach (var name in provider.GetFontFamilyNames())
                {
                    Assert.True(provider.TryGetFamilyFaces(name, out var faces));

                    foreach (var face in faces)
                    {
                        Assert.True(face.TryOpenFontMemory(out var memory));
                        memories.Add(memory);
                    }
                }

                foreach (var culture in new[] { "", "ja-JP", "ko-KR", "zh-CN", "zh-TW" })
                {
                    foreach (var codepoint in new[] { 'a', 0x0628, 0x05D0, 0x0B95, 0x4E2D, 0x3042, 0x1F600 })
                    {
                        foreach (var family in new[] { null, "serif" })
                        {
                            Assert.True(provider.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal,
                                FontStretch.Normal, family, CultureInfo.GetCultureInfo(culture), out var face));
                            Assert.True(face.TryOpenFontMemory(out var memory));
                            memories.Add(memory);
                        }
                    }
                }

                var entries = FontFileCache.GetSnapshot()
                    .Where(x => x.Key is string path && path.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var expectedFiles = new[]
                {
                    "Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Italic.ttf", "NotoMono-Regular.ttf", "CJK.ttc",
                    "ArabicTriple.ttc", "EmojiPair.ttc", "NotoSansHebrew-Regular.ttf", "NotoSansTamil-Regular.ttf",
                };

                Assert.Equal(expectedFiles.OrderBy(x => x), entries.Select(x => Path.GetFileName((string)x.Key)).OrderBy(x => x));
                Assert.All(entries, x => Assert.Equal(typeof(FontFileMemory), x.OwnerType));
                Assert.Equal(expectedFiles.Sum(x => new FileInfo(_fixture.FontPath(x)).Length), entries.Sum(x => (long)x.Length));

                // Every face of the collection was loaded, over one mapping.
                Assert.Equal(new[] { 0, 1, 2, 3 },
                    memories.OfType<SfntFace>().Where(x => x.TryGetFontFileData(out var data, out _) &&
                        data.Length == new FileInfo(_fixture.FontPath("CJK.ttc")).Length).Select(x => x.FaceIndex).Distinct().OrderBy(x => x));
            }
            finally
            {
                foreach (var memory in memories)
                {
                    memory.Dispose();
                }
            }

            Assert.DoesNotContain(FontFileCache.GetSnapshot(),
                x => x.Key is string path && path.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class DisposableSource : AndroidSystemFontSource
        {
            public bool IsDisposed { get; private set; }

            public override IReadOnlyList<AndroidSystemFont> GetFonts() => Array.Empty<AndroidSystemFont>();

            public override bool TryMatch(string familyName, int codepoint, int weight, bool italic, string languageTags,
                [NotNullWhen(true)] out AndroidSystemFont? font)
            {
                font = null;

                return false;
            }

            public override void Dispose() => IsDisposed = true;
        }
    }
}
