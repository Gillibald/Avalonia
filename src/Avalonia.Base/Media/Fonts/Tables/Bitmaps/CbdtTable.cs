using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Media.Fonts.Tables.Bitmaps
{
    /// <summary>A color bitmap strike: one fixed ppem size carried by CBLC/CBDT.</summary>
    internal readonly struct BitmapStrike
    {
        public BitmapStrike(int index, byte ppemX, byte ppemY)
        {
            Index = index;
            PpemX = ppemX;
            PpemY = ppemY;
        }

        public int Index { get; }
        public byte PpemX { get; }
        public byte PpemY { get; }
    }

    /// <summary>
    /// One glyph's bitmap image within a strike: encoded PNG bytes plus the small metrics the
    /// strike stores alongside (bearings are in strike pixels, y-up from the pen like the spec).
    /// </summary>
    internal readonly struct BitmapGlyphImage
    {
        public BitmapGlyphImage(ReadOnlyMemory<byte> pngData, byte width, byte height,
            sbyte bearingX, sbyte bearingY, byte advance)
        {
            PngData = pngData;
            Width = width;
            Height = height;
            BearingX = bearingX;
            BearingY = bearingY;
            Advance = advance;
        }

        public ReadOnlyMemory<byte> PngData { get; }
        public byte Width { get; }
        public byte Height { get; }
        public sbyte BearingX { get; }
        public sbyte BearingY { get; }
        public byte Advance { get; }
    }

    /// <summary>
    /// Facade over the CBLC (strike locations) and CBDT (bitmap data) tables — the Google color
    /// bitmap format (Noto Color Emoji). Supported subset: CBLC version 3, 32-bit-depth strikes,
    /// index formats 1 and 3, image formats 17 and 18 (PNG with small/big metrics) — the shapes
    /// shipping fonts actually use. Everything else degrades to "no image", never a throw:
    /// offsets are validated in long arithmetic against both tables' lengths, per the table
    /// hardening rules the outline stack established.
    /// </summary>
    internal sealed class CbdtTable
    {
        private const int CblcHeaderSize = 8;
        private const int BitmapSizeRecordSize = 48;
        private const int MaxStrikes = 64;   // hostile-input cap; real fonts carry a handful

        internal static OpenTypeTag CblcTag { get; } = OpenTypeTag.Parse("CBLC");
        internal static OpenTypeTag CbdtTag { get; } = OpenTypeTag.Parse("CBDT");

        private readonly ReadOnlyMemory<byte> _cblc;
        private readonly ReadOnlyMemory<byte> _cbdt;
        private readonly StrikeRecord[] _strikes;

        private CbdtTable(ReadOnlyMemory<byte> cblc, ReadOnlyMemory<byte> cbdt, StrikeRecord[] strikes)
        {
            _cblc = cblc;
            _cbdt = cbdt;
            _strikes = strikes;
        }

        public int StrikeCount => _strikes.Length;

        public static bool TryLoad(GlyphTypeface glyphTypeface, [NotNullWhen(true)] out CbdtTable? table)
        {
            table = null;

            if (!glyphTypeface.PlatformTypeface.TryGetTable(CblcTag, out var cblc) ||
                !glyphTypeface.PlatformTypeface.TryGetTable(CbdtTag, out var cbdt))
            {
                return false;
            }

            var span = cblc.Span;

            if (span.Length < CblcHeaderSize || cbdt.Length < 4)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(span) != 3)
            {
                return false;   // CBLC major version; EBLC (2) is out of scope
            }

            var numSizes = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4));

            if (numSizes == 0 || numSizes > MaxStrikes ||
                CblcHeaderSize + (long)numSizes * BitmapSizeRecordSize > span.Length)
            {
                return false;
            }

            var strikes = new StrikeRecord[(int)numSizes];
            var count = 0;

            for (var i = 0; i < numSizes; i++)
            {
                var record = span.Slice(CblcHeaderSize + i * BitmapSizeRecordSize, BitmapSizeRecordSize);
                var arrayOffset = BinaryPrimitives.ReadUInt32BigEndian(record);
                var subTableCount = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8));
                var bitDepth = record[46];

                // Color strikes only, with a plausible, in-bounds subtable array.
                if (bitDepth != 32 || subTableCount == 0 || subTableCount > ushort.MaxValue ||
                    arrayOffset + (long)subTableCount * 8 > span.Length)
                {
                    continue;
                }

                strikes[count++] = new StrikeRecord(
                    arrayOffset, (int)subTableCount, record[44], record[45]);
            }

            if (count == 0)
            {
                return false;
            }

            Array.Resize(ref strikes, count);
            table = new CbdtTable(cblc, cbdt, strikes);
            return true;
        }

        /// <summary>
        /// Picks the strike for a target device ppem: the smallest strike at or above the
        /// request (downscaling a bitmap looks better than upscaling), else the largest.
        /// </summary>
        public BitmapStrike SelectStrike(float pixelsPerEm)
        {
            var best = 0;
            var bestAtOrAbove = -1;

            for (var i = 0; i < _strikes.Length; i++)
            {
                var ppem = _strikes[i].PpemY;

                if (ppem >= pixelsPerEm && (bestAtOrAbove < 0 || ppem < _strikes[bestAtOrAbove].PpemY))
                {
                    bestAtOrAbove = i;
                }

                if (ppem > _strikes[best].PpemY)
                {
                    best = i;
                }
            }

            var index = bestAtOrAbove >= 0 ? bestAtOrAbove : best;

            return new BitmapStrike(index, _strikes[index].PpemX, _strikes[index].PpemY);
        }

        /// <summary>
        /// Reads one glyph's PNG image and metrics from a strike; <c>false</c> for glyphs the
        /// strike does not cover, empty (zero-length) slots, or any malformed offset.
        /// </summary>
        public bool TryGetGlyphImage(in BitmapStrike strike, ushort glyphIndex, out BitmapGlyphImage image)
        {
            image = default;

            if ((uint)strike.Index >= (uint)_strikes.Length)
            {
                return false;
            }

            var record = _strikes[strike.Index];
            var cblc = _cblc.Span;

            for (var i = 0; i < record.SubTableCount; i++)
            {
                var entry = cblc.Slice((int)record.ArrayOffset + i * 8, 8);
                var first = BinaryPrimitives.ReadUInt16BigEndian(entry);
                var last = BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(2));

                if (glyphIndex < first || glyphIndex > last)
                {
                    continue;
                }

                var headerOffset = record.ArrayOffset + BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4));

                if (headerOffset + 8 > (long)cblc.Length)
                {
                    return false;
                }

                var header = cblc.Slice((int)headerOffset);
                var indexFormat = BinaryPrimitives.ReadUInt16BigEndian(header);
                var imageFormat = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2));
                var imageDataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4));

                long start, end;

                if (indexFormat == 1)
                {
                    var pos = headerOffset + 8 + (long)(glyphIndex - first) * 4;

                    if (pos + 8 > cblc.Length)
                    {
                        return false;
                    }

                    start = BinaryPrimitives.ReadUInt32BigEndian(cblc.Slice((int)pos));
                    end = BinaryPrimitives.ReadUInt32BigEndian(cblc.Slice((int)pos + 4));
                }
                else if (indexFormat == 3)
                {
                    var pos = headerOffset + 8 + (long)(glyphIndex - first) * 2;

                    if (pos + 4 > cblc.Length)
                    {
                        return false;
                    }

                    start = BinaryPrimitives.ReadUInt16BigEndian(cblc.Slice((int)pos));
                    end = BinaryPrimitives.ReadUInt16BigEndian(cblc.Slice((int)pos + 2));
                }
                else
                {
                    return false;   // sparse/constant formats: not in the supported subset
                }

                if (end <= start)
                {
                    return false;   // empty slot — the glyph has no image in this strike
                }

                return TryReadImage(imageFormat, imageDataOffset + start, end - start, out image);
            }

            return false;
        }

        private bool TryReadImage(int imageFormat, long offset, long length, out BitmapGlyphImage image)
        {
            image = default;

            if (offset < 0 || length < 0 || offset + length > _cbdt.Length)
            {
                return false;
            }

            var data = _cbdt.Slice((int)offset, (int)length);
            var span = data.Span;

            // Format 17: small metrics (5 bytes); format 18: big metrics (8 bytes, vertical
            // fields ignored). Both follow with dataLen + PNG bytes.
            var metricsSize = imageFormat switch
            {
                17 => 5,
                18 => 8,
                _ => -1,
            };

            if (metricsSize < 0 || span.Length < metricsSize + 4)
            {
                return false;
            }

            var height = span[0];
            var width = span[1];
            var bearingX = (sbyte)span[2];
            var bearingY = (sbyte)span[3];
            var advance = span[4];

            var dataLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(metricsSize));

            if (dataLen > span.Length - metricsSize - 4)
            {
                return false;
            }

            image = new BitmapGlyphImage(
                data.Slice(metricsSize + 4, (int)dataLen), width, height, bearingX, bearingY, advance);
            return true;
        }

        private readonly struct StrikeRecord
        {
            public StrikeRecord(uint arrayOffset, int subTableCount, byte ppemX, byte ppemY)
            {
                ArrayOffset = arrayOffset;
                SubTableCount = subTableCount;
                PpemX = ppemX;
                PpemY = ppemY;
            }

            public uint ArrayOffset { get; }
            public int SubTableCount { get; }
            public byte PpemX { get; }
            public byte PpemY { get; }
        }
    }
}
