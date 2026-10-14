using Avalonia.Media.Fonts.Tables.Cmap;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The glyphs of a typeface that draw ideographic scripts (Han, Kana, Hangul, Bopomofo),
    /// found from the code points the character map gives them. A glyph that no code point
    /// reaches, such as a substitution variant, belongs to the writing system that the font maps
    /// most of its characters to.
    /// </summary>
    internal sealed class IdeographicGlyphs
    {
        private static readonly (int First, int Last)[] s_ranges =
        {
            (0x1100, 0x11FF),   // Hangul Jamo
            (0x2E80, 0x2FDF),   // CJK and Kangxi radicals
            (0x3005, 0x3007),   // ideographic iteration mark, closing mark, number zero
            (0x3021, 0x3029),   // Hangzhou numerals
            (0x3038, 0x303B),
            (0x3040, 0x30FF),   // Hiragana, Katakana
            (0x3100, 0x312F),   // Bopomofo
            (0x3130, 0x318F),   // Hangul compatibility Jamo
            (0x31A0, 0x31BF),   // Bopomofo extended
            (0x31F0, 0x31FF),   // Katakana phonetic extensions
            (0x3400, 0x4DBF),   // CJK extension A
            (0x4E00, 0x9FFF),   // CJK unified ideographs
            (0xA960, 0xA97F),   // Hangul Jamo extended A
            (0xAC00, 0xD7FF),   // Hangul syllables, Jamo extended B
            (0xF900, 0xFAFF),   // CJK compatibility ideographs
            (0x20000, 0x3134F), // CJK extensions B to G, compatibility supplement
        };

        private readonly bool[]? _ideographic;
        private readonly bool[]? _mapped;
        private readonly bool _unmappedIdeographic;

        private IdeographicGlyphs(bool[]? ideographic, bool[]? mapped, bool unmappedIdeographic)
        {
            _ideographic = ideographic;
            _mapped = mapped;
            _unmappedIdeographic = unmappedIdeographic;
        }

        /// <summary>Whether the typeface maps any ideographic code point.</summary>
        public bool Any => _ideographic is not null;

        public static IdeographicGlyphs Create(GlyphTypeface typeface)
        {
            var map = typeface.CharacterToGlyphMap;
            var glyphCount = typeface.GlyphCount;

            if (glyphCount <= 0 || !MapsAnyIdeograph(map))
            {
                return new IdeographicGlyphs(null, null, false);
            }

            var ideographic = new bool[glyphCount];
            var mapped = new bool[glyphCount];
            var ideographicCount = 0;
            var otherCount = 0;
            var ranges = map.GetMappedRanges();

            while (ranges.MoveNext())
            {
                var range = ranges.Current;

                for (var codePoint = range.Start; codePoint <= range.End; codePoint++)
                {
                    if (!map.TryGetGlyph(codePoint, out var glyph) || glyph == 0 || glyph >= glyphCount)
                    {
                        continue;
                    }

                    mapped[glyph] = true;

                    if (IsIdeographic(codePoint))
                    {
                        ideographic[glyph] = true;
                        ideographicCount++;
                    }
                    else
                    {
                        otherCount++;
                    }
                }
            }

            return new IdeographicGlyphs(ideographic, mapped, ideographicCount > otherCount);
        }

        /// <summary>Whether <paramref name="glyph"/> draws an ideographic script.</summary>
        public bool Contains(ushort glyph)
        {
            if (_ideographic is null || glyph >= _ideographic.Length)
            {
                return false;
            }

            return _mapped![glyph] ? _ideographic[glyph] : _unmappedIdeographic;
        }

        private static bool MapsAnyIdeograph(CharacterToGlyphMap map)
        {
            var ranges = map.GetMappedRanges();

            while (ranges.MoveNext())
            {
                var range = ranges.Current;

                foreach (var (first, last) in s_ranges)
                {
                    if (range.Start <= last && range.End >= first)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsIdeographic(int codePoint)
        {
            foreach (var (first, last) in s_ranges)
            {
                if (codePoint >= first && codePoint <= last)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
