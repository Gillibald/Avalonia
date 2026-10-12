using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Android.Platform.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Android.UnitTests
{
    /// <summary>
    /// The catalog on API 29 and later, where the platform lists the installed fonts and answers
    /// character fallback; a fake stands in for the platform.
    /// </summary>
    public class AndroidSystemFontSourceTests : IClassFixture<AndroidFontFixtureHolder>
    {
        private readonly AndroidFontFixture _fixture;

        public AndroidSystemFontSourceTests(AndroidFontFixtureHolder holder)
        {
            _fixture = holder.Fixture;
        }

        private static string Describe(SystemFontFace? face) =>
            face is null ? "none" : Path.GetFileName(face.FilePath) + "#" + face.FaceIndex;

        private string UpdatedFont(string sourceFileName, string fileName)
        {
            // Updated fonts live in their own directories below /data/fonts/files.
            var directory = Path.Combine(_fixture.Root, "data", "fonts", "files", "~~" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, fileName);

            File.Copy(Fixtures.GetFontPath(sourceFileName), path);

            return path;
        }

        private AndroidFontConfiguration NoConfiguration() =>
            new(new[] { new AndroidFontConfigurationFile(Path.Combine(_fixture.EtcDirectory, "absent.xml"), _fixture.FontDirectory) },
                Array.Empty<AndroidFontConfigurationFile>());

        private static SystemFontFace? MatchCharacter(AndroidFontCatalog catalog, int codepoint, string? familyName = null,
            string culture = "")
        {
            return catalog.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, familyName,
                CultureInfo.GetCultureInfo(culture), out var match)
                ? match
                : null;
        }

        [Fact]
        public void With_A_Configuration_Known_Names_Do_Not_Enumerate_The_Platform_Fonts()
        {
            // Enumerating makes the platform map every installed font file into the process for
            // good, so names the configuration resolves never trigger it.
            var source = new FakeSystemFontSource { Fonts = { new AndroidSystemFont(_fixture.FontPath("Inter-Regular.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>()) } };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            catalog.GetFamilyNames();
            catalog.TryGetDefaultFace(out _);

            foreach (var name in new[] { "sans-serif", "arial", "Inter", "Noto Sans JP" })
            {
                Assert.True(catalog.TryMatchFamily(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
                Assert.True(catalog.TryGetFamilyFaces(name, out _));
            }

            MatchCharacter(catalog, 0x05D0);
            MatchCharacter(catalog, 'a', "monospace");
            MatchCharacter(catalog, 0x05D0, "Noto Sans Hebrew");

            Assert.Equal(0, source.EnumerationCount);
        }

        [Fact]
        public void The_First_Name_Only_The_Platform_Knows_Enumerates_The_Platform_Fonts_Once()
        {
            // Updated fonts outside the configuration (Google Sans under /data/fonts/files) are
            // reachable by name only through the platform's font list.
            var updated = UpdatedFont("Manrope-Light.ttf", "Manrope-Light.ttf");
            var source = new FakeSystemFontSource
            {
                Fonts =
                {
                    new AndroidSystemFont(_fixture.FontPath("Inter-Regular.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>()),
                    new AndroidSystemFont(updated, 0, 300, false, null, Array.Empty<FontsXmlAxis>()),
                },
            };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.True(catalog.TryMatchFamily("Manrope", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var match));
            Assert.Equal(updated, match.FilePath);
            Assert.Equal("Manrope", match.FamilyName);
            Assert.Equal(1, source.EnumerationCount);

            Assert.False(catalog.TryMatchFamily("Segoe UI", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
            Assert.True(catalog.TryGetFamilyFaces("manrope", out var faces));
            Assert.Equal(updated, Assert.Single(faces).FilePath);
            Assert.Equal(updated, MatchCharacter(catalog, 'a', "Manrope")?.FilePath);
            Assert.Equal(1, source.EnumerationCount);

            // Fonts the configuration also names keep resolving to the configured faces.
            Assert.Equal("Inter-Regular.ttf#0", Describe(catalog.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var inter) ? inter : null));

            // Only the configuration's names are listed.
            Assert.DoesNotContain("Manrope", catalog.GetFamilyNames());

            // Reading the names of the platform's fonts leaves none of them mapped.
            Assert.DoesNotContain(FontFileCache.GetSnapshot(),
                x => x.Key is string path && path.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Character_Fallback_For_An_Unknown_Family_Does_Not_Enumerate_The_Platform_Fonts()
        {
            // Text without a system family asks with the font manager's placeholder name ($Default),
            // so character fallback for unknown names must not read the platform font list.
            var updated = UpdatedFont("Manrope-Light.ttf", "Manrope-Light.ttf");
            var source = new FakeSystemFontSource { Fonts = { new AndroidSystemFont(updated, 0, 300, false, null, Array.Empty<FontsXmlAxis>()) } };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a', "$Default")));
            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0, "Manrope")));
            Assert.Equal(0, source.EnumerationCount);
        }

        [Fact]
        public void A_Failing_Platform_Font_List_Is_Not_Read_Again()
        {
            var source = new FakeSystemFontSource { FailEnumeration = true };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.False(catalog.TryMatchFamily("Manrope", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
            Assert.False(catalog.TryMatchFamily("Segoe UI", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
            Assert.Equal(1, source.EnumerationCount);
            Assert.True(catalog.TryMatchFamily("sans-serif", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Fact]
        public void Platform_Fallback_Answers_Keep_Their_Own_Paths()
        {
            // Updated fonts live in their own directories below /data/fonts/files.
            var updated = UpdatedFont("NotoSansHebrew-Regular.ttf", "NotoSansHebrew-Regular.ttf");
            var source = new FakeSystemFontSource
            {
                Match = (_, codepoint, _, _, _) =>
                    codepoint == 0x05D0 ? new AndroidSystemFont(updated, 0, 400, false, "und-Hebr", Array.Empty<FontsXmlAxis>()) : null,
            };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            var match = MatchCharacter(catalog, 0x05D0);

            Assert.Equal(updated, match?.FilePath);
            Assert.Equal("Noto Sans Hebrew", match?.FamilyName);
        }

        [Fact]
        public void Without_A_Configuration_Platform_Fonts_Resolve_By_Real_Name()
        {
            var updated = UpdatedFont("Manrope-Light.ttf", "Manrope-Light.ttf");
            var source = new FakeSystemFontSource { Fonts = { new AndroidSystemFont(updated, 0, 300, false, null, Array.Empty<FontsXmlAxis>()) } };
            var catalog = AndroidFontCatalog.Create(NoConfiguration(), source);

            Assert.True(catalog.TryMatchFamily("Manrope", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var match));
            Assert.Equal(updated, match.FilePath);
            Assert.Equal("Manrope", match.FamilyName);
            Assert.Equal(1, source.EnumerationCount);
        }
        [Fact]
        public void Platform_Fallback_Answers_Character_Matches()
        {
            var source = new FakeSystemFontSource
            {
                Match = (family, codepoint, _, _, _) =>
                    codepoint == 0x4E2D ? new AndroidSystemFont(_fixture.FontPath("CJK.ttc"), 1, 400, false, "ko", Array.Empty<FontsXmlAxis>()) : null,
            };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            // The managed walk would pick the Japanese face for ja-JP; the platform's answer wins.
            Assert.Equal("CJK.ttc#1", Describe(MatchCharacter(catalog, 0x4E2D, culture: "ja-JP")));

            var call = Assert.Single(source.Calls);

            Assert.Equal("sans-serif", call.Family);
            Assert.Equal("ja-Jpan-JP", call.Locales);
            Assert.Equal(400, call.Weight);
        }

        [Fact]
        public void Platform_Fallback_Receives_The_Configured_Family()
        {
            var source = new FakeSystemFontSource();
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            MatchCharacter(catalog, 0x0628, "Times New Roman", "zh-TW");
            MatchCharacter(catalog, 0x0628, "unknown family");
            MatchCharacter(catalog, 0x0628, "Noto Sans Hebrew");
            MatchCharacter(catalog, 0x0628);

            Assert.Equal(new[] { "serif", "sans-serif", "sans-serif", "sans-serif" }, source.Calls.Select(x => x.Family));
            Assert.Equal(new[] { "zh-Hant-TW", "", "", "" }, source.Calls.Select(x => x.Locales));
        }

        [Fact]
        public void Platform_Answers_Without_Coverage_Fall_Back_To_The_Configuration()
        {
            var source = new FakeSystemFontSource
            {
                // The platform answers with its default font when nothing covers the codepoint.
                Match = (_, _, _, _, _) => new AndroidSystemFont(_fixture.FontPath("Inter-Regular.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>()),
            };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0)));
            Assert.Null(MatchCharacter(catalog, 0x20000));
        }

        [Fact]
        public void Platform_Answers_That_Cannot_Be_Opened_Fall_Back_To_The_Configuration()
        {
            var source = new FakeSystemFontSource
            {
                Match = (_, _, _, _, _) => new AndroidSystemFont(Path.Combine(_fixture.Root, "gone.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>()),
            };
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.Equal("NotoSansTamil-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x0B95)));
        }

        [Fact]
        public void A_Requested_Family_That_Covers_The_Codepoint_Is_Not_Sent_To_The_Platform()
        {
            var source = new FakeSystemFontSource();
            var catalog = AndroidFontCatalog.Create(_fixture.VendorConfiguration(), source);

            Assert.Equal("NotoMono-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a', "monospace")));
            Assert.Empty(source.Calls);
        }

        [Fact]
        public void Without_A_Configuration_The_Platform_Fonts_Form_The_Model()
        {
            var source = PlatformOnlySource();
            var catalog = AndroidFontCatalog.Create(NoConfiguration(), source);

            Assert.True(catalog.TryGetDefaultFace(out var face));
            Assert.Equal("sans-serif", face.FamilyName);
            Assert.Equal("Inter-Regular.ttf#0", Describe(face));

            Assert.True(catalog.TryMatchFamily("sans-serif", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var bold));
            Assert.Equal("Inter-Bold.ttf#0", Describe(bold));

            Assert.True(catalog.TryMatchFamily("monospace", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var mono));
            Assert.Equal("monospace", mono.FamilyName);
            Assert.Equal("NotoMono-Regular.ttf#0", Describe(mono));

            Assert.True(catalog.TryMatchFamily("Inter", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var inter));
            Assert.Equal("Inter-Bold.ttf#0", Describe(inter));

            Assert.True(catalog.TryGetFamilyFaces("Inter", out var faces));
            Assert.Equal(2, faces.Count);

            var names = catalog.GetFamilyNames();

            Assert.Equal(new[] { "sans-serif", "serif", "monospace" }, names.Take(3));
            Assert.Contains("Inter", names);
            Assert.Contains("Noto Mono", names);
            Assert.Contains("Noto Sans Hebrew", names);

            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0)));
            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a')));
        }

        [Fact]
        public void Without_A_Configuration_Or_Platform_Fonts_The_Catalog_Is_Empty()
        {
            var catalog = AndroidFontCatalog.Create(NoConfiguration(), null);

            Assert.Empty(catalog.GetFamilyNames());
            Assert.False(catalog.TryGetDefaultFace(out _));
            Assert.False(catalog.TryMatchFamily("sans-serif", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
            Assert.Null(MatchCharacter(catalog, 'a'));
        }

        private FakeSystemFontSource PlatformOnlySource()
        {
            var regular = new AndroidSystemFont(_fixture.FontPath("Inter-Regular.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>());
            var bold = new AndroidSystemFont(_fixture.FontPath("Inter-Bold.ttf"), 0, 700, false, null, Array.Empty<FontsXmlAxis>());
            var mono = new AndroidSystemFont(_fixture.FontPath("NotoMono-Regular.ttf"), 0, 400, false, null, Array.Empty<FontsXmlAxis>());
            var serif = new AndroidSystemFont(_fixture.FontPath("NotoSans-Italic.ttf"), 0, 400, true, null, Array.Empty<FontsXmlAxis>());
            var hebrew = new AndroidSystemFont(_fixture.FontPath("NotoSansHebrew-Regular.ttf"), 0, 400, false, "und-Hebr", Array.Empty<FontsXmlAxis>());

            return new FakeSystemFontSource
            {
                Fonts = { regular, bold, mono, serif, hebrew },
                Match = (family, codepoint, weight, _, _) =>
                {
                    if (codepoint == 0x05D0)
                    {
                        return hebrew;
                    }

                    return family switch
                    {
                        "monospace" => mono,
                        "serif" => serif,
                        _ => weight >= 600 ? bold : regular,
                    };
                },
            };
        }

        private sealed class FakeSystemFontSource : AndroidSystemFontSource
        {
            public List<AndroidSystemFont> Fonts { get; } = new();

            public Func<string, int, int, bool, string, AndroidSystemFont?>? Match { get; set; }

            public List<(string Family, int Codepoint, int Weight, bool Italic, string Locales)> Calls { get; } = new();

            public int EnumerationCount { get; private set; }

            public bool FailEnumeration { get; set; }

            public override IReadOnlyList<AndroidSystemFont> GetFonts()
            {
                EnumerationCount++;

                if (FailEnumeration)
                {
                    throw new InvalidOperationException("The platform font list is unavailable.");
                }

                return Fonts;
            }

            public override bool TryMatch(string familyName, int codepoint, int weight, bool italic, string languageTags,
                [NotNullWhen(true)] out AndroidSystemFont? font)
            {
                Calls.Add((familyName, codepoint, weight, italic, languageTags));

                font = Match?.Invoke(familyName, codepoint, weight, italic, languageTags);

                return font != null;
            }
        }
    }
}
