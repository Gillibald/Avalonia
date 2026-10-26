using System;

namespace Avalonia.Media.Fonts.Tables
{
    /// <summary>
    /// The decoded ligature caret list of a <c>GDEF</c> table: the caret positions of every ligature
    /// glyph that has resolvable carets, in design units from the glyph origin, ascending per glyph.
    /// </summary>
    /// <remarks>
    /// Immutable once built, so one instance can be published to and read by any number of threads.
    /// </remarks>
    internal sealed class LigatureCaretList
    {
        // Sorted ascending, one entry per glyph. The carets of _glyphs[i] are
        // _positions[_starts[i].._starts[i + 1]].
        private readonly ushort[] _glyphs;
        private readonly int[] _starts;
        private readonly int[] _positions;

        public LigatureCaretList(ushort[] glyphs, int[] starts, int[] positions)
        {
            _glyphs = glyphs;
            _starts = starts;
            _positions = positions;
        }

        public static LigatureCaretList Empty { get; } = new(Array.Empty<ushort>(), new int[1], Array.Empty<int>());

        /// <summary>
        /// Gets the carets of a ligature glyph, or an empty span when the list has none for it.
        /// </summary>
        public ReadOnlySpan<int> GetCarets(ushort glyphIndex)
        {
            var index = Array.BinarySearch(_glyphs, glyphIndex);

            if (index < 0)
            {
                return default;
            }

            var start = _starts[index];

            return _positions.AsSpan(start, _starts[index + 1] - start);
        }
    }
}
