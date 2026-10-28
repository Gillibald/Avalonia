using System;
using System.Globalization;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting.Unicode;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class FontFallbackScriptHintsTests
    {
        [Fact]
        public void RefineWithCulture_Returns_Han_For_Han_Without_Culture()
        {
            // U+4E2D 中 — Han script.
            var cp = new Codepoint(0x4E2D);

            var refined = FontFallbackScriptHints.RefineWithCulture(cp, culture: null);

            Assert.Equal(Script.Han, refined);
        }

        [Fact]
        public void RefineWithCulture_Han_With_Japanese_Culture_Maps_To_Hiragana()
        {
            var cp = new Codepoint(0x4E2D);

            var refined = FontFallbackScriptHints.RefineWithCulture(cp, CultureInfo.GetCultureInfo("ja-JP"));

            Assert.Equal(Script.Hiragana, refined);
        }

        [Fact]
        public void RefineWithCulture_Han_With_Korean_Culture_Maps_To_Hangul()
        {
            var cp = new Codepoint(0x4E2D);

            var refined = FontFallbackScriptHints.RefineWithCulture(cp, CultureInfo.GetCultureInfo("ko-KR"));

            Assert.Equal(Script.Hangul, refined);
        }

        [Fact]
        public void RefineWithCulture_Han_With_Chinese_Culture_Stays_Han()
        {
            var cp = new Codepoint(0x4E2D);

            var refinedSimplified = FontFallbackScriptHints.RefineWithCulture(cp, CultureInfo.GetCultureInfo("zh-CN"));
            var refinedTraditional = FontFallbackScriptHints.RefineWithCulture(cp, CultureInfo.GetCultureInfo("zh-TW"));

            Assert.Equal(Script.Han, refinedSimplified);
            Assert.Equal(Script.Han, refinedTraditional);
        }

        [Theory]
        // U+30FC (Script=Common) has Script_Extensions Hira Kana: kana in any culture.
        [InlineData(0x30FC, null, Script.Hiragana)]
        [InlineData(0x30FC, "ja-JP", Script.Hiragana)]
        [InlineData(0x30FC, "zh-TW", Script.Hiragana)]
        // U+3001, U+3002, U+300C (Script=Common) list Han among their Script_Extensions: the
        // culture's Han writing system.
        [InlineData(0x3001, "ja-JP", Script.Hiragana)]
        [InlineData(0x3002, "ko-KR", Script.Hangul)]
        [InlineData(0x300C, "zh-TW", Script.Han)]
        [InlineData(0x3001, "zh-CN", Script.Han)]
        [InlineData(0x3001, "en-US", Script.Han)]
        // U+3099 (Script=Inherited) has Script_Extensions Hira Kana.
        [InlineData(0x3099, "ja-JP", Script.Hiragana)]
        // No CJK Script_Extensions: unchanged.
        [InlineData(0x002C, "ja-JP", Script.Common)]
        [InlineData(0x002E, "ja-JP", Script.Common)]
        [InlineData(0xFF0C, "ja-JP", Script.Common)]
        public void RefineWithCulture_Common_Codepoint_With_Cjk_Script_Extensions_Takes_The_Cjk_Script(
            int codepoint, string? culture, Script expected)
        {
            var refined = FontFallbackScriptHints.RefineWithCulture(new Codepoint((uint)codepoint),
                culture is null ? null : CultureInfo.GetCultureInfo(culture));

            Assert.Equal(expected, refined);
        }

        [Fact]
        public void RefineWithCulture_Latin_Codepoint_Returns_Latin_Unchanged()
        {
            var cp = new Codepoint('A');

            var refined = FontFallbackScriptHints.RefineWithCulture(cp, CultureInfo.GetCultureInfo("en-US"));

            Assert.Equal(Script.Latin, refined);
        }

        [Fact]
        public void IsLocaleSensitive_True_For_Han()
        {
            Assert.True(FontFallbackScriptHints.IsLocaleSensitive(Script.Han));
        }

        [Fact]
        public void IsLocaleSensitive_False_For_Latin()
        {
            Assert.False(FontFallbackScriptHints.IsLocaleSensitive(Script.Latin));
        }

        [Theory]
        [InlineData(Script.Hiragana, 49)]
        [InlineData(Script.Katakana, 50)]
        [InlineData(Script.Hangul, 56)]
        [InlineData(Script.Han, 59)]
        [InlineData(Script.Cyrillic, 9)]
        [InlineData(Script.Arabic, 13)]
        public void TryGetOS2Bit_Returns_Spec_Bit(Script script, int expected)
        {
            Assert.True(FontFallbackScriptHints.TryGetOS2Bit(script, out var bit));
            Assert.Equal(expected, bit);
        }

        [Theory]
        [InlineData("\U0001F600", true)]
        [InlineData("\U0001F600︎", false)]
        [InlineData("☀", false)]
        [InlineData("☀️", true)]
        [InlineData("★️", false)]
        [InlineData("1️⃣", true)]
        [InlineData("1⃣", true)]
        [InlineData("#", false)]
        [InlineData("1️", true)]
        [InlineData("\U0001F1E9\U0001F1EA", true)]
        [InlineData("☝\U0001F3FB", true)]
        [InlineData("❤️‍\U0001F525", true)]
        [InlineData("a", false)]
        public void HasEmojiPresentation_Follows_Variation_Selectors_And_Emoji_Sequences(string cluster, bool expected)
        {
            Assert.Equal(expected, FontFallbackScriptHints.HasEmojiPresentation(cluster.AsSpan()));
        }

        [Fact]
        public void TryGetOS2Bit_Latin_Returns_False()
        {
            Assert.False(FontFallbackScriptHints.TryGetOS2Bit(Script.Latin, out var bit));
            Assert.Equal(-1, bit);
        }
    }
}
