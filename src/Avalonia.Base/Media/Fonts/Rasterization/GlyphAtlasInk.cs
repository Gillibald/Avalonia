using System;

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
        public void Mark(int x, int y, ReadOnlySpan<byte> texels, int step)
        {
            ulong[]? block = null;
            var row = y % BlockRows * WordsPerRow;

            for (int i = 0, offset = step - 1; offset < texels.Length; i++, offset += step)
            {
                if (texels[offset] == 0)
                {
                    continue;
                }

                block ??= GetBlock(y);

                var column = x + i;

                block[row + column / 64] |= 1UL << (column % 64);
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
            if (_blocks[y / BlockRows] is not { } block)
            {
                return 0;
            }

            var word = y % BlockRows * WordsPerRow + x / 64;
            var shift = x % 64;
            var bits = block[word] >> shift;

            if (shift != 0 && shift + count > 64)
            {
                bits |= block[word + 1] << (64 - shift);
            }

            return count < 64 ? bits & ((1UL << count) - 1) : bits;
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
