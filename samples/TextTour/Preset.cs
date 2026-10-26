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
            "Office ffi مرحبا 123 क्षि \U0001F468‍\U0001F469‍\U0001F467 漢字",
            new StyleSpan(11, 9),
            "ligature, Arabic with a number, Devanagari conjunct, ZWJ emoji, CJK");

        public static IReadOnlyList<Preset> All { get; } = new[]
        {
            RunningExample,
            new Preset(
                "Latin only",
                "Official affinity: café and café look the same",
                new StyleSpan(19, 4),
                "ligatures, precomposed and combining accent"),
            new Preset(
                "Arabic",
                "مرحبا بالعالم 123 (نص) عربي",
                new StyleSpan(6, 7),
                "joining forms, a number inside right-to-left text, mirrored brackets"),
            new Preset(
                "Devanagari",
                "क्षि हिन्दी कि क्षत्रिय",
                new StyleSpan(5, 6),
                "conjuncts and the i-matra drawn before its consonants"),
            new Preset(
                "Emoji",
                "\U0001F468‍\U0001F469‍\U0001F467 \U0001F44D\U0001F3FD \U0001F1EA\U0001F1EA ❤️ 1️⃣",
                new StyleSpan(9, 4),
                "ZWJ sequence, skin tone modifier, flag, variation selector, keycap"),
            new Preset(
                "CJK",
                "漢字かな交じり文、「日本語」と中文。",
                new StyleSpan(9, 5),
                "breaks between ideographs, never before a closing bracket"),
        };
    }
}
