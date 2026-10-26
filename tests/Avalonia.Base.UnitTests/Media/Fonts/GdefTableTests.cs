using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Fonts.Tables;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// The ligature caret list of hand-built GDEF tables, covering the coverage and caret value
    /// formats the bundled test fonts do not use.
    /// </summary>
    public class GdefTableTests
    {
        [Fact]
        public void LigCaretList_Format1_Returns_Coordinates()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(5, 9), Lig(Caret1(100), Caret1(200)), Lig(Caret1(50)));

            Assert.Equal(new[] { 100, 200 }, Carets(gdef, 5));
            Assert.Equal(new[] { 50 }, Carets(gdef, 9));
            Assert.Empty(Carets(gdef, 7));
        }

        [Fact]
        public void Coverage_Format2_Resolves_Glyph()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat2(10, 12), Lig(Caret1(10)), Lig(Caret1(20), Caret1(40)), Lig(Caret1(30)));

            Assert.Equal(new[] { 10 }, Carets(gdef, 10));
            Assert.Equal(new[] { 20, 40 }, Carets(gdef, 11));
            Assert.Equal(new[] { 30 }, Carets(gdef, 12));
            Assert.Empty(Carets(gdef, 13));
        }

        [Fact]
        public void CaretValue_Format3_Ignores_Device_Table()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(3), Lig(Caret3Device(300), Caret3VariationIndex(450)));

            Assert.Equal(new[] { 300, 450 }, Carets(gdef, 3));
        }

        [Fact]
        public void CaretValue_Format3_Applies_Variation_Delta()
        {
            using var app = UnitTestApplication.Start();

            // One axis, one region peaking at +1.0, a delta of +40 units for the caret's VariationIndex.
            var gdef = BuildGdef(CoverageFormat1(3), new[] { Lig(Caret1(100), Caret3VariationIndex(450)) },
                majorVersion: 1, minorVersion: 3, itemVariationStore: SingleRegionVariationStore(40));
            var table = GdefTable.TryParse(gdef);

            Assert.NotNull(table);

            Assert.Equal(new[] { 100, 490 }, table!.DecodeLigatureCarets(new[] { 1f }).GetCarets(3).ToArray());
            Assert.Equal(new[] { 100, 470 }, table.DecodeLigatureCarets(new[] { 0.5f }).GetCarets(3).ToArray());
            Assert.Equal(new[] { 100, 450 }, table.DecodeLigatureCarets().GetCarets(3).ToArray());
        }

        [Fact]
        public void Unresolvable_Contour_Point_Returns_No_Carets()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(4, 6), Lig(Caret1(100), Caret2(3)), Lig(Caret1(70)));

            Assert.Empty(Carets(gdef, 4));
            Assert.Equal(new[] { 70 }, Carets(gdef, 6));
        }

        [Fact]
        public void Truncated_Table_Returns_No_Carets()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(5), Lig(Caret1(100), Caret1(200)));

            for (var length = 0; length < gdef.Length; length++)
            {
                Assert.Empty(Carets(gdef.AsSpan(0, length).ToArray(), 5));
            }

            Assert.Equal(new[] { 100, 200 }, Carets(gdef, 5));
        }

        [Fact]
        public void Carets_Out_Of_Order_Are_Returned_Ascending()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(5), Lig(Caret1(400), Caret1(-20), Caret1(150)));

            Assert.Equal(new[] { -20, 150, 400 }, Carets(gdef, 5));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void Header_Version_1_2_And_1_3_Are_Read(int minorVersion)
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(5), new[] { Lig(Caret1(100)) }, majorVersion: 1, minorVersion: minorVersion);

            Assert.Equal(new[] { 100 }, Carets(gdef, 5));
        }

        [Fact]
        public void Unknown_Major_Version_Returns_No_Carets()
        {
            using var app = UnitTestApplication.Start();

            var gdef = BuildGdef(CoverageFormat1(5), new[] { Lig(Caret1(100)) }, majorVersion: 2, minorVersion: 0);

            Assert.Empty(Carets(gdef, 5));
        }

        [Fact]
        public void Table_Without_LigCaretList_Returns_No_Carets()
        {
            using var app = UnitTestApplication.Start();

            var gdef = new byte[12];
            gdef[1] = 1;

            Assert.Empty(Carets(gdef, 5));
        }

        private static int[] Carets(byte[] gdef, ushort glyph)
        {
            var table = GdefTable.TryParse(gdef);

            if (table is null)
            {
                return Array.Empty<int>();
            }

            return table.DecodeLigatureCarets().GetCarets(glyph).ToArray();
        }

        private static byte[] BuildGdef(byte[] coverage, params byte[][] ligGlyphs)
            => BuildGdef(coverage, ligGlyphs, majorVersion: 1, minorVersion: 0);

        // Lays out the header, the LigCaretList, its coverage and its LigGlyph tables in that order.
        private static byte[] BuildGdef(byte[] coverage, byte[][] ligGlyphs, int majorVersion, int minorVersion,
            byte[]? itemVariationStore = null)
        {
            var headerSize = minorVersion switch { >= 3 => 18, 2 => 14, _ => 12 };

            var ligCaretListHeader = 4 + 2 * ligGlyphs.Length;
            var coverageOffset = ligCaretListHeader;
            var ligGlyphOffset = coverageOffset + coverage.Length;

            var bytes = new List<byte>();

            WriteUInt16(bytes, majorVersion);
            WriteUInt16(bytes, minorVersion);
            WriteUInt16(bytes, 0); // glyphClassDefOffset
            WriteUInt16(bytes, 0); // attachListOffset
            WriteUInt16(bytes, headerSize); // ligCaretListOffset
            WriteUInt16(bytes, 0); // markAttachClassDefOffset

            if (minorVersion >= 2)
            {
                WriteUInt16(bytes, 0); // markGlyphSetsDefOffset
            }

            if (minorVersion >= 3)
            {
                // itemVarStoreOffset (Offset32): the store follows the LigCaretList.
                var storeOffset = itemVariationStore is null
                    ? 0
                    : headerSize + ligGlyphOffset + ligGlyphs.Sum(x => x.Length);

                WriteUInt16(bytes, storeOffset >> 16);
                WriteUInt16(bytes, storeOffset & 0xFFFF);
            }

            WriteUInt16(bytes, coverageOffset);
            WriteUInt16(bytes, ligGlyphs.Length);

            var offset = ligGlyphOffset;

            foreach (var ligGlyph in ligGlyphs)
            {
                WriteUInt16(bytes, offset);
                offset += ligGlyph.Length;
            }

            bytes.AddRange(coverage);

            foreach (var ligGlyph in ligGlyphs)
            {
                bytes.AddRange(ligGlyph);
            }

            if (itemVariationStore is not null)
            {
                bytes.AddRange(itemVariationStore);
            }

            return bytes.ToArray();
        }

        // An ItemVariationStore with one axis, one region (start 0, peak 1, end 1) and one
        // ItemVariationData holding a single 16-bit delta at outer 0, inner 0.
        private static byte[] SingleRegionVariationStore(short delta)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 1); // format
            WriteUInt16(bytes, 0); // variationRegionListOffset (Offset32) = 12
            WriteUInt16(bytes, 12);
            WriteUInt16(bytes, 1); // itemVariationDataCount
            WriteUInt16(bytes, 0); // itemVariationDataOffsets[0] (Offset32) = 22
            WriteUInt16(bytes, 22);

            WriteUInt16(bytes, 1); // axisCount
            WriteUInt16(bytes, 1); // regionCount
            WriteUInt16(bytes, 0); // startCoord F2DOT14 0.0
            WriteUInt16(bytes, 0x4000); // peakCoord 1.0
            WriteUInt16(bytes, 0x4000); // endCoord 1.0

            WriteUInt16(bytes, 1); // itemCount
            WriteUInt16(bytes, 1); // wordDeltaCount
            WriteUInt16(bytes, 1); // regionIndexCount
            WriteUInt16(bytes, 0); // regionIndexes[0]
            WriteUInt16(bytes, (ushort)delta);

            return bytes.ToArray();
        }

        private static byte[] CoverageFormat1(params ushort[] glyphs)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 1);
            WriteUInt16(bytes, glyphs.Length);

            foreach (var glyph in glyphs)
            {
                WriteUInt16(bytes, glyph);
            }

            return bytes.ToArray();
        }

        private static byte[] CoverageFormat2(ushort startGlyph, ushort endGlyph)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 2);
            WriteUInt16(bytes, 1);
            WriteUInt16(bytes, startGlyph);
            WriteUInt16(bytes, endGlyph);
            WriteUInt16(bytes, 0);

            return bytes.ToArray();
        }

        // A LigGlyph table followed by its caret values; offsets are relative to the LigGlyph table.
        private static byte[] Lig(params byte[][] caretValues)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, caretValues.Length);

            var offset = 2 + 2 * caretValues.Length;

            foreach (var caretValue in caretValues)
            {
                WriteUInt16(bytes, offset);
                offset += caretValue.Length;
            }

            foreach (var caretValue in caretValues)
            {
                bytes.AddRange(caretValue);
            }

            return bytes.ToArray();
        }

        private static byte[] Caret1(short coordinate)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 1);
            WriteUInt16(bytes, (ushort)coordinate);

            return bytes.ToArray();
        }

        private static byte[] Caret2(ushort pointIndex)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 2);
            WriteUInt16(bytes, pointIndex);

            return bytes.ToArray();
        }

        // Format 3 with a ppem Device table (deltaFormat 1) that would add 2 units at 12 ppem.
        private static byte[] Caret3Device(short coordinate)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 3);
            WriteUInt16(bytes, (ushort)coordinate);
            WriteUInt16(bytes, 6); // deviceOffset
            WriteUInt16(bytes, 12); // startSize
            WriteUInt16(bytes, 12); // endSize
            WriteUInt16(bytes, 1); // deltaFormat
            WriteUInt16(bytes, 0x2000); // one 2-bit delta of +2

            return bytes.ToArray();
        }

        // Format 3 with a VariationIndex table; without variation coordinates it adds nothing.
        private static byte[] Caret3VariationIndex(short coordinate)
        {
            var bytes = new List<byte>();

            WriteUInt16(bytes, 3);
            WriteUInt16(bytes, (ushort)coordinate);
            WriteUInt16(bytes, 6); // deviceOffset
            WriteUInt16(bytes, 0); // deltaSetOuterIndex
            WriteUInt16(bytes, 0); // deltaSetInnerIndex
            WriteUInt16(bytes, 0x8000); // deltaFormat VARIATION_INDEX

            return bytes.ToArray();
        }

        private static void WriteUInt16(List<byte> bytes, int value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }
    }
}
