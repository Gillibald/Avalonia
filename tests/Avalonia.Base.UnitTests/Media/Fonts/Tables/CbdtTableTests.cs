using System;
using System.Linq;
using Avalonia.Media.Fonts.Tables.Bitmaps;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts.Tables
{
    public class CbdtTableTests
    {
        private const string InterAsset = SyntheticFont.Assets.InterRegular;

        [Fact]
        public void Loads_Color_Strikes_From_A_Grafted_Font()
        {
            var table = LoadTable(out _, out _);

            Assert.Equal(2, table.StrikeCount);
        }

        [Fact]
        public void Strike_Selection_Prefers_The_Nearest_At_Or_Above()
        {
            var table = LoadTable(out _, out _);

            Assert.Equal(16, table.SelectStrike(10f).PpemY);
            Assert.Equal(16, table.SelectStrike(16f).PpemY);
            Assert.Equal(64, table.SelectStrike(20f).PpemY);
            Assert.Equal(64, table.SelectStrike(64f).PpemY);
            Assert.Equal(64, table.SelectStrike(100f).PpemY);   // nothing above: largest wins
        }

        [Fact]
        public void Glyph_Image_Round_Trips_Metrics_And_Png_Bytes()
        {
            var table = LoadTable(out var glyph, out _);
            var strike = table.SelectStrike(64f);

            Assert.True(table.TryGetGlyphImage(strike, glyph, out var image));

            Assert.Equal(60, image.Width);
            Assert.Equal(58, image.Height);
            Assert.Equal(2, image.BearingX);
            Assert.Equal(56, image.BearingY);
            Assert.Equal(62, image.Advance);
            Assert.True(image.PngData.Span.SequenceEqual(FakePng(64)));
        }

        [Fact]
        public void Empty_Slots_And_Uncovered_Glyphs_Report_No_Image()
        {
            var table = LoadTable(out var glyph, out var emptyGlyph);
            var strike = table.SelectStrike(64f);

            // The neighbouring glyph has a zero-length slot in the 64px strike.
            Assert.False(table.TryGetGlyphImage(strike, emptyGlyph, out _));

            // A glyph far outside every subtable range.
            Assert.False(table.TryGetGlyphImage(strike, (ushort)(glyph + 1000), out _));
        }

        [Fact]
        public void Truncated_Tables_Fail_To_Load_Or_Degrade_Gracefully()
        {
            var font = BuildFont(out _, out _);

            // CBLC cut mid-records: the load rejects it, the typeface still builds.
            var truncated = font.Truncate("CBLC", 40).TryCreateGlyphTypeface();
            Assert.NotNull(truncated);
            Assert.Null(truncated!.BitmapTable);
        }

        private static CbdtTable LoadTable(out ushort glyph, out ushort emptyGlyph)
        {
            var typeface = BuildFont(out glyph, out emptyGlyph).TryCreateGlyphTypeface();
            Assert.NotNull(typeface);
            Assert.NotNull(typeface!.BitmapTable);
            return typeface.BitmapTable!;
        }

        private static SyntheticFont BuildFont(out ushort glyph, out ushort emptyGlyph)
        {
            var font = SyntheticFont.FromAsset(InterAsset);
            var probe = font.TryCreateGlyphTypeface();
            Assert.NotNull(probe);

            glyph = probe!.CharacterToGlyphMap['A'];
            emptyGlyph = (ushort)(glyph + 1);

            BuildStrikeTables(glyph, out var cblc, out var cbdt);

            return font.Replace("CBLC", cblc).Replace("CBDT", cbdt);
        }

        private static byte[] FakePng(int marker)
            => new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', (byte)marker, 1, 2, 3 };

        /// <summary>
        /// Two 32-bit strikes (16 and 64 ppem), each one index subtable in format 1 with image
        /// format 17. The 64px strike covers [glyph, glyph+1] with the second slot empty.
        /// </summary>
        private static void BuildStrikeTables(ushort glyph, out byte[] cblc, out byte[] cbdt)
        {
            var png16 = FakePng(16);
            var png64 = FakePng(64);

            // CBDT: header, then format-17 entries (5B metrics + length + png).
            var cbdtBuffer = new BigEndianBuffer();
            cbdtBuffer.UInt16(3).UInt16(0);

            var image16Offset = 4;
            cbdtBuffer.UInt8(14).UInt8(15).Int8(1).Int8(14).UInt8(16);
            cbdtBuffer.UInt32((uint)png16.Length);
            foreach (var b in png16) cbdtBuffer.UInt8(b);
            var image16Length = 5 + 4 + png16.Length;

            var image64Offset = image16Offset + image16Length;
            cbdtBuffer.UInt8(58).UInt8(60).Int8(2).Int8(56).UInt8(62);
            cbdtBuffer.UInt32((uint)png64.Length);
            foreach (var b in png64) cbdtBuffer.UInt8(b);
            var image64Length = 5 + 4 + png64.Length;

            cbdt = cbdtBuffer.ToArray();

            // CBLC: header + 2 records + per-strike (array entry + header + offsets).
            // Strike 0 (16px): covers [glyph, glyph], format-1 offsets = 2 entries.
            // Strike 1 (64px): covers [glyph, glyph+1], 3 entries, second slot empty.
            const int header = 8;
            const int records = 2 * 48;
            var array0 = header + records;
            var sub0 = 8 + 8 + 8;          // entry + subheader + 2 offsets
            var array1 = array0 + sub0;
            var sub1 = 8 + 8 + 12;         // entry + subheader + 3 offsets

            var cblcBuffer = new BigEndianBuffer();
            cblcBuffer.UInt16(3).UInt16(0).UInt32(2);

            WriteBitmapSizeRecord(cblcBuffer, (uint)array0, (uint)sub0, glyph, glyph, 16);
            WriteBitmapSizeRecord(cblcBuffer, (uint)array1, (uint)sub1, glyph, (ushort)(glyph + 1), 64);

            // Strike 0.
            cblcBuffer.UInt16(glyph).UInt16(glyph).UInt32(8);
            cblcBuffer.UInt16(1).UInt16(17).UInt32((uint)image16Offset);
            cblcBuffer.UInt32(0).UInt32((uint)image16Length);

            // Strike 1: [0, len, len] — the second glyph's slot is empty.
            cblcBuffer.UInt16(glyph).UInt16((ushort)(glyph + 1)).UInt32(8);
            cblcBuffer.UInt16(1).UInt16(17).UInt32((uint)image64Offset);
            cblcBuffer.UInt32(0).UInt32((uint)image64Length).UInt32((uint)image64Length);

            cblc = cblcBuffer.ToArray();
        }

        private static void WriteBitmapSizeRecord(BigEndianBuffer buffer, uint arrayOffset,
            uint tablesSize, ushort startGlyph, ushort endGlyph, byte ppem)
        {
            buffer.UInt32(arrayOffset).UInt32(tablesSize).UInt32(1).UInt32(0);

            for (var i = 0; i < 24; i++)
            {
                buffer.UInt8(0);   // hori + vert line metrics, unused by the parser
            }

            buffer.UInt16(startGlyph).UInt16(endGlyph);
            buffer.UInt8(ppem).UInt8(ppem).UInt8(32).Int8(1);
        }
    }
}
