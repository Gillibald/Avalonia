using System;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// Reads serialized Slug texels back with exactly the pixel shader's addressing: curve end
    /// points come from the next texel in the same row, header blocks and list walks are
    /// unwrapped, and only a list's start location resolves a linear offset with row wrapping.
    /// The round-trip tests are built on this, and it doubles as the debugging tool for payload
    /// issues — if a layout rule is violated, reads land on the wrong texel and comparisons
    /// fail, the same way the shader would misrender.
    /// </summary>
    internal static class SlugTexelDecoder
    {
        /// <summary>Reads the three control points of the curve whose first texel is (x, y).</summary>
        public static SlugQuadCurve ReadCurve(ReadOnlySpan<Half> curveTexels, int x, int y)
        {
            var i = ((y << SlugTexelSerializer.LogTextureWidth) + x) * 4;

            return new SlugQuadCurve(
                (float)curveTexels[i], (float)curveTexels[i + 1],
                (float)curveTexels[i + 2], (float)curveTexels[i + 3],
                (float)curveTexels[i + 4], (float)curveTexels[i + 5]);
        }

        /// <summary>
        /// Reads one band header of a glyph's header block and resolves its list location. The
        /// header index counts horizontal bands first, then vertical ones.
        /// </summary>
        public static (int Count, int ListX, int ListY) ReadBandHeader(
            ReadOnlySpan<Half> bandTexels, int glyphLocX, int glyphLocY, int headerIndex)
        {
            var i = ((glyphLocY << SlugTexelSerializer.LogTextureWidth) + glyphLocX + headerIndex) * 4;
            var count = (int)(float)bandTexels[i];
            var offset = (int)(float)bandTexels[i + 1];

            // CalcBandLoc: the offset is linear from the glyph location and wraps to later rows.
            var x = glyphLocX + offset;
            var y = glyphLocY + (x >> SlugTexelSerializer.LogTextureWidth);

            return (count, x & (SlugTexelSerializer.TextureWidth - 1), y);
        }

        /// <summary>Reads one curve-location entry of a band list.</summary>
        public static (int X, int Y) ReadListEntry(
            ReadOnlySpan<Half> bandTexels, int listX, int listY, int entryIndex)
        {
            var i = ((listY << SlugTexelSerializer.LogTextureWidth) + listX + entryIndex) * 4;

            return ((int)(float)bandTexels[i], (int)(float)bandTexels[i + 1]);
        }
    }
}
