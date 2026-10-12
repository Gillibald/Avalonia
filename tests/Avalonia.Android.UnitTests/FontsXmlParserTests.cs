using System;
using System.IO;
using System.Linq;
using Avalonia.Android.Platform.Fonts;
using Xunit;

namespace Avalonia.Android.UnitTests
{
    public class FontsXmlParserTests
    {
        private const string FontDirectory = "/system/fonts";

        private static FontsXmlConfig ParseFixture(string fileName, string fontDirectory = FontDirectory)
        {
            Assert.True(FontsXmlParser.TryParseFile(Fixtures.GetPath(fileName), fontDirectory, out var config));

            return config;
        }

        private static FontsXmlConfig ParseText(string text)
        {
            using var reader = new StringReader(text);

            return FontsXmlParser.Parse(reader, FontDirectory);
        }

        [Fact]
        public void Aosp_Configuration_Lists_Every_Family_And_Alias_In_File_Order()
        {
            var config = ParseFixture("aosp-api30-fonts.xml");

            Assert.Equal(148, config.Families.Count);
            Assert.Equal(24, config.Aliases.Count);
            Assert.Equal(277, config.Families.Sum(x => x.Fonts.Count));

            Assert.Equal(
                new[]
                {
                    "sans-serif", "sans-serif-condensed", "serif", "monospace", "serif-monospace", "casual",
                    "cursive", "sans-serif-smallcaps", "source-sans-pro",
                },
                config.Families.Where(x => x.Name != null).Select(x => x.Name));

            var sansSerif = config.Families[0];

            Assert.Equal(12, sansSerif.Fonts.Count);
            Assert.Equal("Roboto-Thin.ttf", sansSerif.Fonts[0].FileName);
            Assert.Equal(100, sansSerif.Fonts[0].Weight);
            Assert.False(sansSerif.Fonts[0].IsItalic);
            Assert.True(sansSerif.Fonts[1].IsItalic);
            Assert.Equal(Path.Combine(FontDirectory, "Roboto-Thin.ttf"), sansSerif.Fonts[0].Path);
        }

        [Theory]
        [InlineData("aosp-api24-fonts.xml", 85, 21, 141, 8, 0, "NotoColorEmoji.ttf", "zh-Hant")]
        [InlineData("aosp-api26-fonts.xml", 116, 21, 179, 8, 0, "NotoColorEmoji.ttf", "zh-Hant")]
        [InlineData("aosp-api28-fonts.xml", 141, 22, 245, 8, 0, "NotoColorEmoji.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api29-fonts.xml", 142, 24, 263, 9, 0, "NotoColorEmoji.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api30-fonts.xml", 148, 24, 277, 9, 0, "NotoColorEmoji.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api31-fonts.xml", 157, 24, 366, 9, 0, "NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api33-fonts.xml", 158, 24, 367, 9, 1, "NotoColorEmojiLegacy.ttf,NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api34-fonts.xml", 158, 24, 367, 9, 1, "NotoColorEmojiLegacy.ttf,NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api35-fonts.xml", 160, 24, 387, 10, 1, "NotoColorEmojiLegacy.ttf,NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api35-font_fallback.xml", 159, 24, 219, 10, 0, "NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        [InlineData("aosp-api36-fonts.xml", 160, 24, 419, 10, 1, "NotoColorEmojiLegacy.ttf,NotoColorEmoji.ttf,NotoColorEmojiFlags.ttf", "zh-Hant,zh-Bopo")]
        public void Aosp_Configurations_Of_Every_Release_Parse(string fileName, int families, int aliases, int fonts,
            int namedFamilies, int ignoredFamilies, string emojiFiles, string traditionalChineseLanguages)
        {
            var config = ParseFixture(fileName);

            Assert.Equal(families, config.Families.Count);
            Assert.Equal(aliases, config.Aliases.Count);
            Assert.Equal(fonts, config.Families.Sum(x => x.Fonts.Count));
            Assert.Equal(namedFamilies, config.Families.Count(x => x.Name != null));
            Assert.Equal(ignoredFamilies, config.Families.Count(x => x.Ignore));
            Assert.Equal("sans-serif", config.Families[0].Name);

            // Every alias targets a named family declared before it.
            Assert.All(config.Aliases, x => Assert.Contains(config.Families, f => f.Name == x.To));

            Assert.Equal(emojiFiles.Split(','),
                config.Families.Where(x => x.Languages.Contains("und-Zsye")).Select(x => x.Fonts[0].FileName));

            // Han fallback: the first family of each language is a face of the CJK collection.
            Assert.Equal(0, config.Families.First(x => x.Languages.SequenceEqual(new[] { "ja" })).Fonts[0].Index);
            Assert.Equal(1, config.Families.First(x => x.Languages.SequenceEqual(new[] { "ko" })).Fonts[0].Index);
            Assert.Equal(2, config.Families.First(x => x.Languages.SequenceEqual(new[] { "zh-Hans" })).Fonts[0].Index);
            Assert.Equal(traditionalChineseLanguages.Split(','),
                Assert.Single(config.Families, x => x.Languages.Contains("zh-Hant")).Languages);
        }

        [Fact]
        public void Language_Lists_Separated_By_Spaces_Are_Split()
        {
            // Android 9 separates the languages of a family with a space, later releases with a comma.
            var config = ParseText("<familyset><family lang=\"zh-Hant zh-Bopo\"><font>A.ttc</font></family>" +
                                   "<family lang=\" ja ,  ko \"><font>B.ttc</font></family></familyset>");

            Assert.Equal(new[] { "zh-Hant", "zh-Bopo" }, config.Families[0].Languages);
            Assert.Equal(new[] { "ja", "ko" }, config.Families[1].Languages);
        }

        [Fact]
        public void Aosp_Configuration_Reads_Alias_Targets_And_Weights()
        {
            var config = ParseFixture("aosp-api30-fonts.xml");

            var medium = Assert.Single(config.Aliases, x => x.Name == "sans-serif-medium");

            Assert.Equal("sans-serif", medium.To);
            Assert.Equal(500, medium.Weight);

            var arial = Assert.Single(config.Aliases, x => x.Name == "arial");

            Assert.Equal("sans-serif", arial.To);
            Assert.Null(arial.Weight);

            Assert.Contains(config.Aliases, x => x.Name == "times new roman" && x.To == "serif");
        }

        [Fact]
        public void Aosp_Configuration_Reads_Languages_Collection_Indices_And_Serif_Substitutes()
        {
            var config = ParseFixture("aosp-api30-fonts.xml");

            var japanese = Assert.Single(config.Families, x => x.Languages.SequenceEqual(new[] { "ja" }));

            Assert.Null(japanese.Name);
            Assert.Equal("NotoSansCJK-Regular.ttc", japanese.Fonts[0].FileName);
            Assert.Equal(0, japanese.Fonts[0].Index);
            Assert.Null(japanese.Fonts[0].FallbackFor);
            Assert.Equal("NotoSerifCJK-Regular.ttc", japanese.Fonts[1].FileName);
            Assert.Equal("serif", japanese.Fonts[1].FallbackFor);

            var traditional = Assert.Single(config.Families, x => x.Languages.Contains("zh-Hant"));

            Assert.Equal(new[] { "zh-Hant", "zh-Bopo" }, traditional.Languages);
            Assert.Equal(3, traditional.Fonts[0].Index);

            var korean = Assert.Single(config.Families, x => x.Languages.SequenceEqual(new[] { "ko" }));

            Assert.Equal(1, korean.Fonts[0].Index);

            var emoji = Assert.Single(config.Families, x => x.Languages.Contains("und-Zsye"));

            Assert.Equal("NotoColorEmoji.ttf", Assert.Single(emoji.Fonts).FileName);
            Assert.False(emoji.Ignore);
        }

        [Fact]
        public void Aosp_Configuration_Reads_Variants_And_Axes()
        {
            var config = ParseFixture("aosp-api30-fonts.xml");

            var arabic = config.Families.Where(x => x.Languages.Contains("und-Arab")).ToArray();

            Assert.Equal(2, arabic.Length);
            Assert.Equal(FontsXmlVariant.Elegant, arabic[0].Variant);
            Assert.Equal(FontsXmlVariant.Compact, arabic[1].Variant);
            Assert.Equal(FontsXmlVariant.Default, config.Families[0].Variant);

            var georgian = config.Families.Single(x => x.Languages.Contains("und-Geor"));

            Assert.Equal(new[] { "und-Geor", "und-Geok" }, georgian.Languages);
            Assert.Equal(new FontsXmlAxis("wght", 400), Assert.Single(georgian.Fonts[0].Axes));

            var khmer = config.Families.First(x => x.Languages.Contains("und-Khmr"));

            Assert.Equal(new[] { new FontsXmlAxis("wdth", 100), new FontsXmlAxis("wght", 26) }, khmer.Fonts[0].Axes);
            Assert.Equal("NotoSansKhmer-VF.ttf", khmer.Fonts[0].FileName);
        }

        [Fact]
        public void Vendor_Configuration_Trims_File_Names_And_Skips_Comments_And_Unknown_Markup()
        {
            var config = ParseFixture("vendor-fonts.xml", "/vendor/fonts");

            Assert.DoesNotContain(config.Families, x => x.Name == "vendor-flip");

            var vendorUi = Assert.Single(config.Families, x => x.Name == "vendor-ui");
            var font = Assert.Single(vendorUi.Fonts);

            Assert.Equal("Inter-Bold.ttf", font.FileName);
            Assert.Equal(Path.Combine("/vendor/fonts", "Inter-Bold.ttf"), font.Path);
            Assert.Equal("Inter-Bold", font.PostScriptName);

            var simplified = Assert.Single(config.Families, x => x.Languages.Contains("zh-Hans"));

            Assert.Equal("CJK.ttc", simplified.Fonts[0].FileName);
            Assert.Equal(2, simplified.Fonts[0].Index);

            var sansSerif = config.Families[0];

            Assert.Equal(3, sansSerif.Fonts.Count);
            Assert.All(sansSerif.Fonts, x => Assert.Equal("Inter-Regular.ttf", x.FileName));
            Assert.Equal(new[] { new FontsXmlAxis("ital", 1), new FontsXmlAxis("wght", 400) }, sansSerif.Fonts[2].Axes);
            Assert.True(sansSerif.Fonts[2].IsItalic);
        }

        [Fact]
        public void Vendor_Configuration_Reads_Ignored_Families()
        {
            var config = ParseFixture("vendor-fonts.xml");

            var emoji = config.Families.Where(x => x.Languages.Contains("und-Zsye")).ToArray();

            Assert.Equal(2, emoji.Length);
            Assert.True(emoji[0].Ignore);
            Assert.False(emoji[1].Ignore);
            Assert.Equal(1, emoji[1].Fonts[0].Index);
        }

        [Fact]
        public void Customization_Files_Read_Families_And_Aliases_Under_Their_Own_Root()
        {
            var config = ParseFixture("product-customization.xml", "/product/fonts");

            var family = Assert.Single(config.Families);

            Assert.Equal("product-sans", family.Name);
            Assert.Equal(Path.Combine("/product/fonts", "ProductSans-Regular.ttf"), Assert.Single(family.Fonts).Path);
            Assert.Equal("product-sans", Assert.Single(config.Aliases).To);
        }

        [Fact]
        public void Missing_Attributes_Take_The_Format_Defaults()
        {
            var config = ParseText(
                "<familyset><family><font>A.ttf</font><font weight=\"bold\" style=\"oblique\" index=\"x\">B.ttf</font></family></familyset>");

            var family = Assert.Single(config.Families);

            Assert.Null(family.Name);
            Assert.Empty(family.Languages);
            Assert.Equal(FontsXmlVariant.Default, family.Variant);
            Assert.False(family.Ignore);

            Assert.All(family.Fonts, x =>
            {
                Assert.Equal(400, x.Weight);
                Assert.False(x.IsItalic);
                Assert.Equal(0, x.Index);
                Assert.Null(x.PostScriptName);
                Assert.Null(x.FallbackFor);
                Assert.Empty(x.Axes);
            });
        }

        [Fact]
        public void Fonts_Without_A_File_Name_Are_Skipped()
        {
            var config = ParseText("<familyset><family name=\"a\"><font weight=\"400\">  </font><font>A.ttf</font></family></familyset>");

            Assert.Equal("A.ttf", Assert.Single(Assert.Single(config.Families).Fonts).FileName);
        }

        [Fact]
        public void Malformed_Configuration_Yields_An_Empty_Model()
        {
            var config = ParseText("<familyset><family name=\"a\"><font>A.ttf</font></family><family name=");

            Assert.Empty(config.Families);
            Assert.Empty(config.Aliases);
        }

        [Fact]
        public void Document_Type_Declarations_Are_Not_Processed()
        {
            var config = ParseText(
                "<!DOCTYPE familyset [<!ENTITY e \"A.ttf\">]><familyset><family name=\"a\"><font>B.ttf</font></family></familyset>");

            Assert.Equal("B.ttf", Assert.Single(Assert.Single(config.Families).Fonts).FileName);
        }

        [Fact]
        public void Unreadable_Files_Are_Reported()
        {
            Assert.False(FontsXmlParser.TryParseFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml"),
                FontDirectory, out _));
        }
    }
}
