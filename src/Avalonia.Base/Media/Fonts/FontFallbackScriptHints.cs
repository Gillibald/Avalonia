using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media.TextFormatting.Unicode;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Script-aware hints used by <see cref="FontCollectionBase.TryMatchCharacter"/> to
    /// disambiguate locale-sensitive scripts (e.g. CJK), to provide deterministic probe
    /// codepoints for scoring candidate fonts, and to decide whether a candidate font is
    /// compatible with the caller's culture.
    /// </summary>
    internal static class FontFallbackScriptHints
    {
        /// <summary>
        /// Returns true when the script's preferred font typically depends on the user's
        /// culture (CJK Han is the canonical example: identical codepoints render with
        /// different fonts in zh-CN vs. ja-JP vs. ko-KR).
        /// </summary>
        public static bool IsLocaleSensitive(Script script)
            => GetProbeCodepoint(script) != 0;

        /// <summary>
        /// Refines an ambiguous script using the supplied codepoint's
        /// <see cref="Codepoint.HasScriptExtension"/> data and the requested culture.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rules mirror what major shaping/font-matching stacks (DirectWrite/UniscribeExtensions,
        /// CoreText, HarfBuzz + ICU likelySubtags) do:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="Script.KatakanaOrHiragana"/> (Hrkt) is resolved using the codepoint's
        /// <c>Script_Extensions</c> set per UAX #24 — if it contains Hiragana the codepoint
        /// is treated as Hiragana, otherwise Katakana.
        /// </description></item>
        /// <item><description>
        /// <see cref="Script.Han"/> is refined to the user's regional CJK variant via
        /// <see cref="Bcp47ScriptResolver.GetScriptSubtag"/>: <c>"Jpan"</c> selects Hiragana
        /// (which forces matching against a Japanese font), <c>"Kore"</c> selects Hangul,
        /// and <c>"Hans"/"Hant"</c> leave the script as Han.
        /// </description></item>
        /// <item><description>
        /// Any other script is returned unchanged.
        /// </description></item>
        /// </list>
        /// </remarks>
        public static Script RefineWithCulture(Codepoint codepoint, CultureInfo? culture)
        {
            var script = codepoint.Script;

            if (script == Script.KatakanaOrHiragana)
            {
                return codepoint.HasScriptExtension(Script.Hiragana)
                    ? Script.Hiragana
                    : Script.Katakana;
            }

            if (script != Script.Han)
            {
                return script;
            }

            var subtag = Bcp47ScriptResolver.GetScriptSubtag(culture);

            if (subtag is null)
            {
                return script;
            }

            return subtag switch
            {
                "Jpan" => Script.Hiragana,
                "Kore" => Script.Hangul,
                _ => script,
            };
        }

        /// <summary>
        /// Determines whether <paramref name="candidate"/> self-declares coverage for the supplied
        /// culture's writing system. Used as a positive signal in font fallback scoring.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the font advertises the culture's BCP-47 tag in its OpenType <c>meta</c>
        /// table (<c>dlng</c>/<c>slng</c>), or when its OS/2 codepage range bits cover the
        /// resolved script subtag's canonical codepage. Returns <c>false</c> when no positive
        /// signal exists; this is a non-negative test and should be combined with the probe-based
        /// fallback in <see cref="FontCollectionBase"/>.
        /// </returns>
        public static bool IsFontCompatibleWithCulture(GlyphTypeface candidate, CultureInfo? culture)
        {
            if (culture is null || culture == CultureInfo.InvariantCulture)
            {
                return false;
            }

            if (candidate.DeclaresLanguageCoverage(culture))
            {
                return true;
            }

            var coverage = candidate.CodePageCoverage;

            if (coverage == FontCodePageCoverage.None)
            {
                return false;
            }

            var subtag = Bcp47ScriptResolver.GetScriptSubtag(culture);

            return subtag != null && HasCodePageFor(coverage, subtag);
        }

        /// <summary>
        /// Counts how many distinct CJK code page groups (Japanese JIS, Simplified Chinese,
        /// Traditional Chinese, Korean) the font's legacy OS/2 declaration covers. Fonts that
        /// predate the OpenType <c>meta</c> table often set several CJK pages indiscriminately;
        /// such a promiscuous declaration is a much weaker language signal than a single,
        /// targeted page.
        /// </summary>
        public static int CountCjkCodePageGroups(FontCodePageCoverage coverage)
        {
            var count = 0;

            if ((coverage & FontCodePageCoverage.JapaneseJis) != 0)
            {
                count++;
            }

            if ((coverage & FontCodePageCoverage.ChineseSimplified) != 0)
            {
                count++;
            }

            if ((coverage & FontCodePageCoverage.ChineseTraditional) != 0)
            {
                count++;
            }

            if ((coverage & (FontCodePageCoverage.KoreanWansung | FontCodePageCoverage.KoreanJohab)) != 0)
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Scores the candidate's self-declared coverage for the culture. The OpenType
        /// <c>meta</c> table is authoritative (+4) and a targeted legacy code page is an
        /// equally strong signal (+4), but a promiscuous CJK code page declaration —
        /// several CJK pages set at once — only weakly supports the culture (+1):
        /// browsers resolve such fonts through their name-table languages instead.
        /// </summary>
        public static int ScoreDeclaredCoverage(GlyphTypeface candidate, CultureInfo culture)
        {
            if (candidate.DeclaresLanguageCoverage(culture))
            {
                return 4;
            }

            var coverage = candidate.CodePageCoverage;

            if (coverage == FontCodePageCoverage.None)
            {
                return 0;
            }

            var subtag = Bcp47ScriptResolver.GetScriptSubtag(culture);

            if (subtag is null || !HasCodePageFor(coverage, subtag))
            {
                return 0;
            }

            var isCjk = subtag is "Jpan" or "Kore" or "Hans" or "Hant";

            if (!isCjk)
            {
                return 4;
            }

            return CountCjkCodePageGroups(coverage) == 1 ? 4 : 1;
        }

        /// <summary>
        /// Scores the candidate's name-table languages against the requested culture:
        /// exact culture (+8), the culture's parent (+4), or the same language under
        /// another region or script — e.g. zh-CN names for a zh-Hant request — (+3).
        /// A font that localizes its names for a different CJK language and not the
        /// requested one scores down (−3): CJK font preference lists never cross
        /// languages, so e.g. a Chinese-named font must not win Japanese text over a
        /// Japanese font.
        /// </summary>
        public static int ScoreFamilyNames(IReadOnlyDictionary<CultureInfo, string> familyNames, CultureInfo culture)
        {
            if (familyNames.Count == 0 || culture == CultureInfo.InvariantCulture)
            {
                return 0;
            }

            if (familyNames.ContainsKey(culture))
            {
                return 8;
            }

            // Walk the whole parent chain (zh-CN → zh-Hans → zh) — a font that
            // localizes for any ancestor of the requested culture is a direct hit.
            for (var parent = culture.Parent;
                 parent != null && parent != CultureInfo.InvariantCulture;
                 parent = parent.Parent)
            {
                if (familyNames.ContainsKey(parent))
                {
                    return 4;
                }
            }

            var language = culture.TwoLetterISOLanguageName;
            var hasOtherCjkLanguage = false;

            foreach (var key in familyNames.Keys)
            {
                if (key == CultureInfo.InvariantCulture)
                {
                    continue;
                }

                var keyLanguage = key.TwoLetterISOLanguageName;

                if (string.Equals(keyLanguage, language, StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }

                hasOtherCjkLanguage |= IsCjkLanguage(keyLanguage);
            }

            if (IsCjkLanguage(language) && hasOtherCjkLanguage)
            {
                return -3;
            }

            return 0;
        }

        /// <summary>
        /// Returns true when the font carries a positive Chinese design signal: Chinese
        /// name-table languages, a <c>meta</c> declaration for Hans or Hant, or a
        /// targeted (non-promiscuous) Chinese code page. Used to resolve unmarked Han
        /// the way browsers do — language-less Han text defaults to a Chinese font.
        /// </summary>
        public static bool HasChineseDesignEvidence(GlyphTypeface candidate)
        {
            foreach (var key in candidate.FamilyNames.Keys)
            {
                if (key != CultureInfo.InvariantCulture
                    && string.Equals(key.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (candidate.DeclaresLanguageCoverage(CultureInfo.GetCultureInfo("zh-Hans"))
                || candidate.DeclaresLanguageCoverage(CultureInfo.GetCultureInfo("zh-Hant")))
            {
                return true;
            }

            var coverage = candidate.CodePageCoverage;

            return (coverage & (FontCodePageCoverage.ChineseSimplified | FontCodePageCoverage.ChineseTraditional)) != 0
                   && CountCjkCodePageGroups(coverage) == 1;
        }

        private static bool IsCjkLanguage(string language)
            => language is "zh" or "ja" or "ko";

        private static bool HasCodePageFor(FontCodePageCoverage coverage, string subtag) => subtag switch
        {
            "Jpan" => (coverage & FontCodePageCoverage.JapaneseJis) != 0,
            "Kore" => (coverage & (FontCodePageCoverage.KoreanWansung | FontCodePageCoverage.KoreanJohab)) != 0,
            "Hans" => (coverage & FontCodePageCoverage.ChineseSimplified) != 0,
            "Hant" => (coverage & FontCodePageCoverage.ChineseTraditional) != 0,
            "Cyrl" => (coverage & FontCodePageCoverage.Cyrillic) != 0,
            "Grek" => (coverage & FontCodePageCoverage.Greek) != 0,
            "Arab" => (coverage & FontCodePageCoverage.Arabic) != 0,
            "Hebr" => (coverage & FontCodePageCoverage.Hebrew) != 0,
            "Thai" => (coverage & FontCodePageCoverage.Thai) != 0,
            "Latn" => (coverage & FontCodePageCoverage.Latin1) != 0,
            _ => false,
        };

        /// <summary>
        /// Returns a representative codepoint for the script that can be used to probe a
        /// candidate font's character-to-glyph map. Returns 0 for scripts without a probe
        /// (in which case the script is considered locale-insensitive).
        /// </summary>
        public static int GetProbeCodepoint(Script script) => script switch
        {
            Script.Han => 0x4E2D,        // 中
            Script.Hiragana => 0x3042,   // あ
            Script.Katakana => 0x30A2,   // ア
            Script.KatakanaOrHiragana => 0x3042,
            Script.Hangul => 0xAC00,     // 가
            Script.Bopomofo => 0x3105,   // ㄅ
            Script.Arabic => 0x0627,     // ا
            Script.Hebrew => 0x05D0,     // א
            Script.Devanagari => 0x0915, // क
            Script.Bengali => 0x0995,    // ক
            Script.Thai => 0x0E01,       // ก
            Script.Tibetan => 0x0F40,    // ཀ
            Script.Cyrillic => 0x0410,   // А
            Script.Greek => 0x0391,      // Α
            _ => 0,
        };

        /// <summary>
        /// Returns the bit index into the OS/2 ulUnicodeRange bitfield (UnicodeRange1..4) that the
        /// OpenType specification assigns to the supplied script, or -1 if the script has no
        /// canonical OS/2 bit. Bits 0..31 belong to UnicodeRange1, 32..63 to UnicodeRange2, etc.
        /// </summary>
        public static bool TryGetOS2Bit(Script script, out int bit)
        {
            bit = script switch
            {
                Script.Greek => 7,
                Script.Cyrillic => 9,
                Script.Hebrew => 11,
                Script.Arabic => 13,
                Script.Devanagari => 15,
                Script.Bengali => 16,
                Script.Thai => 24,
                Script.Hiragana => 49,
                Script.Katakana => 50,
                Script.KatakanaOrHiragana => 49,
                Script.Bopomofo => 51,
                Script.Hangul => 56,
                Script.Han => 59,
                Script.Tibetan => 70,
                _ => -1,
            };

            return bit >= 0;
        }
    }
}
