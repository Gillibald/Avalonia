using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Android.Platform.Fonts;
using Avalonia.Media;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Android.UnitTests
{
    public class AndroidFontCatalogTests : IClassFixture<AndroidFontFixtureHolder>
    {
        private readonly AndroidFontFixture _fixture;

        public AndroidFontCatalogTests(AndroidFontFixtureHolder holder)
        {
            _fixture = holder.Fixture;
        }

        private AndroidFontCatalog Vendor(bool withAdditions = false) =>
            AndroidFontCatalog.Create(_fixture.VendorConfiguration(withAdditions));

        private static SystemFontFace MatchFamily(AndroidFontCatalog catalog, string familyName,
            FontStyle style = FontStyle.Normal, FontWeight weight = FontWeight.Normal)
        {
            Assert.True(catalog.TryMatchFamily(familyName, style, weight, FontStretch.Normal, out var match));

            return match;
        }

        private static SystemFontFace? MatchCharacter(AndroidFontCatalog catalog, int codepoint, string? familyName = null,
            string? culture = "", FontWeight weight = FontWeight.Normal)
        {
            var cultureInfo = culture is null ? null : CultureInfo.GetCultureInfo(culture);

            return catalog.TryMatchCharacter(codepoint, FontStyle.Normal, weight, FontStretch.Normal, familyName,
                cultureInfo, out var match)
                ? match
                : null;
        }

        private static string Describe(SystemFontFace? face) =>
            face is null ? "none" : Path.GetFileName(face.FilePath) + "#" + face.FaceIndex;

        [Fact]
        public void Family_List_Holds_Named_Families_And_Aliases_With_Existing_Files_In_File_Order()
        {
            Assert.Equal(
                new[]
                {
                    "sans-serif", "sans-serif-medium", "arial", "serif", "serif-bold", "times new roman", "monospace",
                    "vendor-ui",
                },
                Vendor().GetFamilyNames());
        }

        [Fact]
        public void Default_Face_Is_The_Normal_Entry_Of_The_First_Family()
        {
            Assert.True(Vendor().TryGetDefaultFace(out var face));

            Assert.Equal("sans-serif", face.FamilyName);
            Assert.Equal(_fixture.FontPath("Inter-Regular.ttf"), face.FilePath);
            Assert.Equal(0, face.FaceIndex);
            Assert.Equal(FontWeight.Normal, face.Weight);
        }

        [Fact]
        public void Aliases_Resolve_To_Their_Target_Family_Ignoring_Case()
        {
            var match = MatchFamily(Vendor(), "Arial");

            Assert.Equal("sans-serif", match.FamilyName);
            Assert.Equal("Inter-Regular.ttf#0", Describe(match));
        }

        [Fact]
        public void Weighted_Aliases_Resolve_To_The_Entry_Nearest_Their_Weight()
        {
            var match = MatchFamily(Vendor(), "serif-bold");

            Assert.Equal("serif", match.FamilyName);
            Assert.Equal("Inter-Bold.ttf#0", Describe(match));
            Assert.Equal(FontWeight.Bold, match.Weight);
        }

        [Fact]
        public void Nearest_Entry_Follows_The_Platform_Weight_And_Slant_Distance()
        {
            var catalog = Vendor();

            Assert.Equal("NotoSans-Italic.ttf#0", Describe(MatchFamily(catalog, "serif")));
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchFamily(catalog, "serif", weight: FontWeight.Bold)));
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchFamily(catalog, "serif", weight: FontWeight.SemiBold)));
            Assert.Equal("NotoSans-Italic.ttf#0", Describe(MatchFamily(catalog, "serif", FontStyle.Italic)));

            // A slant mismatch costs two weight steps: bold upright beats regular italic.
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchFamily(catalog, "serif", FontStyle.Italic, FontWeight.Bold)));
        }

        [Fact]
        public void Equal_Distances_Resolve_To_The_Entry_Listed_First()
        {
            var catalog = AndroidFontCatalog.Create(_fixture.AospConfiguration());

            // 800 is one step from both 700 and 900; the AOSP file lists the 900 weights first.
            Assert.Equal("Roboto-Black.ttf#0", Describe(MatchFamily(catalog, "sans-serif", weight: FontWeight.ExtraBold)));
            Assert.Equal("Roboto-Bold.ttf#0", Describe(MatchFamily(catalog, "sans-serif", weight: FontWeight.Bold)));
            Assert.Equal("Roboto-Medium.ttf#0", Describe(MatchFamily(catalog, "sans-serif-medium")));
            Assert.Equal("Roboto-Thin.ttf#0", Describe(MatchFamily(catalog, "sans-serif-thin")));
            Assert.Equal("RobotoCondensed-Light.ttf#0", Describe(MatchFamily(catalog, "sans-serif-condensed-light")));
        }

        [Fact]
        public void Aosp_Family_List_And_Default_Face()
        {
            var catalog = AndroidFontCatalog.Create(_fixture.AospConfiguration());
            var names = catalog.GetFamilyNames();

            Assert.Equal(33, names.Count);
            Assert.Equal(new[] { "sans-serif", "sans-serif-thin", "sans-serif-light" }, names.Take(3));
            Assert.Contains("source-sans-pro-semi-bold", names);

            Assert.True(catalog.TryGetDefaultFace(out var face));
            Assert.Equal("Roboto-Regular.ttf#0", Describe(face));
        }

        [Theory]
        [InlineData("aosp-api24-fonts.xml", 29, "Roboto-Regular.ttf", "Roboto-Medium.ttf")]
        [InlineData("aosp-api26-fonts.xml", 29, "Roboto-Regular.ttf", "Roboto-Medium.ttf")]
        [InlineData("aosp-api28-fonts.xml", 30, "Roboto-Regular.ttf", "Roboto-Medium.ttf")]
        [InlineData("aosp-api29-fonts.xml", 33, "Roboto-Regular.ttf", "Roboto-Medium.ttf")]
        [InlineData("aosp-api30-fonts.xml", 33, "Roboto-Regular.ttf", "Roboto-Medium.ttf")]
        [InlineData("aosp-api31-fonts.xml", 33, "RobotoStatic-Regular.ttf", "Roboto-Regular.ttf")]
        [InlineData("aosp-api33-fonts.xml", 33, "RobotoStatic-Regular.ttf", "Roboto-Regular.ttf")]
        [InlineData("aosp-api34-fonts.xml", 33, "Roboto-Regular.ttf", "Roboto-Regular.ttf")]
        [InlineData("aosp-api35-fonts.xml", 34, "Roboto-Regular.ttf", "Roboto-Regular.ttf")]
        [InlineData("aosp-api35-font_fallback.xml", 34, "Roboto-Regular.ttf", "Roboto-Regular.ttf")]
        [InlineData("aosp-api36-fonts.xml", 34, "Roboto-Regular.ttf", "Roboto-Regular.ttf")]
        public void Aosp_Configurations_Of_Every_Release_Form_A_Catalog(string fileName, int listedNames,
            string defaultFile, string mediumFile)
        {
            var catalog = AndroidFontCatalog.Create(_fixture.AospConfiguration(fileName));
            var names = catalog.GetFamilyNames();

            // Named families and the aliases of them.
            Assert.Equal(listedNames, names.Count);
            Assert.Equal(new[] { "sans-serif", "sans-serif-thin", "sans-serif-light" }, names.Take(3));
            Assert.Contains("monospace", names);
            Assert.Contains("serif", names);

            Assert.True(catalog.TryGetDefaultFace(out var face));
            Assert.Equal(defaultFile + "#0", Describe(face));
            Assert.Equal(mediumFile + "#0", Describe(MatchFamily(catalog, "sans-serif-medium")));
        }

        [Fact]
        public void Languages_Separated_By_Spaces_Steer_Han_Fallback()
        {
            // Android 9 lists the languages of a family separated by a space.
            var catalog = FromXml(
                "<family name=\"sans-serif\"><font>NotoMono-Regular.ttf</font></family>" +
                "<family lang=\"zh-Hans\"><font index=\"2\">CJK.ttc</font></family>" +
                "<family lang=\"zh-Hant zh-Bopo\"><font index=\"3\">CJK.ttc</font></family>");

            Assert.Equal("CJK.ttc#3", Describe(MatchCharacter(catalog, 0x4E2D, culture: "zh-TW")));
            Assert.Equal("CJK.ttc#2", Describe(MatchCharacter(catalog, 0x4E2D, culture: "zh-CN")));
        }

        [Fact]
        public void Entries_Of_One_Variable_Face_Report_Its_Default_Instance()
        {
            var catalog = Vendor();

            // Every sans-serif entry is an instance of one face; the collection simulates the rest.
            var bold = MatchFamily(catalog, "sans-serif", weight: FontWeight.Bold);
            var italic = MatchFamily(catalog, "sans-serif", FontStyle.Italic);

            Assert.Equal("Inter-Regular.ttf#0", Describe(bold));
            Assert.Equal(FontWeight.Normal, bold.Weight);
            Assert.Equal("Inter-Regular.ttf#0", Describe(italic));
            Assert.Equal(FontStyle.Normal, italic.Style);

            Assert.True(catalog.TryGetFamilyFaces("sans-serif", out var faces));
            Assert.Equal("Inter-Regular.ttf#0", Describe(Assert.Single(faces)));
        }

        [Fact]
        public void Family_Faces_Are_The_Distinct_Faces_Of_The_Family()
        {
            var catalog = Vendor();

            Assert.True(catalog.TryGetFamilyFaces("serif", out var faces));
            Assert.Equal(new[] { "NotoSans-Italic.ttf#0", "Inter-Bold.ttf#0" }, faces.Select(Describe));
            Assert.All(faces, x => Assert.Equal("serif", x.FamilyName));

            Assert.True(catalog.TryGetFamilyFaces("serif-bold", out var aliasFaces));
            Assert.Equal("Inter-Bold.ttf#0", Describe(Assert.Single(aliasFaces)));

            Assert.False(catalog.TryGetFamilyFaces("unknown", out _));
        }

        [Fact]
        public void Real_Family_Names_From_The_Name_Table_Resolve()
        {
            var catalog = Vendor();

            var inter = MatchFamily(catalog, "Inter");

            Assert.Equal("Inter", inter.FamilyName);
            Assert.Equal("Inter-Regular.ttf#0", Describe(inter));
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchFamily(catalog, "inter", weight: FontWeight.Bold)));

            Assert.Equal("CJK.ttc#0", Describe(MatchFamily(catalog, "Noto Sans JP")));
            Assert.Equal("CJK.ttc#2", Describe(MatchFamily(catalog, "Noto Sans SC")));
            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchFamily(catalog, "Noto Sans Hebrew")));

            Assert.True(catalog.TryGetFamilyFaces("Noto Sans JP", out var faces));
            Assert.Equal(new[] { "CJK.ttc#0", "CJK.ttc#1" }, faces.Select(Describe));

            Assert.False(catalog.TryMatchFamily("Segoe UI", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Fact]
        public void Real_Family_Names_Prefer_Faces_Of_Families_In_Use()
        {
            // Both emoji faces carry the same name; the first belongs to a family marked ignored
            // (a legacy font kept on the device but no longer used).
            var catalog = Vendor();

            Assert.Equal("EmojiPair.ttc#1", Describe(MatchFamily(catalog, "Twitter Color Emoji")));

            Assert.True(catalog.TryGetFamilyFaces("Twitter Color Emoji", out var faces));
            Assert.Equal(new[] { "EmojiPair.ttc#1", "EmojiPair.ttc#0" }, faces.Select(Describe));
        }

        [Fact]
        public void Real_Family_Names_Are_Not_Listed()
        {
            Assert.DoesNotContain("Inter", Vendor().GetFamilyNames());
        }

        [Fact]
        public void Families_Without_Existing_Files_Do_Not_Resolve()
        {
            Assert.False(Vendor().TryMatchFamily("vendor-missing", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));
        }

        [Fact]
        public void Additions_Contribute_Only_Families_And_Aliases_The_Main_File_Lacks()
        {
            var catalog = Vendor(withAdditions: true);

            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchFamily(catalog, "vendor-extra")));
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchFamily(catalog, "vendor-extra-bold")));
            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchFamily(catalog, "sans-serif")));
            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchFamily(catalog, "arial")));

            var product = MatchFamily(catalog, "product-sans-alias");

            Assert.Equal(Path.Combine(_fixture.ProductDirectory, "ProductSans-Regular.ttf"), product.FilePath);
            Assert.Equal("product-sans", product.FamilyName);

            var names = catalog.GetFamilyNames();

            Assert.Equal(new[] { "vendor-extra", "vendor-extra-bold", "product-sans", "product-sans-alias" }, names.Skip(8));

            // Fallback families come from the main file only.
            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0)));
        }

        [Theory]
        [InlineData("ja-JP", "CJK.ttc#0")]
        [InlineData("ko-KR", "CJK.ttc#1")]
        [InlineData("zh-CN", "CJK.ttc#2")]
        [InlineData("zh-TW", "CJK.ttc#3")]
        [InlineData("zh-Hant-HK", "CJK.ttc#3")]
        [InlineData("", "CJK.ttc#2")]
        public void Han_Fallback_Follows_The_Culture(string culture, string expected)
        {
            Assert.Equal(expected, Describe(MatchCharacter(Vendor(), 0x4E2D, culture: culture)));
        }

        [Fact]
        public void Fallback_Skips_Faces_That_Do_Not_Cover_The_Codepoint()
        {
            // Hiragana: the zh-Hans face lacks it; the zh-Hant face lacks it too, the Japanese face has it.
            Assert.Equal("CJK.ttc#0", Describe(MatchCharacter(Vendor(), 0x3042, culture: "zh-CN")));
        }

        [Fact]
        public void Compact_Families_Are_Preferred_Over_Elegant_Ones()
        {
            Assert.Equal("ArabicTriple.ttc#1", Describe(MatchCharacter(Vendor(), 0x0628)));
        }

        [Fact]
        public void Serif_Requests_Use_The_Serif_Substitutes_Of_Fallback_Families()
        {
            var catalog = Vendor();

            Assert.Equal("ArabicTriple.ttc#2", Describe(MatchCharacter(catalog, 0x0628, "serif")));
            Assert.Equal("ArabicTriple.ttc#2", Describe(MatchCharacter(catalog, 0x0628, "Times New Roman")));
            Assert.Equal("ArabicTriple.ttc#1", Describe(MatchCharacter(catalog, 0x0628, "monospace")));
        }

        [Fact]
        public void Emoji_Resolve_To_The_First_Emoji_Family_Not_Ignored()
        {
            var catalog = Vendor();

            Assert.Equal("EmojiPair.ttc#1", Describe(MatchCharacter(catalog, 0x1F600)));
            Assert.Equal("EmojiPair.ttc#1", Describe(MatchCharacter(catalog, 0x1F1E9, culture: "ja-JP")));
        }

        private AndroidFontCatalog FromXml(string familySet)
        {
            var path = Path.Combine(_fixture.EtcDirectory, System.Guid.NewGuid().ToString("N") + ".xml");

            File.WriteAllText(path, "<familyset version=\"23\">" + familySet + "</familyset>");

            return AndroidFontCatalog.Create(new AndroidFontConfiguration(
                new[] { new AndroidFontConfigurationFile(path, _fixture.FontDirectory) },
                System.Array.Empty<AndroidFontConfigurationFile>()));
        }

        [Fact]
        public void Text_Presentation_Symbols_Follow_File_Order()
        {
            var catalog = FromXml(
                "<family name=\"sans-serif\"><font>NotoMono-Regular.ttf</font></family>" +
                "<family><font>Inter-Bold.ttf</font></family>" +
                "<family lang=\"und-Zsye\"><font index=\"1\">EmojiPair.ttc</font></family>");

            // The heart defaults to text presentation; a text font listed first draws it.
            Assert.Equal("Inter-Bold.ttf#0", Describe(MatchCharacter(catalog, 0x2764)));

            // Emoji presentation codepoints prefer the emoji family wherever it is listed.
            Assert.Equal("EmojiPair.ttc#1", Describe(MatchCharacter(catalog, 0x1F600)));
            Assert.Equal("EmojiPair.ttc#1", Describe(MatchCharacter(catalog, 0x1F1E9)));
        }

        [Fact]
        public void Families_Declared_For_Script_Variants_Are_Not_Skipped()
        {
            // Syre (Estrangela) is a variant subtag of the Syriac script, not a script of its own;
            // a Hebrew font stands in for the Syriac one.
            var catalog = FromXml(
                "<family name=\"sans-serif\"><font>NotoMono-Regular.ttf</font></family>" +
                "<family lang=\"und-Syre\"><font>NotoSansHebrew-Regular.ttf</font></family>");

            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0)));
        }

        [Fact]
        public void A_Requested_Family_That_Covers_The_Codepoint_Wins()
        {
            var catalog = Vendor();

            Assert.Equal("NotoMono-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a', "monospace")));
            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a')));
            Assert.Equal("Inter-Regular.ttf#0", Describe(MatchCharacter(catalog, 'a', "unknown family")));
            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x05D0, "Noto Sans Hebrew")));
        }

        [Fact]
        public void Script_Fallback_Families_Resolve_By_Coverage()
        {
            var catalog = Vendor();

            var hebrew = MatchCharacter(catalog, 0x05D0);

            Assert.Equal("NotoSansHebrew-Regular.ttf#0", Describe(hebrew));
            Assert.Equal("Noto Sans Hebrew", hebrew!.FamilyName);
            Assert.Equal("NotoSansTamil-Regular.ttf#0", Describe(MatchCharacter(catalog, 0x0B95)));
        }

        [Fact]
        public void Codepoints_Nothing_Covers_Do_Not_Match()
        {
            var catalog = Vendor();

            Assert.Null(MatchCharacter(catalog, 0x20000));

            // The only Bengali family names a file that does not exist.
            Assert.Null(MatchCharacter(catalog, 0x0995));
            Assert.Null(MatchCharacter(catalog, 0));
        }
    }

    public sealed class AndroidFontFixtureHolder : System.IDisposable
    {
        internal AndroidFontFixture Fixture { get; } = new();

        public void Dispose() => Fixture.Dispose();
    }
}
