using System;
using System.Buffers.Binary;
using Avalonia.Media.Fonts.Tables.Variation;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Characterization tests for the font-table robustness issues in the GlyphTypeface parsers,
    /// using the <see cref="SyntheticFont"/> / <see cref="BigEndianBuffer"/> harness.
    /// </summary>
    /// <remarks>
    /// Each test asserts the <b>current</b> behaviour and documents the contract it should meet plus
    /// the one-line change to flip once the fix lands, so it becomes a regression guard that goes red
    /// exactly when the bug is fixed (prompting the assertion flip).
    /// </remarks>
    public class MalformedFontCharacterizationTests
    {
        // ── (int)-cast uint32 offsets slip additive length guards ──

        [Fact]
        public void ItemVariationStore_TryLoad_Returns_False_On_Hostile_RegionListOffset()
        {
            // An ItemVariationStore header: format(uint16), regionListOffset(uint32), ivdCount(uint16).
            // regionListOffset = 0xFFFFFFFF previously cast to int -1, slipped the additive length
            // guard, and threw on span.Slice(-1).
            var ivs = new BigEndianBuffer()
                .UInt16(1)            // format = 1 (the only supported format)
                .UInt32(0xFFFFFFFF)   // regionListOffset (hostile)
                .UInt16(0)            // ivdCount = 0
                .ToArray();

            var loaded = true;
            var exception = Record.Exception(() => loaded = ItemVariationStore.TryLoad(ivs, expectedAxisCount: 2, out _));

            // Now validated as unsigned / range-safe: TryLoad returns false instead of throwing.
            Assert.Null(exception);
            Assert.False(loaded);
        }

        [Fact]
        public void Corrupt_Hvar_VariationStore_Does_Not_Deny_The_Font()
        {
            // Drive the same input through the real load path: corrupt the regionListOffset *inside*
            // InterVariable's HVAR ItemVariationStore. The GlyphTypeface constructor loads HVAR eagerly.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterVariable);

            var hvar = font.GetTable("HVAR");
            // HVAR header: majorVersion(2), minorVersion(2), itemVariationStoreOffset(4), ...
            var ivsOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(hvar.AsSpan(4));

            // regionListOffset is the uint32 at offset +2 within the ItemVariationStore.
            font.PatchUInt32("HVAR", ivsOffset + 2, 0xFFFFFFFF);

            var typeface = font.TryCreateGlyphTypeface();

            // A malformed HVAR now degrades to "no advance variation" instead of denying the font.
            Assert.NotNull(typeface);
        }

        [Fact]
        public void Truncated_Avar_Segment_Map_Does_Not_Deny_The_Font()
        {
            // The avar header is 6 bytes (major/minor version + axisCount). Keeping 8 leaves the
            // first segment map's positionMapCount readable but cuts its value-pair array (and the
            // next map), so the read loop would over-run AvarTable's reader.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterVariable).Truncate("avar", 8);

            var typeface = font.TryCreateGlyphTypeface();

            // AvarTable.TryLoad now bounds its reads (try/catch), so a malformed optional avar degrades
            // to identity normalization instead of denying the whole font.
            Assert.NotNull(typeface);
        }

        [Fact]
        public void FvarTable_Rejects_An_Unclamped_AxisCount()
        {
            // No real font exceeds ~64 variation axes. An unclamped axisCount flows into three
            // `stackalloc float[axisCount]` buffers in GlyphVariationReader — an uncatchable
            // StackOverflow (DoS) on a hostile variable font declaring thousands of axes.
            const ushort axisCount = 2048;
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("fvar", BuildFvar(axisCount));

            var typeface = font.TryCreateGlyphTypeface();

            // fvar now rejects an implausibly large axisCount (> MaxAxisCount), so the font loads as
            // static (no variation axes) rather than feeding the gvar stackalloc.
            Assert.NotNull(typeface);
            Assert.Empty(typeface!.VariationAxes);
        }

        // ── hmtx accessors throw at *layout* time on a degenerate metric count ──
        //
        // Unlike the other T1 cases (which fail at load), these surface at runtime: the
        // GlyphTypeface constructor stores the counts without touching the per-glyph arrays, so
        // the throw only fires when text layout later queries an advance.

        [Fact]
        public void Zero_NumberOfHMetrics_Does_Not_Throw_From_Advance_Lookup()
        {
            // hhea.numberOfHMetrics is the last uint16, at offset 34. Zero previously made the "repeat
            // last advance" math compute (numberOfHMetrics - 1) * 4 = -4 → a negative slice/seek.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).PatchUInt16("hhea", 34, 0);

            var typeface = font.TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            // The accessor now treats a zero metric count as "no horizontal metrics": it returns false
            // rather than throwing at layout time.
            var advance = true;
            var exception = Record.Exception(() => advance = typeface!.TryGetHorizontalGlyphAdvance(3, out _));
            Assert.Null(exception);
            Assert.False(advance);
        }

        [Fact]
        public void Truncated_Hmtx_Does_Not_Throw_From_Advance_Lookup()
        {
            // numberOfHMetrics is unchanged (hundreds) but hmtx is cut to a single metric, so a
            // lookup of a later glyph would slice past the table.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).Truncate("hmtx", 4);

            var typeface = font.TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            var glyph = (ushort)(typeface!.GlyphCount - 1); // well past the surviving metric

            // numberOfHMetrics is clamped to what the table holds, so the lookup reads in range and
            // returns without throwing.
            Assert.Null(Record.Exception(() => typeface.TryGetHorizontalGlyphAdvance(glyph, out _)));
        }

        // ── present-but-malformed cosmetic tables must not deny the font ──
        //
        // A *missing* 'post' / 'name' table is handled gracefully (see the two "Missing_*" tests
        // below), and so is a *present-but-malformed* (truncated) one: the loader isolates the
        // over-reading parse and degrades to the same fallback as an absent table.

        [Fact]
        public void Truncated_Post_Table_Does_Not_Deny_The_Font()
        {
            // Keep only the 4-byte version field; reading the rest of the header over-runs.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).Truncate("post", 4);

            var typeface = font.TryCreateGlyphTypeface();

            // A malformed 'post' (underline / italic-angle / fixed-pitch hints only) degrades to
            // defaults; the rest of the font — including the intact 'name' — still loads.
            Assert.NotNull(typeface);
            Assert.Equal("Inter", typeface!.FamilyName);
        }

        [Fact]
        public void Truncated_Name_Table_Falls_Back_To_Unknown_Family()
        {
            // Keep only format + count; reading stringOffset and the record array over-runs.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).Truncate("name", 4);

            var typeface = font.TryCreateGlyphTypeface();

            // A malformed 'name' degrades to no name table, so the family name falls back to "unknown"
            // instead of denying a renderable font.
            Assert.NotNull(typeface);
            Assert.Equal("unknown", typeface!.FamilyName);
        }

        [Fact]
        public void Missing_Post_Table_Still_Loads_The_Font()
        {
            // Documents the already-correct path: an *absent* cosmetic table is tolerated.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).Remove("post");

            var typeface = font.TryCreateGlyphTypeface();

            Assert.NotNull(typeface);
            Assert.Equal("Inter", typeface!.FamilyName);
        }

        [Fact]
        public void Missing_Name_Table_Falls_Back_To_Unknown_Family()
        {
            // Documents the already-correct path: an *absent* name table yields a usable typeface
            // with a fallback family name.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular).Remove("name");

            var typeface = font.TryCreateGlyphTypeface();

            Assert.NotNull(typeface);
            Assert.Equal("unknown", typeface!.FamilyName);
        }

        // ── cmap format 12 nGroups*12 overflow denies the whole font ──

        [Fact]
        public void Cmap_Format12_NGroups_Overflow_Does_Not_Deny_The_Font()
        {
            // numGroups = 0x20000000 would make `numGroups * 12` overflow int32 to a negative slice
            // length and throw out of the (throwing) CmapTable.Load.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("cmap", BuildCmapWithOverflowingFormat12(numGroups: 0x20000000));

            var typeface = font.TryCreateGlyphTypeface();

            // The group count is now computed in long and clamped to what the table holds, so the bad
            // subtable yields empty coverage rather than throwing — the font still loads.
            Assert.NotNull(typeface);
        }

        // ── cmap Format-4 subtable selection prefers Unicode over Symbol ──

        [Theory]
        [InlineData(true)]   // Symbol subtable listed first
        [InlineData(false)]  // Unicode subtable listed first
        public void Format4_Subtable_Selection_Prefers_Unicode_Over_Symbol(bool symbolFirst)
        {
            // Two Windows-platform Format-4 subtables: a Symbol (encoding 0) one that maps only the
            // PUA codepoint 0xF041, and a Unicode-BMP (encoding 1) one that maps 'A'. The selection
            // now scores the Symbol encoding worse than Unicode, so the Unicode subtable wins
            // regardless of directory order.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("cmap", BuildDualFormat4Cmap(symbolFirst));

            var typeface = font.TryCreateGlyphTypeface();
            Assert.NotNull(typeface);

            // ASCII 'A' now resolves in BOTH orderings — encoding, not directory order, decides.
            Assert.True(typeface!.CharacterToGlyphMap.ContainsGlyph('A'));
        }

        /// <summary>Builds a valid fvar table declaring <paramref name="axisCount"/> axes (no instances).</summary>
        private static byte[] BuildFvar(ushort axisCount)
        {
            var fvar = new BigEndianBuffer();

            // fvar header (16 bytes).
            fvar.UInt16(1);          // majorVersion
            fvar.UInt16(0);          // minorVersion
            fvar.UInt16(16);         // axesArrayOffset (axes follow the header)
            fvar.UInt16(2);          // reserved
            fvar.UInt16(axisCount);
            fvar.UInt16(20);         // axisSize
            fvar.UInt16(0);          // instanceCount
            fvar.UInt16(0);          // instanceSize

            // AxisRecord[axisCount]: Tag(4) + min/default/max Fixed(4 each) + flags(2) + axisNameID(2).
            for (var i = 0; i < axisCount; i++)
            {
                fvar.Tag("axis");
                fvar.Fixed(0.0);     // min
                fvar.Fixed(0.0);     // default
                fvar.Fixed(1.0);     // max
                fvar.UInt16(0);      // flags
                fvar.UInt16(0);      // axisNameID
            }

            return fvar.ToArray();
        }

        private static byte[] BuildCmapWithOverflowingFormat12(uint numGroups)
        {
            // Format-12 subtable: format(2) reserved(2) length(4) language(4) numGroups(4) groups[…].
            // length is honest about the 16-byte buffer, so the length-slice succeeds and the
            // overflow surfaces at the group-array slice (the exact path under test).
            var subtable = new BigEndianBuffer()
                .UInt16(12)        // format
                .UInt16(0)         // reserved
                .UInt32(16)        // length (header only)
                .UInt32(0)         // language
                .UInt32(numGroups) // numGroups
                .ToArray();

            // cmap header: version(2) numTables(2), then one EncodingRecord: platform(2) encoding(2) offset(4).
            var cmap = new BigEndianBuffer();
            cmap.UInt16(0);   // version
            cmap.UInt16(1);   // numTables
            cmap.UInt16(3);   // platformID = Windows
            cmap.UInt16(10);  // encodingID = UCS-4 (any value works; format 12 is selected regardless)
            var offsetPos = cmap.ReserveOffset32();
            cmap.PatchUInt32(offsetPos, (uint)cmap.Position);
            cmap.Bytes(subtable);

            return cmap.ToArray();
        }

        /// <summary>
        /// Builds a cmap with two Windows-platform Format-4 subtables — a Symbol (encoding 0) one
        /// mapping the PUA codepoint 0xF041 and a Unicode-BMP (encoding 1) one mapping 'A' — ordered
        /// per <paramref name="symbolFirst"/>.
        /// </summary>
        private static byte[] BuildDualFormat4Cmap(bool symbolFirst)
        {
            var symbol = BuildSingleCharFormat4(charCode: 0xF041, glyph: 7);
            var unicode = BuildSingleCharFormat4(charCode: 'A', glyph: 5);

            var firstSub = symbolFirst ? symbol : unicode;
            var firstEncoding = symbolFirst ? 0 : 1;   // Symbol = 0, UnicodeBMP = 1
            var secondSub = symbolFirst ? unicode : symbol;
            var secondEncoding = symbolFirst ? 1 : 0;

            var cmap = new BigEndianBuffer();
            cmap.UInt16(0);   // version
            cmap.UInt16(2);   // numTables

            // Two 8-byte EncodingRecords follow the 4-byte header, so the subtables start at offset 20.
            const int subtablesStart = 4 + 2 * 8;
            cmap.UInt16(3); cmap.UInt16(firstEncoding); cmap.UInt32(subtablesStart);
            cmap.UInt16(3); cmap.UInt16(secondEncoding); cmap.UInt32((uint)(subtablesStart + firstSub.Length));
            cmap.Bytes(firstSub);
            cmap.Bytes(secondSub);

            return cmap.ToArray();
        }

        /// <summary>Builds a minimal Format-4 cmap subtable mapping a single <paramref name="charCode"/> to <paramref name="glyph"/>.</summary>
        private static byte[] BuildSingleCharFormat4(int charCode, int glyph)
        {
            // Two segments: [charCode, charCode] and the mandatory terminal [0xFFFF, 0xFFFF].
            // No glyphIdArray — the glyph comes from idDelta (idRangeOffset = 0). Total length 32.
            return new BigEndianBuffer()
                .UInt16(4)        // format
                .UInt16(32)       // length
                .UInt16(0)        // language
                .UInt16(4)        // segCountX2 (segCount = 2)
                .UInt16(4)        // searchRange
                .UInt16(1)        // entrySelector
                .UInt16(0)        // rangeShift
                .UInt16(charCode).UInt16(0xFFFF)                 // endCode[2]
                .UInt16(0)                                       // reservedPad
                .UInt16(charCode).UInt16(0xFFFF)                 // startCode[2]
                .UInt16((glyph - charCode) & 0xFFFF).UInt16(1)   // idDelta[2]
                .UInt16(0).UInt16(0)                             // idRangeOffset[2]
                .ToArray();
        }
    }
}
