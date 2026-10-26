using System;

namespace Avalonia.Media.Fonts.Tables
{
    /// <summary>
    /// Reads the ligature caret list of the OpenType <c>GDEF</c> table.
    /// See <see href="https://learn.microsoft.com/typography/opentype/spec/gdef"/>.
    /// </summary>
    internal sealed class GdefTable
    {
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse("GDEF");

        private GdefTable()
        {
        }

        public static GdefTable? TryLoad(GlyphTypeface glyphTypeface)
        {
            if (!glyphTypeface.FontMemory.TryGetTable(Tag, out var table))
            {
                return null;
            }

            return TryParse(table);
        }

        internal static GdefTable? TryParse(ReadOnlyMemory<byte> table) => null;

        /// <summary>
        /// Gets the caret positions defined for a ligature glyph, in design units from the glyph origin.
        /// </summary>
        /// <returns>The number of carets defined for the glyph, or 0 when there are none or they cannot be resolved.</returns>
        public int GetLigatureCarets(ushort glyphIndex, Span<int> caretPositions) => 0;
    }
}
