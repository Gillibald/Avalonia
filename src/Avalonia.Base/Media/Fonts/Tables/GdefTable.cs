using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Variation;

namespace Avalonia.Media.Fonts.Tables
{
    /// <summary>
    /// Reads the ligature caret list of the OpenType <c>GDEF</c> table.
    /// See <see href="https://learn.microsoft.com/typography/opentype/spec/gdef"/>.
    /// </summary>
    /// <remarks>
    /// Malformed data makes the affected glyph report no carets instead of throwing, because the
    /// callers are layout and hit testing.
    /// </remarks>
    internal sealed class GdefTable
    {
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse("GDEF");

        private readonly ReadOnlyMemory<byte> _table;
        private readonly int _ligCaretListOffset;
        private readonly uint _itemVariationStoreOffset;

        // Loaded on the first caret that needs a variation delta. The store is immutable, so a race
        // only loads it twice.
        private volatile ItemVariationStore? _itemVariationStore;
        private volatile bool _itemVariationStoreInvalid;

        private GdefTable(ReadOnlyMemory<byte> table, int ligCaretListOffset, uint itemVariationStoreOffset)
        {
            _table = table;
            _ligCaretListOffset = ligCaretListOffset;
            _itemVariationStoreOffset = itemVariationStoreOffset;
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
                !TryReadUInt16(span, 2, out var minorVersion) ||
                !TryReadUInt16(span, 8, out var ligCaretListOffset) || ligCaretListOffset == 0)
            {
                return null;
            }

            // Version 1.3 adds Offset32 itemVarStoreOffset after markGlyphSetsDefOffset.
            uint itemVariationStoreOffset = 0;

            if (minorVersion >= 3 && TryReadUInt16(span, 14, out var high) && TryReadUInt16(span, 16, out var low))
            {
                itemVariationStoreOffset = ((uint)high << 16) | low;
            }

            return new GdefTable(table, ligCaretListOffset, itemVariationStoreOffset);
        }

        /// <summary>
        /// Decodes the carets of every ligature glyph in the ligature caret list, in design units from the
        /// glyph origin, in increasing order per glyph. A ligature with a caret that cannot be resolved is
        /// left out, so it reports no carets. With <paramref name="variationCoordinates"/>, the normalized
        /// coordinates of a variable font instance in fvar axis order, carets that carry a variation index
        /// move by their delta for that instance.
        /// </summary>
        public LigatureCaretList DecodeLigatureCarets(ReadOnlySpan<float> variationCoordinates = default)
        {
            var span = _table.Span;

            // LigCaretList: Offset16 coverageOffset, uint16 ligGlyphCount, Offset16 ligGlyphOffsets[ligGlyphCount]
            var listOffset = _ligCaretListOffset;

            if (!TryReadUInt16(span, listOffset, out var coverageOffset) ||
                !TryReadUInt16(span, listOffset + 2, out var ligGlyphCount) || ligGlyphCount == 0)
            {
                return LigatureCaretList.Empty;
            }

            var coverage = listOffset + coverageOffset;

            if (!TryReadUInt16(span, coverage, out var coverageFormat) ||
                !TryReadUInt16(span, coverage + 2, out var coverageCount))
            {
                return LigatureCaretList.Empty;
            }

            var ligatures = new List<(ushort Glyph, int Start, int Count)>();
            var positions = new List<int>();

            switch (coverageFormat)
            {
                // Format 1: uint16 glyphArray[glyphCount]; the coverage index is the array index.
                case 1:
                    {
                        var count = Math.Min(coverageCount, ligGlyphCount);

                        for (var coverageIndex = 0; coverageIndex < count; coverageIndex++)
                        {
                            if (!TryReadUInt16(span, coverage + 4 + 2 * coverageIndex, out var glyph))
                            {
                                break;
                            }

                            AddLigature(span, glyph, coverageIndex, variationCoordinates, ligatures, positions);
                        }

                        break;
                    }

                // Format 2: RangeRecord { uint16 startGlyphID, uint16 endGlyphID, uint16 startCoverageIndex }[rangeCount]
                case 2:
                    {
                        for (var i = 0; i < coverageCount; i++)
                        {
                            var record = coverage + 4 + 6 * i;

                            if (!TryReadUInt16(span, record, out var start) ||
                                !TryReadUInt16(span, record + 2, out var end) ||
                                !TryReadUInt16(span, record + 4, out var startCoverageIndex))
                            {
                                break;
                            }

                            // Glyphs whose coverage index has no LigGlyph table carry no carets.
                            var count = Math.Min(end - start + 1, ligGlyphCount - startCoverageIndex);

                            for (var j = 0; j < count; j++)
                            {
                                AddLigature(span, (ushort)(start + j), startCoverageIndex + j, variationCoordinates,
                                    ligatures, positions);
                            }
                        }

                        break;
                    }
            }

            if (ligatures.Count == 0)
            {
                return LigatureCaretList.Empty;
            }

            // Coverage tables are sorted by glyph, but malformed ones are not trusted to be. A glyph listed
            // twice keeps its first carets.
            ligatures.Sort((x, y) => x.Glyph != y.Glyph ? x.Glyph.CompareTo(y.Glyph) : x.Start.CompareTo(y.Start));

            var glyphs = new List<ushort>(ligatures.Count);
            var starts = new List<int>(ligatures.Count + 1);
            var packed = new int[positions.Count];
            var packedCount = 0;

            foreach (var (glyph, start, count) in ligatures)
            {
                if (glyphs.Count > 0 && glyphs[glyphs.Count - 1] == glyph)
                {
                    continue;
                }

                glyphs.Add(glyph);
                starts.Add(packedCount);
                positions.CopyTo(start, packed, packedCount, count);
                packedCount += count;
            }

            starts.Add(packedCount);

            return new LigatureCaretList(glyphs.ToArray(), starts.ToArray(), packed.AsSpan(0, packedCount).ToArray());
        }

        private void AddLigature(ReadOnlySpan<byte> span, ushort glyph, int coverageIndex,
            ReadOnlySpan<float> variationCoordinates, List<(ushort Glyph, int Start, int Count)> ligatures,
            List<int> positions)
        {
            var listOffset = _ligCaretListOffset;

            if (!TryReadUInt16(span, listOffset + 4 + 2 * coverageIndex, out var ligGlyphOffset))
            {
                return;
            }

            // LigGlyph: uint16 caretCount, Offset16 caretValueOffsets[caretCount], relative to the LigGlyph
            var ligGlyph = listOffset + ligGlyphOffset;

            if (!TryReadUInt16(span, ligGlyph, out var caretCount) || caretCount == 0)
            {
                return;
            }

            var start = positions.Count;

            for (var i = 0; i < caretCount; i++)
            {
                if (!TryReadUInt16(span, ligGlyph + 2 + 2 * i, out var caretValueOffset) ||
                    !TryReadCaretValue(span, ligGlyph + caretValueOffset, variationCoordinates, out var caret))
                {
                    positions.RemoveRange(start, positions.Count - start);

                    return;
                }

                positions.Add(caret);
            }

            // The specification lists carets in increasing order; sort for fonts that do not.
            positions.Sort(start, caretCount, null);

            ligatures.Add((glyph, start, caretCount));
        }

        private bool TryReadCaretValue(ReadOnlySpan<byte> span, int offset, ReadOnlySpan<float> variationCoordinates,
            out int coordinate)
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
                // VariationIndex table adds the delta of the variable font instance.
                case 1:
                case 3:
                    {
                        if (!TryReadUInt16(span, offset + 2, out var value))
                        {
                            return false;
                        }

                        coordinate = (short)value;

                        if (format == 3 && !variationCoordinates.IsEmpty &&
                            TryReadUInt16(span, offset + 4, out var deviceOffset) && deviceOffset != 0)
                        {
                            coordinate += GetVariationDelta(span, offset + deviceOffset, variationCoordinates);
                        }

                        return true;
                    }

                // Format 2 names an outline contour point, which needs the glyph outline to resolve.
                // Treating it as unresolvable lets callers divide the ligature evenly instead.
                default:
                    return false;
            }
        }

        // VariationIndex table: uint16 deltaSetOuterIndex, uint16 deltaSetInnerIndex, uint16 deltaFormat 0x8000.
        private int GetVariationDelta(ReadOnlySpan<byte> span, int offset, ReadOnlySpan<float> variationCoordinates)
        {
            const ushort VariationIndexFormat = 0x8000;

            if (!TryReadUInt16(span, offset, out var outerIndex) ||
                !TryReadUInt16(span, offset + 2, out var innerIndex) ||
                !TryReadUInt16(span, offset + 4, out var deltaFormat) || deltaFormat != VariationIndexFormat)
            {
                return 0;
            }

            var store = GetItemVariationStore(variationCoordinates.Length);

            if (store is null || !store.TryGetDelta(outerIndex, innerIndex, variationCoordinates, out var delta))
            {
                return 0;
            }

            return (int)MathF.Round(delta, MidpointRounding.AwayFromZero);
        }

        private ItemVariationStore? GetItemVariationStore(int axisCount)
        {
            var store = _itemVariationStore;

            if (store is not null || _itemVariationStoreInvalid)
            {
                return store;
            }

            if (_itemVariationStoreOffset == 0 || _itemVariationStoreOffset >= (uint)_table.Length ||
                !ItemVariationStore.TryLoad(_table.Slice((int)_itemVariationStoreOffset), axisCount, out store))
            {
                _itemVariationStoreInvalid = true;

                return null;
            }

            _itemVariationStore = store;

            return store;
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
