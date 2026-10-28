using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Character fallback picks a face for the writing system the text asks for. A loaded face that
    /// maps the codepoint but declares another writing system (a Korean face for Japanese Han) does
    /// not answer the request while the platform can still place it.
    /// </summary>
    public class FontFallbackScriptMatchTests
    {
        private const int Han = 0x4E2D;
        private const int Hiragana = 0x3042;
        private const int Katakana = 0x30A2;
        private const int Hangul = 0xAC00;

        private const string NiscAsset = "resm:Avalonia.Base.UnitTests.Assets.NISC18030.ttf?assembly=Avalonia.Base.UnitTests";

        // Fixture base for kana and Hangul, which the GB18030 font does not map.
        private const string BlankAsset = "resm:Avalonia.Base.UnitTests.Assets.AdobeBlank2VF.ttf?assembly=Avalonia.Base.UnitTests";

        // ulCodePageRange1 bits 17 (JIS/Japan), 18 (Chinese Simplified) and 19 (Korean Wansung).
        private const uint JapaneseJisCodePage = 1u << 17;
        private const uint ChineseSimplifiedCodePage = 1u << 18;
        private const uint KoreanWansungCodePage = 1u << 19;

        // A Simplified Chinese design that lists Japanese among its supported languages, as the
        // PingFang SC face in macOS's PingFang.ttc does.
        private const string SimplifiedWideDesign = "zh-Hans";
        private const string SimplifiedWideSupport = "en,de,ru,uk,ja,zh,zh-Hans,yue";

        // A Simplified Chinese design that lists Korean among its supported languages.
        private const string SimplifiedKoreanSupport = "en,ja,ko,zh,zh-Hans";

        [Fact]
        public void Japanese_Han_Should_Not_Take_A_Loaded_Face_That_Only_Supports_Japanese()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Simplified Wide Test", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Japanese_Han_Should_Not_Take_A_Loaded_Face_That_Only_Claims_The_Jis_Code_Page()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Jis Test", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Theory]
        [InlineData("Japanese Test")]
        [InlineData("Jpan Script Test")]
        public void Japanese_Han_Should_Take_A_Loaded_Face_Designed_For_Japanese(string familyName)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface(familyName, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal(familyName, Match(collection, "ja-JP"));
            Assert.Equal(0, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void Japanese_Han_Should_Not_Depend_On_An_Earlier_Simplified_Chinese_Lookup()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["zh-CN"] = "Simplified Wide Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Simplified Wide Test", Match(collection, "zh-CN"));
            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Korean_Han_Should_Not_Take_A_Loaded_Japanese_Face_That_Supports_Korean()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Japanese Wide Test", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
        }

        [Theory]
        [InlineData("ja-JP", "Mismatch")]
        [InlineData("zh-CN", "Match")]
        [InlineData("zh-TW", "Mismatch")]
        public void A_Face_Should_Match_Han_Only_In_Its_Design_Language(string culture, string expected)
        {
            using var app = UnitTestApplication.Start();

            var face = CreateGlyphTypeface(CreateFont("Simplified Wide Test", SimplifiedWideDesign,
                SimplifiedWideSupport, ChineseSimplifiedCodePage));

            Assert.Equal(expected,
                FontFallbackScriptHints.MatchWritingSystem(face, new Codepoint((uint)Han), CultureInfo.GetCultureInfo(culture)).ToString());
        }

        [Theory]
        [InlineData("ja", "ja-JP", "Match")]
        [InlineData("Jpan", "ja-JP", "Match")]
        [InlineData("zh-Hant", "zh-TW", "Match")]
        [InlineData("zh-HK", "zh-TW", "Match")]
        [InlineData("zh-Hans", "zh-TW", "Mismatch")]
        [InlineData("ko", "ja-JP", "Mismatch")]
        [InlineData("zh,zh-Hans", "zh-CN", "Match")]
        [InlineData("zh,zh-Hans", "zh-TW", "Mismatch")]
        [InlineData("zh", "zh-CN", "Match")]
        [InlineData("zh", "zh-TW", "Mismatch")]
        [InlineData("Hani", "ja-JP", "Mismatch")]
        public void A_Design_Language_Should_Name_Its_Han_Writing_System_In_Either_Tag_Form(string designLanguage,
            string culture, string expected)
        {
            using var app = UnitTestApplication.Start();

            var face = CreateGlyphTypeface(CreateFont("Design Test", designLanguage));

            Assert.Equal(expected,
                FontFallbackScriptHints.MatchWritingSystem(face, new Codepoint((uint)Han), CultureInfo.GetCultureInfo(culture)).ToString());
        }

        [Fact]
        public void A_Face_With_Only_The_Jis_Code_Page_Should_Not_Match_Japanese_Han()
        {
            using var app = UnitTestApplication.Start();

            var face = CreateGlyphTypeface(CreateFont("Jis Test", null, null, JapaneseJisCodePage));

            Assert.Equal(WritingSystemMatch.Mismatch,
                FontFallbackScriptHints.MatchWritingSystem(face, new Codepoint((uint)Han), CultureInfo.GetCultureInfo("ja-JP")));
        }

        [Fact]
        public void Japanese_Han_Should_Not_Take_A_Loaded_Korean_Face()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Traditional_Han_Should_Not_Take_A_Loaded_Simplified_Face()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Simplified Test", Match(collection, "zh-Hans"));
            Assert.Equal("Traditional Test", Match(collection, "zh-Hant"));
        }

        [Fact]
        public void A_Loaded_Face_Declaring_The_Requested_Writing_System_Should_Answer_Without_The_Platform()
        {
            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));

            var calls = provider.TryMatchCharacterCount;

            Assert.Equal("Japanese Test", Match(collection, "ja-JP", 0x5B57));
            Assert.Equal(calls, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void Japanese_Han_Should_Not_Take_A_Loaded_Face_Declaring_No_Writing_System()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Plain Test", Match(collection, "en-US"));
            Assert.Equal("Japanese Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void A_Loaded_Face_Declaring_No_Writing_System_Should_Answer_When_The_Platform_Has_None()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["ja-JP"] = null } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Plain Test", Match(collection, "en-US"));
            Assert.Equal("Plain Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void A_Face_Declaring_Another_Writing_System_Should_Answer_When_The_Platform_Has_None()
        {
            var provider = new LanguageFontProvider { Languages = { ["ja-JP"] = null } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Korean Test", Match(collection, "ja-JP"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Not_Take_A_Loaded_Face()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.True(collection.TryGetGlyphTypeface("Simplified Test", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

            Assert.Equal("Japanese Test", Match(collection, "de-DE"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Not_Depend_On_An_Earlier_Lookup()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Korean Test", Match(collection, "ko-KR"));
            Assert.Equal("Japanese Test", Match(collection, "de-DE"));
        }

        [Fact]
        public void Han_Without_A_Writing_System_Should_Ask_The_Platform_Once_Per_Bucket()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["de-DE"] = "Japanese Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Assert.Equal("Japanese Test", Match(collection, "de-DE"));

            var calls = provider.TryMatchCharacterCount;

            Assert.Equal("Japanese Test", Match(collection, "de-DE", 0x5B57));
            Assert.Equal(calls, provider.TryMatchCharacterCount);
        }

        [Theory]
        [InlineData(Hiragana)]
        [InlineData(Katakana)]
        public void Japanese_Kana_Should_Not_Take_A_Loaded_Face_That_Only_Supports_Japanese(int codepoint)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["ja-JP"] = "Japanese Kana Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Simplified Wide Kana Test");

            Assert.Equal("Japanese Kana Test", Match(collection, "ja-JP", codepoint));
        }

        [Theory]
        [InlineData("zh-CN")]
        [InlineData("zh-TW")]
        [InlineData("zh-HK")]
        [InlineData("de-DE")]
        public void Kana_In_Another_Culture_Should_Not_Take_A_Loaded_Face_That_Only_Supports_Japanese(string culture)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { [culture] = "Traditional Kana Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Simplified Wide Kana Test");

            Assert.Equal("Traditional Kana Test", Match(collection, culture, Hiragana));
            Assert.Equal("Traditional Kana Test", Match(collection, culture, Katakana));
        }

        [Theory]
        [InlineData(Hiragana, "ja-JP", "Japanese Kana Test")]
        [InlineData(Katakana, "ja-JP", "Japanese Kana Test")]
        [InlineData(Hangul, "ko-KR", "Korean Hangul Test")]
        public void A_Face_Designed_For_Unspecified_Han_Should_Not_Answer_Kana_Or_Hangul(int codepoint, string culture,
            string platformFamily)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { [culture] = platformFamily } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Hani Blank Test");

            Assert.Equal(platformFamily, Match(collection, culture, codepoint));
        }

        [Theory]
        [InlineData(Hiragana, "ja-JP")]
        [InlineData(Katakana, "ja-JP")]
        [InlineData(Hiragana, "zh-TW")]
        [InlineData(Katakana, "de-DE")]
        public void Kana_Should_Take_A_Loaded_Face_Designed_For_Japanese(int codepoint, string culture)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Japanese Kana Test");

            Assert.Equal("Japanese Kana Test", Match(collection, culture, codepoint));
            Assert.Equal(0, provider.TryMatchCharacterCount);
        }

        [Theory]
        [InlineData("ko-KR")]
        [InlineData("en-US")]
        public void Hangul_Should_Take_A_Loaded_Face_Designed_For_Korean(string culture)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Korean Hangul Test");

            Assert.Equal("Korean Hangul Test", Match(collection, culture, Hangul));
            Assert.Equal(0, provider.TryMatchCharacterCount);
        }

        [Fact]
        public void Hangul_Should_Not_Take_A_Loaded_Face_That_Only_Supports_Korean()
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { ["ko-KR"] = "Korean Hangul Test" } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Simplified Korean Support Test");

            Assert.Equal("Korean Hangul Test", Match(collection, "ko-KR", Hangul));
        }

        [Theory]
        [InlineData("Plain Blank Test", Hiragana, "ja-JP", "Japanese Kana Test")]
        [InlineData("Plain Blank Test", Katakana, "ja-JP", "Japanese Kana Test")]
        [InlineData("Plain Blank Test", Hangul, "ko-KR", "Korean Hangul Test")]
        [InlineData("Jis Blank Test", Hiragana, "ja-JP", "Japanese Kana Test")]
        public void Kana_And_Hangul_Should_Not_Take_A_Loaded_Face_Declaring_No_Writing_System(string loadedFamily,
            int codepoint, string culture, string platformFamily)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { [culture] = platformFamily } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, loadedFamily);

            Assert.Equal(platformFamily, Match(collection, culture, codepoint));
        }

        [Theory]
        [InlineData(Hiragana, "ja-JP")]
        [InlineData(Katakana, "ja-JP")]
        [InlineData(Hangul, "ko-KR")]
        public void Kana_And_Hangul_Should_Take_A_Loaded_Face_Declaring_No_Writing_System_When_The_Platform_Has_None(
            int codepoint, string culture)
        {
            using var app = UnitTestApplication.Start();

            var provider = new LanguageFontProvider { Languages = { [culture] = null } };
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Load(collection, "Plain Blank Test");

            Assert.Equal("Plain Blank Test", Match(collection, culture, codepoint));
            Assert.Equal("Plain Blank Test", Match(collection, culture, codepoint));
        }

        [Theory]
        [InlineData("ja", null, Hiragana, "Match")]
        [InlineData("ja", null, Katakana, "Match")]
        [InlineData("Jpan", null, Katakana, "Match")]
        [InlineData("Hira", null, Hiragana, "Match")]
        [InlineData("Hira", null, Katakana, "Mismatch")]
        [InlineData("Kana", null, Katakana, "Match")]
        [InlineData("Kana", null, Hiragana, "Mismatch")]
        [InlineData("Hrkt", null, Hiragana, "Match")]
        [InlineData("Hrkt", null, Katakana, "Match")]
        [InlineData("Hani", null, Hiragana, "Mismatch")]
        [InlineData("Hani", "Hani", Katakana, "Mismatch")]
        [InlineData("ko", null, Hiragana, "Mismatch")]
        [InlineData(SimplifiedWideDesign, SimplifiedWideSupport, Hiragana, "Mismatch")]
        [InlineData(SimplifiedWideDesign, SimplifiedWideSupport, Katakana, "Mismatch")]
        [InlineData("ko", null, Hangul, "Match")]
        [InlineData("Kore", null, Hangul, "Match")]
        [InlineData("Hang", null, Hangul, "Match")]
        [InlineData("Hani", null, Hangul, "Mismatch")]
        [InlineData("ja", null, Hangul, "Mismatch")]
        [InlineData(SimplifiedWideDesign, SimplifiedKoreanSupport, Hangul, "Mismatch")]
        [InlineData(null, null, Hiragana, "Mismatch")]
        [InlineData(null, null, Hangul, "Mismatch")]
        public void A_Face_Should_Match_Kana_And_Hangul_Only_In_Its_Design_Language(string? designLanguages,
            string? supportedLanguages, int codepoint, string expected)
        {
            using var app = UnitTestApplication.Start();

            var face = CreateGlyphTypeface(CreateBlankFont("Design Test", designLanguages, supportedLanguages));

            Assert.Equal(expected,
                FontFallbackScriptHints.MatchWritingSystem(face, new Codepoint((uint)codepoint), CultureInfo.GetCultureInfo("ja-JP")).ToString());
        }

        [Theory]
        [InlineData(Hiragana)]
        [InlineData(Hangul)]
        public void A_Face_With_Only_Cjk_Code_Pages_Should_Not_Match_Kana_Or_Hangul(int codepoint)
        {
            using var app = UnitTestApplication.Start();

            var face = CreateGlyphTypeface(CreateBlankFont("Code Page Test", null, null,
                JapaneseJisCodePage | KoreanWansungCodePage));

            Assert.Equal(WritingSystemMatch.Mismatch,
                FontFallbackScriptHints.MatchWritingSystem(face, new Codepoint((uint)codepoint), CultureInfo.GetCultureInfo("ja-JP")));
        }

        private static void Load(SystemFontCollection collection, string familyName)
            => Assert.True(collection.TryGetGlyphTypeface(familyName, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _));

        private static string Match(SystemFontCollection collection, string culture, int codepoint = Han)
        {
            Assert.True(collection.TryMatchCharacter(codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, CultureInfo.GetCultureInfo(culture), out var match), culture);

            return match.FontFamily.Name;
        }

        /// <summary>
        /// A CJK font (the GB18030 test font) named <paramref name="familyName"/> that declares
        /// <paramref name="designLanguages"/> (<c>dlng</c>) and <paramref name="supportedLanguages"/>
        /// (<c>slng</c>) in its meta table, and <paramref name="codePages"/> as its OS/2
        /// ulCodePageRange1. Without either language list the font has no meta table.
        /// </summary>
        private static byte[] CreateFont(string familyName, string? designLanguages,
            string? supportedLanguages = null, uint codePages = 0)
            => CreateFont(SyntheticFont.FromAsset(NiscAsset), familyName, designLanguages, supportedLanguages, codePages);

        /// <summary>
        /// As <see cref="CreateFont(string, string?, string?, uint)"/>, for kana and Hangul: a static
        /// copy of the blank font whose cmap maps only <see cref="Hiragana"/>, <see cref="Katakana"/>
        /// and <see cref="Hangul"/>. The blank font's own format 13 cmap would make it a last-resort
        /// face, which the fallback sweeps skip.
        /// </summary>
        private static byte[] CreateBlankFont(string familyName, string? designLanguages,
            string? supportedLanguages = null, uint codePages = 0)
        {
            var font = SyntheticFont.FromAsset(BlankAsset)
                .Remove("fvar")
                .Remove("gvar")
                .Remove("HVAR")
                .Remove("VVAR")
                .Remove("STAT")
                .Replace("cmap", BuildCmapTable(Hiragana, Katakana, Hangul));

            return CreateFont(font, familyName, designLanguages, supportedLanguages, codePages);
        }

        /// <summary>A Windows Unicode full-repertoire format 12 cmap mapping each codepoint to glyph 1.</summary>
        private static byte[] BuildCmapTable(params int[] codepoints)
        {
            const int subtableOffset = 4 + 8;
            var subtableLength = 16 + 12 * codepoints.Length;
            var bytes = new byte[subtableOffset + subtableLength];

            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 3);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 10);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), subtableOffset);

            var subtable = bytes.AsSpan(subtableOffset);

            BinaryPrimitives.WriteUInt16BigEndian(subtable, 12);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.Slice(4), (uint)subtableLength);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.Slice(12), (uint)codepoints.Length);

            var sorted = (int[])codepoints.Clone();
            Array.Sort(sorted);

            for (var i = 0; i < sorted.Length; i++)
            {
                var group = subtable.Slice(16 + 12 * i);

                BinaryPrimitives.WriteUInt32BigEndian(group, (uint)sorted[i]);
                BinaryPrimitives.WriteUInt32BigEndian(group.Slice(4), (uint)sorted[i]);
                BinaryPrimitives.WriteUInt32BigEndian(group.Slice(8), 1);
            }

            return bytes;
        }

        private static byte[] CreateFont(SyntheticFont font, string familyName, string? designLanguages,
            string? supportedLanguages, uint codePages)
        {
            font = font
                .Replace("name", BuildNameTable(familyName))
                .PatchUInt32("OS/2", 78, codePages)
                .PatchUInt32("OS/2", 82, 0);

            font = designLanguages is null && supportedLanguages is null
                ? font.Remove("meta")
                : font.Replace("meta", BuildMetaTable(designLanguages, supportedLanguages));

            return font.ToBytes();
        }

        private static GlyphTypeface CreateGlyphTypeface(byte[] font)
        {
            Assert.True(SfntFace.TryLoad(new MemoryStream(font), out var face));

            return new GlyphTypeface(face);
        }

        private static byte[] BuildNameTable(string familyName)
        {
            var family = Encoding.BigEndianUnicode.GetBytes(familyName);
            var subfamily = Encoding.BigEndianUnicode.GetBytes("Regular");
            var bytes = new byte[6 + 2 * 12 + family.Length + subfamily.Length];

            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 2);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 6 + 2 * 12);

            WriteRecord(6, 1, family.Length, 0);
            WriteRecord(18, 2, subfamily.Length, family.Length);

            family.CopyTo(bytes, 30);
            subfamily.CopyTo(bytes, 30 + family.Length);

            return bytes;

            void WriteRecord(int offset, int nameId, int length, int stringOffset)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), 3);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4), 0x409);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 6), (ushort)nameId);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 8), (ushort)length);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 10), (ushort)stringOffset);
            }
        }

        private static byte[] BuildMetaTable(string? designLanguages, string? supportedLanguages)
        {
            var maps = new List<(string Tag, byte[] Payload)>();

            if (designLanguages is not null)
            {
                maps.Add(("dlng", Encoding.UTF8.GetBytes(designLanguages)));
            }

            if (supportedLanguages is not null)
            {
                maps.Add(("slng", Encoding.UTF8.GetBytes(supportedLanguages)));
            }

            var dataOffset = 16 + 12 * maps.Count;
            var length = dataOffset;

            foreach (var map in maps)
            {
                length += map.Payload.Length;
            }

            var bytes = new byte[length];

            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)maps.Count);

            for (var i = 0; i < maps.Count; i++)
            {
                var record = 16 + 12 * i;

                Encoding.ASCII.GetBytes(maps[i].Tag).CopyTo(bytes, record);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record + 4), (uint)dataOffset);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record + 8), (uint)maps[i].Payload.Length);
                maps[i].Payload.CopyTo(bytes, dataOffset);

                dataOffset += maps[i].Payload.Length;
            }

            return bytes;
        }

        /// <summary>
        /// A platform that places Han text by language, as CoreText and DirectWrite do: each
        /// culture gets the face designed for its writing system; null means it has none.
        /// </summary>
        private sealed class LanguageFontProvider : ISystemFontProvider
        {
            private static readonly Dictionary<string, byte[]> s_fonts = new()
            {
                ["Korean Test"] = CreateFont("Korean Test", "ko"),
                ["Japanese Test"] = CreateFont("Japanese Test", "ja"),
                ["Simplified Test"] = CreateFont("Simplified Test", "zh-Hans"),
                ["Traditional Test"] = CreateFont("Traditional Test", "zh-Hant"),
                ["Plain Test"] = CreateFont("Plain Test", null),
                ["Simplified Wide Test"] = CreateFont("Simplified Wide Test", SimplifiedWideDesign,
                    SimplifiedWideSupport, ChineseSimplifiedCodePage),
                ["Jis Test"] = CreateFont("Jis Test", null, null, JapaneseJisCodePage),
                ["Jpan Script Test"] = CreateFont("Jpan Script Test", "Jpan"),
                ["Japanese Wide Test"] = CreateFont("Japanese Wide Test", "ja", "ja,ko"),
                ["Japanese Kana Test"] = CreateBlankFont("Japanese Kana Test", "ja", "ja", JapaneseJisCodePage),
                ["Traditional Kana Test"] = CreateBlankFont("Traditional Kana Test", "zh-Hant"),
                ["Korean Hangul Test"] = CreateBlankFont("Korean Hangul Test", "ko", "ko", KoreanWansungCodePage),
                ["Simplified Wide Kana Test"] = CreateBlankFont("Simplified Wide Kana Test", SimplifiedWideDesign,
                    SimplifiedWideSupport, ChineseSimplifiedCodePage),
                ["Simplified Korean Support Test"] = CreateBlankFont("Simplified Korean Support Test",
                    SimplifiedWideDesign, SimplifiedKoreanSupport, ChineseSimplifiedCodePage),
                ["Hani Blank Test"] = CreateBlankFont("Hani Blank Test", "Hani", "Hani"),
                ["Plain Blank Test"] = CreateBlankFont("Plain Blank Test", null),
                ["Jis Blank Test"] = CreateBlankFont("Jis Blank Test", null, null, JapaneseJisCodePage),
            };

            public Dictionary<string, string?> Languages { get; } = new()
            {
                ["ko-KR"] = "Korean Test",
                ["ja-JP"] = "Japanese Test",
                ["zh-Hans"] = "Simplified Test",
                ["zh-Hant"] = "Traditional Test",
                ["zh-TW"] = "Traditional Test",
                ["en-US"] = "Plain Test",
            };

            public int TryMatchCharacterCount { get; private set; }

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = Face("Plain Test");
                return true;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => new List<string>(s_fonts.Keys);

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = s_fonts.ContainsKey(familyName) ? Face(familyName) : null;
                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                TryMatchCharacterCount++;

                match = culture is not null && Languages.TryGetValue(culture.Name, out var family) && family is not null
                    ? Face(family)
                    : null;

                return match != null;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = s_fonts.ContainsKey(familyName) ? new[] { Face(familyName) } : null;
                return faces != null;
            }

            public void Dispose()
            {
            }

            private static SystemFontFace Face(string familyName) => new BytesFontFace(s_fonts[familyName], familyName);
        }

        private sealed class BytesFontFace : SystemFontFace
        {
            private readonly byte[] _data;

            public BytesFontFace(byte[] data, string familyName)
                : base(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)
            {
                _data = data;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                if (!SfntFace.TryLoad(new MemoryStream(_data), out var face))
                {
                    return false;
                }

                fontMemory = face;
                return true;
            }
        }
    }
}
