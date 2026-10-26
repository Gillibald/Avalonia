using System;
using System.Buffers.Binary;

namespace Avalonia.Media.Fonts.Tables
{
    /// <summary>
    /// Reads the ligature caret list of the OpenType <c>GDEF</c> table.
    /// See <see href="https://learn.microsoft.com/typography/opentype/spec/gdef"/>.
    /// </summary>
    /// <remarks>
    /// The table is kept as it is stored in the font and searched per query; nothing is expanded up
    /// front. Malformed data makes the affected glyph report no carets instead of throwing, because the
    /// callers are layout and hit testing.
    /// </remarks>
    internal sealed class GdefTable
    {
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse("GDEF");

        // Carets of one ligature are sorted on the stack up to this count, on the heap beyond it.
        private const int MaxStackCarets = 32;

        private readonly ReadOnlyMemory<byte> _table;
        private readonly int _ligCaretListOffset;

        private GdefTable(ReadOnlyMemory<byte> table, int ligCaretListOffset)
        {
            _table = table;
            _ligCaretListOffset = ligCaretListOffset;
        }

        public static GdefTable? TryLoad(GlyphTypeface glyphTypeface)
        {
            if (!glyphTypeface.FontMemory.TryGetTable(Tag, out var table))
            {
                return null;
            }

            return TryParse(table);
        }

        /// <summary>
        /// Reads the header of a <c>GDEF</c> table. Returns null when the table is not a version 1 table
        /// or has no ligature caret list.
        /// </summary>
        internal static GdefTable? TryParse(ReadOnlyMemory<byte> table)
        {
            // GDEF header, versions 1.0, 1.2 and 1.3 share the first 12 bytes:
            //   uint16 majorVersion, uint16 minorVersion, Offset16 glyphClassDefOffset,
            //   Offset16 attachListOffset, Offset16 ligCaretListOffset, Offset16 markAttachClassDefOffset
            var span = table.Span;

            if (!TryReadUInt16(span, 0, out var majorVersion) || majorVersion != 1 ||
                !TryReadUInt16(span, 8, out var ligCaretListOffset) || ligCaretListOffset == 0)
            {
                return null;
            }

            return new GdefTable(table, ligCaretListOffset);
        }

        /// <summary>
        /// Gets the caret positions defined for a ligature glyph, in design units from the glyph origin,
        /// in increasing order.
        /// </summary>
        /// <returns>
        /// The number of carets defined for the glyph, which can exceed the length of
        /// <paramref name="caretPositions"/>; 0 when there are none or any of them cannot be resolved.
        /// </returns>
        public int GetLigatureCarets(ushort glyphIndex, Span<int> caretPositions)
        {
            var span = _table.Span;

            // LigCaretList: Offset16 coverageOffset, uint16 ligGlyphCount, Offset16 ligGlyphOffsets[ligGlyphCount]
            var listOffset = _ligCaretListOffset;

            if (!TryReadUInt16(span, listOffset, out var coverageOffset) ||
                !TryReadUInt16(span, listOffset + 2, out var ligGlyphCount))
            {
                return 0;
            }

            var coverageIndex = GetCoverageIndex(span, listOffset + coverageOffset, glyphIndex);

            if (coverageIndex < 0 || coverageIndex >= ligGlyphCount ||
                !TryReadUInt16(span, listOffset + 4 + 2 * coverageIndex, out var ligGlyphOffset))
            {
                return 0;
            }

            // LigGlyph: uint16 caretCount, Offset16 caretValueOffsets[caretCount], relative to the LigGlyph
            var ligGlyph = listOffset + ligGlyphOffset;

            if (!TryReadUInt16(span, ligGlyph, out var caretCount) || caretCount == 0)
            {
                return 0;
            }

            var carets = caretCount <= MaxStackCarets ? stackalloc int[caretCount] : new int[caretCount];

            for (var i = 0; i < caretCount; i++)
            {
                if (!TryReadUInt16(span, ligGlyph + 2 + 2 * i, out var caretValueOffset) ||
                    !TryReadCaretValue(span, ligGlyph + caretValueOffset, out carets[i]))
                {
                    return 0;
                }
            }

            // The specification lists carets in increasing order; sort for fonts that do not.
            carets.Sort();

            carets.Slice(0, Math.Min(caretCount, caretPositions.Length)).CopyTo(caretPositions);

            return caretCount;
        }

        private static bool TryReadCaretValue(ReadOnlySpan<byte> span, int offset, out int coordinate)
        {
            coordinate = 0;

            if (!TryReadUInt16(span, offset, out var format))
            {
                return false;
            }

            switch (format)
            {
                // Format 1: int16 coordinate.
                // Format 3: int16 coordinate, Offset16 deviceOffset. A Device table adjusts the caret for
                // specific pixel sizes, which layout in device independent units does not use. A
                // VariationIndex table adds nothing at the default instance.
                case 1:
                case 3:
                    {
                        if (!TryReadUInt16(span, offset + 2, out var value))
                        {
                            return false;
                        }

                        coordinate = (short)value;

                        return true;
                    }

                // Format 2 names an outline contour point, which needs the glyph outline to resolve.
                // Treating it as unresolvable lets callers divide the ligature evenly instead.
                default:
                    return false;
            }
        }

        private static int GetCoverageIndex(ReadOnlySpan<byte> span, int offset, ushort glyphIndex)
        {
            if (!TryReadUInt16(span, offset, out var format) || !TryReadUInt16(span, offset + 2, out var count))
            {
                return -1;
            }

            var low = 0;
            var high = count - 1;

            switch (format)
            {
                // Format 1: uint16 glyphArray[glyphCount], sorted by glyph id.
                case 1:
                    {
                        while (low <= high)
                        {
                            var mid = (low + high) >> 1;

                            if (!TryReadUInt16(span, offset + 4 + 2 * mid, out var glyph))
                            {
                                return -1;
                            }

                            if (glyph == glyphIndex)
                            {
                                return mid;
                            }

                            if (glyph < glyphIndex)
                            {
                                low = mid + 1;
                            }
                            else
                            {
                                high = mid - 1;
                            }
                        }

                        return -1;
                    }

                // Format 2: RangeRecord { uint16 startGlyphID, uint16 endGlyphID, uint16 startCoverageIndex }[rangeCount]
                case 2:
                    {
                        while (low <= high)
                        {
                            var mid = (low + high) >> 1;
                            var record = offset + 4 + 6 * mid;

                            if (!TryReadUInt16(span, record, out var start) ||
                                !TryReadUInt16(span, record + 2, out var end) ||
                                !TryReadUInt16(span, record + 4, out var startCoverageIndex))
                            {
                                return -1;
                            }

                            if (glyphIndex < start)
                            {
                                high = mid - 1;
                            }
                            else if (glyphIndex > end)
                            {
                                low = mid + 1;
                            }
                            else
                            {
                                return startCoverageIndex + glyphIndex - start;
                            }
                        }

                        return -1;
                    }

                default:
                    return -1;
            }
        }

        private static bool TryReadUInt16(ReadOnlySpan<byte> span, int offset, out ushort value)
        {
            if (offset < 0 || offset > span.Length - 2)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset));

            return true;
        }
    }
}
