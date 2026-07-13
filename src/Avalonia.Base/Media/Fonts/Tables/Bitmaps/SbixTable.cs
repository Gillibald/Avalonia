using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Avalonia.Media.Fonts.Rasterization;

namespace Avalonia.Media.Fonts.Tables.Bitmaps
{
    /// <summary>
    /// The Apple color bitmap format: per-strike PNG glyph records. Supported subset: 'png '
    /// graphics and one 'dupe' indirection; 'jpg '/'tiff' degrade to no-image. Offsets are
    /// validated in long arithmetic; strike counts are capped; malformed shapes never throw —
    /// the same hardening rules as the CBDT facade.
    /// </summary>
    internal sealed class SbixTable : IBitmapGlyphSource
    {
        private const int MaxStrikes = 64;
        private const int DecodedBudgetBytes = 4 * 1024 * 1024;
        private static readonly uint s_pngTag = 0x706E6720;    // 'png '
        private static readonly uint s_dupeTag = 0x64757065;   // 'dupe'

        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse("sbix");

        private readonly ReadOnlyMemory<byte> _data;
        private readonly int _glyphCount;
        private readonly (uint Offset, ushort Ppem)[] _strikes;
        private ConcurrentDictionary<uint, BitmapGlyphPlacement>? _decoded;
        private int _decodedBytes;

        private SbixTable(ReadOnlyMemory<byte> data, int glyphCount, (uint, ushort)[] strikes)
        {
            _data = data;
            _glyphCount = glyphCount;
            _strikes = strikes;
        }

        public static bool TryLoad(GlyphTypeface glyphTypeface, [NotNullWhen(true)] out SbixTable? table)
        {
            table = null;

            if (!glyphTypeface.PlatformTypeface.TryGetTable(Tag, out var data))
            {
                return false;
            }

            var span = data.Span;

            if (span.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(span) != 1)
            {
                return false;
            }

            var numStrikes = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4));
            var glyphCount = glyphTypeface.GlyphCount;

            if (numStrikes == 0 || numStrikes > MaxStrikes ||
                8 + (long)numStrikes * 4 > span.Length || glyphCount <= 0)
            {
                return false;
            }

            var strikes = new (uint, ushort)[(int)numStrikes];
            var count = 0;

            for (var i = 0; i < numStrikes; i++)
            {
                var offset = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8 + i * 4));

                // A strike needs its 4-byte header plus glyphCount + 1 data offsets.
                if (offset + 4 + ((long)glyphCount + 1) * 4 > span.Length)
                {
                    continue;
                }

                strikes[count++] = (offset, BinaryPrimitives.ReadUInt16BigEndian(span.Slice((int)offset)));
            }

            if (count == 0)
            {
                return false;
            }

            Array.Resize(ref strikes, count);
            table = new SbixTable(data, glyphCount, strikes);
            return true;
        }

        public BitmapStrike SelectStrike(float pixelsPerEm)
        {
            var best = 0;
            var bestAtOrAbove = -1;

            for (var i = 0; i < _strikes.Length; i++)
            {
                var ppem = _strikes[i].Ppem;

                if (ppem >= pixelsPerEm && (bestAtOrAbove < 0 || ppem < _strikes[bestAtOrAbove].Ppem))
                {
                    bestAtOrAbove = i;
                }

                if (ppem > _strikes[best].Ppem)
                {
                    best = i;
                }
            }

            var index = bestAtOrAbove >= 0 ? bestAtOrAbove : best;
            var selected = (byte)Math.Min(_strikes[index].Ppem, byte.MaxValue);

            return new BitmapStrike(index, selected, selected);
        }

        public bool HasGlyphImage(ushort glyphIndex)
        {
            for (var i = 0; i < _strikes.Length; i++)
            {
                if (TryGetRecord(i, glyphIndex, 0, out _, out _, out _))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryGetPlacement(in BitmapStrike strike, ushort glyphIndex, IBitmapGlyphDecoder decoder,
            out BitmapGlyphPlacement placement)
        {
            placement = default;

            if ((uint)strike.Index >= (uint)_strikes.Length)
            {
                return false;
            }

            var cache = _decoded ??= new();
            var key = (uint)strike.Index << 16 | glyphIndex;

            if (cache.TryGetValue(key, out placement))
            {
                return true;
            }

            if (!TryGetRecord(strike.Index, glyphIndex, 0, out var png, out var originX, out var originY) ||
                !decoder.TryDecode(png, out var decoded))
            {
                return false;
            }

            // sbix origin offsets are the bitmap's lower-left corner from the glyph origin,
            // y-up; normalize to the pipeline's pen-relative y-down top-left.
            placement = new BitmapGlyphPlacement(decoded, originX, -(originY + decoded.Height));

            if (Interlocked.Add(ref _decodedBytes, decoded.Bgra.Length) > DecodedBudgetBytes)
            {
                cache.Clear();
                Interlocked.Exchange(ref _decodedBytes, decoded.Bgra.Length);
            }

            cache.TryAdd(key, placement);
            return true;
        }

        private bool TryGetRecord(int strikeIndex, ushort glyphIndex, int depth,
            out ReadOnlyMemory<byte> png, out short originX, out short originY)
        {
            png = default;
            originX = 0;
            originY = 0;

            if (glyphIndex >= _glyphCount || depth > 1)
            {
                return false;   // one 'dupe' hop only — hostile chains stop here
            }

            var span = _data.Span;
            var strikeOffset = _strikes[strikeIndex].Offset;
            var offsetsBase = strikeOffset + 4;
            var position = offsetsBase + (long)glyphIndex * 4;

            var start = strikeOffset + BinaryPrimitives.ReadUInt32BigEndian(span.Slice((int)position));
            var end = strikeOffset + BinaryPrimitives.ReadUInt32BigEndian(span.Slice((int)position + 4));

            // A record needs origin offsets (4) plus a graphic type tag (4).
            if (end <= start || end > span.Length || end - start < 8)
            {
                return false;
            }

            var record = span.Slice((int)start, (int)(end - start));
            var graphicType = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4));

            if (graphicType == s_dupeTag)
            {
                return record.Length >= 10 &&
                    TryGetRecord(strikeIndex, BinaryPrimitives.ReadUInt16BigEndian(record.Slice(8)), depth + 1,
                        out png, out originX, out originY);
            }

            if (graphicType != s_pngTag)
            {
                return false;   // 'jpg '/'tiff' are out of the supported subset
            }

            originX = BinaryPrimitives.ReadInt16BigEndian(record);
            originY = BinaryPrimitives.ReadInt16BigEndian(record.Slice(2));
            png = _data.Slice((int)start + 8, (int)(end - start) - 8);
            return true;
        }
    }
}
