using System.Collections.Generic;

namespace TextTour
{
    /// <summary>A range of the string that the text source hands out with its own run properties.</summary>
    internal readonly record struct StyleSpan(int Start, int Length);

    /// <summary>A string chosen to make one or more pipeline stages visible.</summary>
    internal sealed record Preset(string Name, string Text, StyleSpan? Span, string Note)
    {
        public override string ToString() => Name;

        /// <summary>
        /// Latin with a ligature, an Arabic word with a number inside it, a Devanagari conjunct with
        /// a reordered vowel sign, a ZWJ family emoji and a CJK pair: every unit mismatch in one line.
        /// The style span covers the Arabic word and its number.
        /// </summary>
        public static Preset RunningExample { get; } = new(
            "Running example",
            "Office ffi \u0645\u0631\u062D\u0628\u0627 123 \u0915\u094D\u0937\u093F \U0001F468\u200D\U0001F469\u200D\U0001F467 \u6F22\u5B57",
            new StyleSpan(11, 9),
            "ligature, Arabic with a number, Devanagari conjunct, ZWJ emoji, CJK");

        public static IReadOnlyList<Preset> All { get; } = new[]
        {
            RunningExample,
            new Preset(
                "Latin only",
                "Official affinity: caf\u00E9 and cafe\u0301 look the same",
                new StyleSpan(19, 4),
                "ligatures, precomposed and combining accent"),
            new Preset(
                "Arabic",
                "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645 123 (\u0646\u0635) \u0639\u0631\u0628\u064A",
                new StyleSpan(6, 7),
                "joining forms, a number inside right-to-left text, mirrored brackets"),
            new Preset(
                "Devanagari",
                "\u0915\u094D\u0937\u093F \u0939\u093F\u0928\u094D\u0926\u0940 \u0915\u093F \u0915\u094D\u0937\u0924\u094D\u0930\u093F\u092F",
                new StyleSpan(5, 6),
                "conjuncts and the i-matra drawn before its consonants"),
            new Preset(
                "Emoji",
                "\U0001F468\u200D\U0001F469\u200D\U0001F467 \U0001F44D\U0001F3FD \U0001F1EA\U0001F1EA \u2764\uFE0F 1\uFE0F\u20E3",
                new StyleSpan(9, 4),
                "ZWJ sequence, skin tone modifier, flag, variation selector, keycap"),
            new Preset(
                "CJK",
                "\u6F22\u5B57\u304B\u306A\u4EA4\u3058\u308A\u6587\u3001\u300C\u65E5\u672C\u8A9E\u300D\u3068\u4E2D\u6587\u3002",
                new StyleSpan(9, 5),
                "breaks between ideographs, never before a closing bracket"),
        };
    }
}
