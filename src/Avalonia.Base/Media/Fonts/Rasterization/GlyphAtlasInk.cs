using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// One bit per texel of an atlas page, set where the page holds ink: coverage other than
    /// zero on an A8 page, alpha other than zero on an RGBA page. It answers whether two sprites
    /// share ink for a page that keeps no CPU copy of its texels.
    /// </summary>
    /// <remarks>
    /// Rows are kept in blocks allocated on the first ink they receive, so a page that grows
    /// leaves no copies behind and rows without ink cost nothing. The atlas sets bits under its
    /// lock; readers may run on other threads, since the bits of an entry never change once it
    /// is placed and a block, once published, is never replaced.
    /// </remarks>
    internal sealed class GlyphAtlasInk
    {
        private const int WordsPerRow = GlyphMaskAtlas.PageWidth / 64;
        private const int BlockRows = 64;
        private const int BlockWords = BlockRows * WordsPerRow;

        private readonly ulong[]?[] _blocks = new ulong[]?[GlyphMaskAtlas.MaxPageHeight / BlockRows];
        private long _bytes;

        /// <summary>The bytes of the blocks allocated so far.</summary>
        public long Bytes => _bytes;

        /// <summary>
        /// Sets the bits of the texels of row <paramref name="y"/> from column <paramref name="x"/>
        /// on that hold ink: <paramref name="texels"/> holds them <paramref name="step"/> bytes apart,
        /// coverage or alpha in the last byte of each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Mark(int x, int y, ReadOnlySpan<byte> texels, int step)
        {
            ulong[]? block = null;
            var row = (y & (BlockRows - 1)) * WordsPerRow;
            var count = texels.Length / step;

            // The bits of 64 texels at a time, gathered without branching on each texel: a frame
            // that lays out a screen of new glyphs marks every texel of their boxes, and whether a
            // texel holds ink changes too often for a branch to predict.
            for (var start = 0; start < count; start += 64)
            {
                var length = Math.Min(64, count - start);
                var bits = 0UL;
                var i = 0;

                if (step == 1)
                {
                    for (; i + 16 <= length; i += 16)
                    {
                        var empty = Vector128.Equals(Vector128.Create(texels.Slice(start + i, 16)), Vector128<byte>.Zero);

                        bits |= (~(ulong)empty.ExtractMostSignificantBits() & 0xFFFF) << i;
                    }
                }
                else if (step == 4)
                {
                    var alpha = Vector128.Create(0xFF000000u);

                    for (; i + 4 <= length; i += 4)
                    {
                        var texel = Vector128.Create(texels.Slice((start + i) * 4, 16)).AsUInt32();
                        var empty = Vector128.Equals(texel & alpha, Vector128<uint>.Zero);

                        bits |= (~(ulong)empty.ExtractMostSignificantBits() & 0xF) << i;
                    }
                }

                for (; i < length; i++)
                {
                    bits |= (texels[(start + i) * step + step - 1] != 0 ? 1UL : 0UL) << i;
                }

                if (bits != 0)
                {
                    Set(block ??= GetBlock(y), row, x + start, bits, length);
                }
            }
        }

        /// <summary>Sets <paramref name="count"/> bits, at most 64, from column <paramref name="column"/> of a block row.</summary>
        private static void Set(ulong[] block, int row, int column, ulong bits, int count)
        {
            var word = row + (column >> 6);
            var shift = column & 63;

            block[word] |= bits << shift;

            if (shift + count > 64)
            {
                block[word + 1] |= bits >> (64 - shift);
            }
        }

        private ulong[] GetBlock(int y)
        {
            ref var block = ref _blocks[y / BlockRows];

            if (block is null)
            {
                block = new ulong[BlockWords];
                _bytes += BlockWords * sizeof(ulong);
            }

            return block;
        }

        /// <summary>Whether the texel at <paramref name="x"/>, <paramref name="y"/> holds ink.</summary>
        public bool IsSet(int x, int y) => (Read(x, y, 1) & 1) != 0;

        /// <summary>
        /// The bits of <paramref name="count"/> texels of row <paramref name="y"/> from column
        /// <paramref name="x"/> on, at most 64, the first in the lowest bit.
        /// </summary>
        public ulong Read(int x, int y, int count)
        {
            if (_blocks[y >> 6] is not { } block)
            {
                return 0;
            }

            var word = (y & (BlockRows - 1)) * WordsPerRow + (x >> 6);
            var shift = x & 63;
            var bits = block[word] >> shift;

            if (shift != 0 && shift + count > 64)
            {
                bits |= block[word + 1] << (64 - shift);
            }

            return count < 64 ? bits & ((1UL << count) - 1) : bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool SharesNarrow(GlyphAtlasInk first, int firstX, int firstY, GlyphAtlasInk second,
            int secondX, int secondY, int width, int height)
        {
            var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            var firstShift = firstX & 63;
            var secondShift = secondX & 63;
            var firstSpans = firstShift + width > 64;
            var secondSpans = secondShift + width > 64;
            ulong[]? firstBlock = null;
            ulong[]? secondBlock = null;
            var firstIndex = -1;
            var secondIndex = -1;

            for (var row = 0; row < height; row++)
            {
                var y = firstY + row;
                var v = secondY + row;

                if (y >> 6 != firstIndex)
                {
                    firstIndex = y >> 6;
                    firstBlock = first._blocks[firstIndex];
                }

                if (v >> 6 != secondIndex)
                {
                    secondIndex = v >> 6;
                    secondBlock = second._blocks[secondIndex];
                }

                if (firstBlock is null || secondBlock is null)
                {
                    continue;
                }

                var a = (y & (BlockRows - 1)) * WordsPerRow + (firstX >> 6);
                var b = (v & (BlockRows - 1)) * WordsPerRow + (secondX >> 6);
                var firstBits = firstBlock[a] >> firstShift;
                var secondBits = secondBlock[b] >> secondShift;

                if (firstSpans)
                {
                    firstBits |= firstBlock[a + 1] << (64 - firstShift);
                }

                if (secondSpans)
                {
                    secondBits |= secondBlock[b + 1] << (64 - secondShift);
                }

                if ((firstBits & secondBits & mask) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a <paramref name="width"/> by <paramref name="height"/> rectangle at
        /// (<paramref name="firstX"/>, <paramref name="firstY"/>) of <paramref name="first"/> and the
        /// one at (<paramref name="secondX"/>, <paramref name="secondY"/>) of <paramref name="second"/>
        /// hold ink at the same place.
        /// </summary>
        public static bool Shares(GlyphAtlasInk first, int firstX, int firstY, GlyphAtlasInk second, int secondX,
            int secondY, int width, int height)
        {
            // Sprites of neighbouring glyphs mostly overlap in a few columns over many rows: one
            // word or two of each page per row, the block looked up when the row enters it.
            if (width <= 64)
            {
                return SharesNarrow(first, firstX, firstY, second, secondX, secondY, width, height);
            }

            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column += 64)
                {
                    var count = Math.Min(64, width - column);

                    if ((first.Read(firstX + column, firstY + row, count) &
                         second.Read(secondX + column, secondY + row, count)) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
