using Avalonia.Media;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Fonts such as Symbol and Wingdings carry only a Windows Symbol (3,0) cmap subtable whose
    /// code points live in U+F020..U+F0FF. Text written against them uses the Latin-1 code points
    /// U+0020..U+00FF, which must resolve to the same glyphs.
    /// </summary>
    public class SymbolCmapTests
    {
        internal const int FirstSymbolCode = 0xF020;
        internal const int LastSymbolCode = 0xF0FF;
        internal const int FirstSymbolGlyph = 3;

        [Fact]
        public void Latin1_Code_Point_Resolves_To_The_Symbol_Glyph()
        {
            var map = CreateSymbolTypeface().CharacterToGlyphMap;

            Assert.Equal(FirstSymbolGlyph, map[0xF020]);
            Assert.Equal(map[0xF020], map[' ']);
            Assert.Equal(map[0xF0B7], map[0xB7]);
            Assert.Equal(map[0xF0B7], map.GetGlyph(0xB7));
            Assert.True(map.ContainsGlyph(' '));
            Assert.True(map.TryGetGlyph(0xB7, out var glyph));
            Assert.Equal(map[0xF0B7], glyph);
        }

        [Fact]
        public void Bulk_Lookup_Resolves_Latin1_Code_Points()
        {
            var map = CreateSymbolTypeface().CharacterToGlyphMap;

            var codePoints = new[] { 'A', 0xF041, ' ', 0x2022 };
            var glyphs = new ushort[codePoints.Length];

            map.GetGlyphs(codePoints, glyphs);

            Assert.Equal(map[0xF041], glyphs[0]);
            Assert.Equal(map[0xF041], glyphs[1]);
            Assert.Equal(map[0xF020], glyphs[2]);
            Assert.Equal(0, glyphs[3]);
        }

        [Theory]
        [InlineData(0x09)]   // control characters sit below the symbol range
        [InlineData(0x1F)]
        [InlineData(0x100)]  // above Latin-1
        [InlineData(0x2022)]
        public void Code_Points_Outside_Latin1_Printable_Range_Are_Not_Remapped(int codePoint)
        {
            var map = CreateSymbolTypeface().CharacterToGlyphMap;

            Assert.Equal(0, map[codePoint]);
            Assert.False(map.ContainsGlyph(codePoint));
            Assert.False(map.TryGetGlyph(codePoint, out _));
        }

        [Fact]
        public void Unicode_Subtable_Does_Not_Remap_Into_The_Symbol_Range()
        {
            // A Unicode (3,1) subtable that happens to map U+F041 must not make 'A' resolve to it.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("cmap", BuildRangeCmap(encodingId: 1, 0xF041, 0xF041, FirstSymbolGlyph));

            var map = font.TryCreateGlyphTypeface()!.CharacterToGlyphMap;

            Assert.Equal(FirstSymbolGlyph, map[0xF041]);
            Assert.Equal(0, map['A']);
            Assert.False(map.ContainsGlyph('A'));
        }

        [Fact]
        public void Symbol_Page_Comes_From_The_High_Byte_Of_First_Char_Index()
        {
            // Like Marlett: no space glyph, so usFirstCharIndex is U+F030, yet '0' is still encoded at U+F030.
            var map = CreateSymbolTypeface(0xF030, 0xF0FF).CharacterToGlyphMap;

            Assert.Equal(FirstSymbolGlyph, map[0xF030]);
            Assert.Equal(map[0xF030], map['0']);
            Assert.Equal(map[0xF072], map['r']);
            Assert.Equal(0, map[' ']);
            Assert.False(map.ContainsGlyph(' '));
        }

        [Fact]
        public void Symbol_Glyphs_In_Another_Page_Resolve_From_That_Page()
        {
            var map = CreateSymbolTypeface(0xF120, 0xF1FF).CharacterToGlyphMap;

            Assert.Equal(FirstSymbolGlyph, map[0xF120]);
            Assert.Equal(map[0xF120], map[' ']);
            Assert.Equal(map[0xF141], map['A']);
            Assert.True(map.TryGetGlyph(0xB7, out var glyph));
            Assert.Equal(map[0xF1B7], glyph);
        }

        [Fact]
        public void Symbol_Font_Without_OS2_Table_Resolves_From_The_F000_Page()
        {
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("cmap", BuildRangeCmap(encodingId: 0, FirstSymbolCode, LastSymbolCode, FirstSymbolGlyph))
                .Remove("OS/2");

            var map = font.TryCreateGlyphTypeface()!.CharacterToGlyphMap;

            Assert.Equal(FirstSymbolGlyph, map[' ']);
            Assert.Equal(map[0xF041], map['A']);
        }

        internal static GlyphTypeface CreateSymbolTypeface() => CreateSymbolTypeface(FirstSymbolCode, LastSymbolCode);

        private static GlyphTypeface CreateSymbolTypeface(int firstCode, int lastCode)
        {
            // usFirstCharIndex sits at offset 64 of the OS/2 table and is set to the lowest mapped code,
            // as symbol fonts do.
            var font = SyntheticFont.FromAsset(SyntheticFont.Assets.InterRegular)
                .Replace("cmap", BuildRangeCmap(encodingId: 0, firstCode, lastCode, FirstSymbolGlyph))
                .PatchUInt16("OS/2", 64, (ushort)firstCode);

            var typeface = font.TryCreateGlyphTypeface();

            Assert.NotNull(typeface);

            return typeface!;
        }

        /// <summary>
        /// Builds a cmap with a single Windows-platform Format-4 subtable mapping
        /// [<paramref name="firstCode"/>, <paramref name="lastCode"/>] to consecutive glyphs
        /// starting at <paramref name="firstGlyph"/>.
        /// </summary>
        private static byte[] BuildRangeCmap(int encodingId, int firstCode, int lastCode, int firstGlyph)
        {
            // Two segments: the range and the mandatory terminal [0xFFFF, 0xFFFF]. Glyphs come
            // from idDelta (idRangeOffset = 0), so there is no glyphIdArray. Total length 32.
            var subtable = new BigEndianBuffer()
                .UInt16(4)        // format
                .UInt16(32)       // length
                .UInt16(0)        // language
                .UInt16(4)        // segCountX2 (segCount = 2)
                .UInt16(4)        // searchRange
                .UInt16(1)        // entrySelector
                .UInt16(0)        // rangeShift
                .UInt16(lastCode).UInt16(0xFFFF)                    // endCode[2]
                .UInt16(0)                                          // reservedPad
                .UInt16(firstCode).UInt16(0xFFFF)                   // startCode[2]
                .UInt16((firstGlyph - firstCode) & 0xFFFF).UInt16(1) // idDelta[2]
                .UInt16(0).UInt16(0)                                // idRangeOffset[2]
                .ToArray();

            var cmap = new BigEndianBuffer();
            cmap.UInt16(0);                  // version
            cmap.UInt16(1);                  // numTables
            cmap.UInt16(3);                  // platformID = Windows
            cmap.UInt16((ushort)encodingId); // 0 = Symbol, 1 = Unicode BMP
            cmap.UInt32(12);                 // offset: header(4) + one EncodingRecord(8)
            cmap.Bytes(subtable);

            return cmap.ToArray();
        }
    }
}
