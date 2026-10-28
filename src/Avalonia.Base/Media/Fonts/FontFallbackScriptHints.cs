using System;
using System.Globalization;
using Avalonia.Media.TextFormatting.Unicode;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Script-aware hints used by <see cref="FontCollectionBase.TryMatchCharacter(int, FontStyle, FontWeight, FontStretch, string?, CultureInfo?, out Typeface)"/> to
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
        /// A <see cref="Script.Common"/> or <see cref="Script.Inherited"/> codepoint whose
        /// <c>Script_Extensions</c> are all East Asian scripts (ideographic punctuation, corner
        /// brackets, the prolonged sound mark) is refined as the script it is written in, see
        /// <see cref="GetWritingScript"/>.
        /// </description></item>
        /// <item><description>
        /// Any other script is returned unchanged.
        /// </description></item>
        /// </list>
        /// </remarks>
        public static Script RefineWithCulture(Codepoint codepoint, CultureInfo? culture)
        {
            var script = GetWritingScript(codepoint);

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
        /// The script fallback treats <paramref name="codepoint"/> as written in. For a
        /// <see cref="Script.Common"/> or <see cref="Script.Inherited"/> codepoint whose
        /// <c>Script_Extensions</c> are all East Asian scripts (Han, Hiragana, Katakana, Hangul,
        /// Bopomofo, Yi, Mongolian, Phags-pa) and name at least one of the first five, that is
        /// <see cref="Script.Han"/> when the set contains Han, so U+3001 follows the culture's Han
        /// writing system like the ideographs around it; otherwise Hiragana, Katakana, Hangul or
        /// Bopomofo in that order, so U+30FC is kana in any culture. Such a mark has regional designs
        /// like the ideographs, and matching it as <see cref="Script.Common"/> lets any face that maps
        /// it answer. Every other codepoint keeps its <see cref="Codepoint.Script"/>; fullwidth forms
        /// such as U+FF0C have no <c>Script_Extensions</c> and stay Common.
        /// </summary>
        public static Script GetWritingScript(Codepoint codepoint)
        {
            var script = codepoint.Script;

            if (script is not (Script.Common or Script.Inherited))
            {
                return script;
            }

            var extensions = UnicodeData.GetScriptExtensions(codepoint.Value);

            if (extensions.IsEmpty)
            {
                return script;
            }

            bool han = false, hiragana = false, katakana = false, hangul = false, bopomofo = false;

            foreach (var value in extensions)
            {
                switch ((Script)value)
                {
                    case Script.Han:
                        han = true;
                        break;
                    case Script.Hiragana:
                        hiragana = true;
                        break;
                    case Script.Katakana:
                        katakana = true;
                        break;
                    case Script.KatakanaOrHiragana:
                        hiragana = katakana = true;
                        break;
                    case Script.Hangul:
                        hangul = true;
                        break;
                    case Script.Bopomofo:
                        bopomofo = true;
                        break;
                    case Script.Yi:
                    case Script.Mongolian:
                    case Script.PhagsPa:
                        break;
                    default:
                        return script;
                }
            }

            return han ? Script.Han
                : hiragana ? Script.Hiragana
                : katakana ? Script.Katakana
                : hangul ? Script.Hangul
                : bopomofo ? Script.Bopomofo
                : script;
        }

        /// <summary>
        /// A codepoint every emoji font maps and that defaults to emoji presentation, so a platform
        /// asked for it answers with its emoji font.
        /// </summary>
        public const int EmojiProbeCodepoint = 0x1F600;

        /// <summary>
        /// The script of the fallback bucket a request for <paramref name="codepoint"/> in
        /// <paramref name="culture"/> belongs to. Emoji presentation is one bucket whatever the
        /// codepoint's script, so every emoji of a paragraph keeps one face; text is bucketed by
        /// <see cref="RefineWithCulture"/>. The font collection keys its replayed fallback answers
        /// and the itemizer its fallback reuse by this script, the presentation and the culture.
        /// </summary>
        public static Script GetBucketScript(Codepoint codepoint, bool emojiPresentation, CultureInfo? culture)
            => emojiPresentation ? Script.Common : RefineWithCulture(codepoint, culture);

        /// <summary>
        /// The presentation a request for <paramref name="codepoint"/> alone asks for: emoji when
        /// the codepoint defaults to it (<c>Emoji_Presentation</c>, regional indicators included).
        /// </summary>
        public static bool HasEmojiPresentation(Codepoint codepoint) => codepoint.HasEmojiPresentation;

        /// <summary>
        /// Determines whether the grapheme cluster <paramref name="cluster"/> asks for emoji
        /// presentation. U+FE0E right after the base asks for text and U+FE0F right after a
        /// pictographic or emoji base asks for emoji. A keycap (<c>0-9</c>, <c>#</c>, <c>*</c> with
        /// U+20E3), an emoji modifier or a pictograph with default emoji presentation joined later
        /// in the cluster ask for emoji. Otherwise the base's default presentation applies.
        /// </summary>
        public static bool HasEmojiPresentation(ReadOnlySpan<char> cluster)
        {
            var codepoints = new CodepointEnumerator(cluster);

            if (!codepoints.MoveNext(out var first))
            {
                return false;
            }

            var pictographic = first.IsEmoji || first.GraphemeBreakClass == GraphemeBreakClass.ExtendedPictographic;
            var keycapBase = first.Value is '#' or '*' or >= '0' and <= '9';
            var followsBase = true;

            while (codepoints.MoveNext(out var codepoint))
            {
                switch (codepoint.Value)
                {
                    case 0xFE0E when followsBase:
                        return false;
                    case 0xFE0F when followsBase && pictographic:
                        return true;
                    case 0x20E3 when keycapBase:
                        return true;
                    default:
                        if (codepoint.HasEmojiPresentation)
                        {
                            return true;
                        }

                        break;
                }

                followsBase = false;
            }

            return first.HasEmojiPresentation;
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

            return subtag switch
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
        }

        // Scripts a design language may name for each kana script and for Hangul: the Japanese or
        // Korean writing system, or the script alone.
        private static readonly string[] s_hiraganaDesigns = ["Jpan", "Hira", "Hrkt"];
        private static readonly string[] s_katakanaDesigns = ["Jpan", "Kana", "Hrkt"];
        private static readonly string[] s_kanaDesigns = ["Jpan", "Hira", "Kana", "Hrkt"];
        private static readonly string[] s_hangulDesigns = ["Kore", "Hang"];

        /// <summary>
        /// How <paramref name="candidate"/> relates to the writing system a fallback request for
        /// <paramref name="codepoint"/> in <paramref name="culture"/> asks for. Han text is asked
        /// for in the culture's writing system (Japanese, Korean, Simplified or Traditional
        /// Chinese); kana are asked for in Japanese and Hangul in Korean, whatever the culture.
        /// For these a face matches only when the writing system is one of its design languages
        /// (<c>dlng</c>), named as a language (<c>"ja"</c>, <c>"ko"</c>) or as a script
        /// (<c>"Jpan"</c>, <c>"Hira"</c>, <c>"Kana"</c>, <c>"Hrkt"</c>, <c>"Kore"</c>,
        /// <c>"Hang"</c>). Supported languages (<c>slng</c>) and OS/2 code pages describe coverage,
        /// which a Simplified Chinese design that lists Japanese has as well, and its kana or Han
        /// would put a second design into a Japanese word. A face that declares no design language
        /// does not match. Text in any other script asks for a face that supports the script. Han
        /// in a culture that names none of these writing systems is
        /// <see cref="WritingSystemMatch.Unspecified"/>: any regional design could answer it, so
        /// the choice belongs to the platform. East Asian punctuation and marks of the Common script
        /// are asked for in the script <see cref="GetWritingScript"/> gives them.
        /// </summary>
        public static WritingSystemMatch MatchWritingSystem(GlyphTypeface candidate, Codepoint codepoint, CultureInfo? culture)
        {
            var script = GetWritingScript(codepoint);

            if (script == Script.Han)
            {
                if (GetHanWritingSystem(culture) is not { } writingSystem)
                {
                    return WritingSystemMatch.Unspecified;
                }

                return candidate.DesignsForWritingSystem(writingSystem)
                    ? WritingSystemMatch.Match
                    : WritingSystemMatch.Mismatch;
            }

            var designs = script switch
            {
                Script.Hiragana => s_hiraganaDesigns,
                Script.Katakana => s_katakanaDesigns,
                Script.KatakanaOrHiragana => s_kanaDesigns,
                Script.Hangul => s_hangulDesigns,
                _ => null,
            };

            if (designs is null)
            {
                return candidate.SupportsScript(RefineWithCulture(codepoint, culture))
                    ? WritingSystemMatch.Match
                    : WritingSystemMatch.Mismatch;
            }

            return candidate.DesignsForWritingSystem(designs)
                ? WritingSystemMatch.Match
                : WritingSystemMatch.Mismatch;
        }

        /// <summary>The Han writing system <paramref name="culture"/> writes in, if it has one.</summary>
        private static string? GetHanWritingSystem(CultureInfo? culture)
            => Bcp47ScriptResolver.GetScriptSubtag(culture) switch
            {
                "Jpan" => "Jpan",
                "Kore" => "Kore",
                "Hans" => "Hans",
                "Hant" => "Hant",
                _ => null,
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

        // OpenType GSUB/GPOS script tags a font must declare to shape each complex script. Indic
        // and Myanmar expose a modern ("dev2") and a legacy ("deva") tag; either is accepted.
        private static readonly OpenTypeTag Arab = OpenTypeTag.Parse("arab");
        private static readonly OpenTypeTag Syrc = OpenTypeTag.Parse("syrc");
        private static readonly OpenTypeTag Mong = OpenTypeTag.Parse("mong");
        private static readonly OpenTypeTag Thaa = OpenTypeTag.Parse("thaa");
        private static readonly OpenTypeTag Khmr = OpenTypeTag.Parse("khmr");
        private static readonly OpenTypeTag Tibt = OpenTypeTag.Parse("tibt");
        private static readonly OpenTypeTag Sinh = OpenTypeTag.Parse("sinh");
        private static readonly OpenTypeTag Dev2 = OpenTypeTag.Parse("dev2");
        private static readonly OpenTypeTag Deva = OpenTypeTag.Parse("deva");
        private static readonly OpenTypeTag Bng2 = OpenTypeTag.Parse("bng2");
        private static readonly OpenTypeTag Beng = OpenTypeTag.Parse("beng");
        private static readonly OpenTypeTag Gur2 = OpenTypeTag.Parse("gur2");
        private static readonly OpenTypeTag Guru = OpenTypeTag.Parse("guru");
        private static readonly OpenTypeTag Gjr2 = OpenTypeTag.Parse("gjr2");
        private static readonly OpenTypeTag Gujr = OpenTypeTag.Parse("gujr");
        private static readonly OpenTypeTag Ory2 = OpenTypeTag.Parse("ory2");
        private static readonly OpenTypeTag Orya = OpenTypeTag.Parse("orya");
        private static readonly OpenTypeTag Tml2 = OpenTypeTag.Parse("tml2");
        private static readonly OpenTypeTag Taml = OpenTypeTag.Parse("taml");
        private static readonly OpenTypeTag Tel2 = OpenTypeTag.Parse("tel2");
        private static readonly OpenTypeTag Telu = OpenTypeTag.Parse("telu");
        private static readonly OpenTypeTag Knd2 = OpenTypeTag.Parse("knd2");
        private static readonly OpenTypeTag Knda = OpenTypeTag.Parse("knda");
        private static readonly OpenTypeTag Mlm2 = OpenTypeTag.Parse("mlm2");
        private static readonly OpenTypeTag Mlym = OpenTypeTag.Parse("mlym");
        private static readonly OpenTypeTag Mym2 = OpenTypeTag.Parse("mym2");
        private static readonly OpenTypeTag Mymr = OpenTypeTag.Parse("mymr");

        /// <summary>
        /// For scripts that require OpenType complex shaping (cursive joining, reordering, conjunct
        /// formation, dependent-vowel positioning, …), returns the GSUB/GPOS script tag(s) a font
        /// must declare in order to shape them. A font that maps the codepoints through cmap but
        /// declares none of these tags cannot shape the script and should be skipped during fallback.
        /// </summary>
        /// <returns>
        /// <c>true</c> with one or two acceptable script tags (Indic and Myanmar expose both a modern
        /// "dev2"-style tag and a legacy "deva"-style tag; for single-tag scripts
        /// <paramref name="secondary"/> equals <paramref name="primary"/>). <c>false</c> for scripts
        /// that render acceptably from cmap alone (Latin, CJK, Hangul, Hebrew, Thai, …), for which no
        /// layout-table gate should apply.
        /// </returns>
        public static bool TryGetComplexShapingTags(Script script, out OpenTypeTag primary, out OpenTypeTag secondary)
        {
            switch (script)
            {
                case Script.Arabic: primary = secondary = Arab; return true;
                case Script.Syriac: primary = secondary = Syrc; return true;
                case Script.Mongolian: primary = secondary = Mong; return true;
                case Script.Thaana: primary = secondary = Thaa; return true;
                case Script.Khmer: primary = secondary = Khmr; return true;
                case Script.Tibetan: primary = secondary = Tibt; return true;
                case Script.Sinhala: primary = secondary = Sinh; return true;
                case Script.Devanagari: primary = Dev2; secondary = Deva; return true;
                case Script.Bengali: primary = Bng2; secondary = Beng; return true;
                case Script.Gurmukhi: primary = Gur2; secondary = Guru; return true;
                case Script.Gujarati: primary = Gjr2; secondary = Gujr; return true;
                case Script.Oriya: primary = Ory2; secondary = Orya; return true;
                case Script.Tamil: primary = Tml2; secondary = Taml; return true;
                case Script.Telugu: primary = Tel2; secondary = Telu; return true;
                case Script.Kannada: primary = Knd2; secondary = Knda; return true;
                case Script.Malayalam: primary = Mlm2; secondary = Mlym; return true;
                case Script.Myanmar: primary = Mym2; secondary = Mymr; return true;
                default: primary = default; secondary = default; return false;
            }
        }
    }

    /// <summary>How a face relates to the writing system a fallback request asks for.</summary>
    internal enum WritingSystemMatch
    {
        /// <summary>
        /// The face names the requested writing system among its design languages (Han, kana,
        /// Hangul) or supports the requested script (any other script).
        /// </summary>
        Match,

        /// <summary>
        /// For Han, kana and Hangul the face does not name the requested writing system among its
        /// design languages; for any other script it lacks the script.
        /// </summary>
        Mismatch,

        /// <summary>
        /// The request names no writing system although the script has regional designs (Han in a
        /// culture without a CJK writing system), so no face can be judged to match it.
        /// </summary>
        Unspecified,
    }
}
