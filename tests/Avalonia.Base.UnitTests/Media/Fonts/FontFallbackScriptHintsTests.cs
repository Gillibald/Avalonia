using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
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

        [Fact]
        public void RefineWithCulture_Common_Codepoint_Passes_Through()
        {
            // U+30FC has primary Script=Common in Avalonia's data (its Hrkt membership lives
            // in Script_Extensions and is consulted directly through Codepoint.HasScriptExtension).
            // RefineWithCulture only refines codepoints whose primary script is ambiguous.
            var cp = new Codepoint(0x30FC);

            var refined = FontFallbackScriptHints.RefineWithCulture(cp, culture: null);

            Assert.Equal(cp.Script, refined);
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

        [Fact]
        public void TryGetOS2Bit_Latin_Returns_False()
        {
            Assert.False(FontFallbackScriptHints.TryGetOS2Bit(Script.Latin, out var bit));
            Assert.Equal(-1, bit);
        }

        [Theory]
        [InlineData(FontCodePageCoverage.None, 0)]
        [InlineData(FontCodePageCoverage.Latin1 | FontCodePageCoverage.Cyrillic, 0)]
        [InlineData(FontCodePageCoverage.JapaneseJis, 1)]
        [InlineData(FontCodePageCoverage.ChineseSimplified, 1)]
        [InlineData(FontCodePageCoverage.KoreanWansung | FontCodePageCoverage.KoreanJohab, 1)]
        [InlineData(FontCodePageCoverage.JapaneseJis | FontCodePageCoverage.ChineseTraditional, 2)]
        [InlineData(FontCodePageCoverage.JapaneseJis | FontCodePageCoverage.ChineseSimplified
                    | FontCodePageCoverage.ChineseTraditional | FontCodePageCoverage.KoreanWansung, 4)]
        public void CountCjkCodePageGroups_Counts_Distinct_Groups(FontCodePageCoverage coverage, int expected)
        {
            Assert.Equal(expected, FontFallbackScriptHints.CountCjkCodePageGroups(coverage));
        }

        [Fact]
        public void ScoreFamilyNames_Exact_Culture_Wins()
        {
            var names = Names("zh-CN", "en-US");

            Assert.Equal(8, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("zh-CN")));
        }

        [Fact]
        public void ScoreFamilyNames_Parent_Culture_Scores_Below_Exact()
        {
            var names = Names("zh", "en-US");

            Assert.Equal(4, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("zh-CN")));
        }

        [Fact]
        public void ScoreFamilyNames_Same_Language_Other_Region_Scores_Positive()
        {
            // A Simplified Chinese font (zh-CN names) asked for Traditional Chinese:
            // same language, different region — the best available Chinese candidate.
            var names = Names("zh-CN", "en-US");

            Assert.Equal(3, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("zh-HANT")));
        }

        [Fact]
        public void ScoreFamilyNames_Other_Cjk_Language_Scores_Negative()
        {
            // A Chinese-named font asked for Japanese: CJK preference lists never
            // cross languages, so the candidate is scored down.
            var names = Names("zh-CN", "en-US");

            Assert.Equal(-3, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("ja-JP")));
        }

        [Fact]
        public void ScoreFamilyNames_Non_Cjk_Mismatch_Is_Neutral()
        {
            var names = Names("en-US");

            Assert.Equal(0, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("ja-JP")));
        }

        [Fact]
        public void ScoreFamilyNames_Invariant_Entries_Are_Ignored()
        {
            // Fonts without localized name records surface a single synthesized
            // invariant entry; it carries no language signal.
            var names = new Dictionary<CultureInfo, string> { [CultureInfo.InvariantCulture] = "Font" };

            Assert.Equal(0, FontFallbackScriptHints.ScoreFamilyNames(names, CultureInfo.GetCultureInfo("ja-JP")));
        }

        private static Dictionary<CultureInfo, string> Names(params string[] cultures)
        {
            var names = new Dictionary<CultureInfo, string>();

            foreach (var culture in cultures)
            {
                names[CultureInfo.GetCultureInfo(culture)] = "Font";
            }

            return names;
        }
    }
}
