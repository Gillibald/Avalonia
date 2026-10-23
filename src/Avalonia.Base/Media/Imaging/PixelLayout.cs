using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Moves pixels without interpreting them, so every format, including indexed and
/// high-depth ones, keeps its exact bits.
/// </summary>
internal static unsafe class PixelLayout
{
    /// <summary>
    /// Copies <paramref name="width"/> pixels starting at pixel <paramref name="sourceX"/>
    /// of a source row to the start of a destination row. Sub-byte pixels are shifted to
    /// begin at the destination's most significant bit, and the unused bits after the
    /// last pixel are zero.
    /// </summary>
    public static void CopyRow(byte* source, int sourceX, byte* dest, int width, int bitsPerPixel)
    {
        if (bitsPerPixel % 8 == 0)
        {
            var bytesPerPixel = bitsPerPixel / 8;
            var count = (long)width * bytesPerPixel;

            Buffer.MemoryCopy(source + (nint)sourceX * bytesPerPixel, dest, count, count);

            return;
        }

        var startBit = (long)sourceX * bitsPerPixel;
        var bitCount = (long)width * bitsPerPixel;
        var src = source + startBit / 8;
        var shift = (int)(startBit % 8);
        var byteCount = (int)((bitCount + 7) / 8);

        if (shift == 0)
        {
            Buffer.MemoryCopy(src, dest, byteCount, byteCount);
        }
        else
        {
            // The last source byte read is the one holding the final bit, so the copy
            // never reads past the source row.
            var lastSourceByte = (int)((shift + bitCount - 1) / 8);

            for (var i = 0; i < byteCount; i++)
            {
                var high = src[i] << shift;
                var low = i + 1 <= lastSourceByte ? src[i + 1] >> (8 - shift) : 0;

                dest[i] = (byte)(high | low);
            }
        }

        var tailBits = (int)(bitCount % 8);

        if (tailBits != 0)
            dest[byteCount - 1] &= (byte)(0xFF << (8 - tailBits));
    }

    /// <summary>
    /// Writes the oriented image of a source into a destination laid out at the oriented
    /// size, with rows of <paramref name="destStride"/> bytes.
    /// </summary>
    public static void Orient(ILockedFramebuffer source, PixelOrientation orientation, byte* dest, int destStride)
    {
        var (swap, negX, negY) = PixelOrientations.Decompose(orientation);
        var size = source.Size;
        var width = swap ? size.Height : size.Width;
        var height = swap ? size.Width : size.Height;
        var bits = source.Format.BitsPerPixel;
        var src = (byte*)source.Address;

        for (var y = 0; y < height; y++)
        {
            var destRow = dest + (nint)y * destStride;

            if (bits % 8 != 0)
                new Span<byte>(destRow, (width * bits + 7) / 8).Clear();

            for (var x = 0; x < width; x++)
            {
                // Map the output pixel back to its source: undo the axis negation, then the swap.
                var unflippedX = negX ? width - 1 - x : x;
                var unflippedY = negY ? height - 1 - y : y;
                var sourceX = swap ? unflippedY : unflippedX;
                var sourceY = swap ? unflippedX : unflippedY;

                CopyPixel(src + (nint)sourceY * source.RowBytes, sourceX, destRow, x, bits);
            }
        }
    }

    private static void CopyPixel(byte* sourceRow, int sourceX, byte* destRow, int destX, int bitsPerPixel)
    {
        if (bitsPerPixel % 8 == 0)
        {
            var bytesPerPixel = bitsPerPixel / 8;

            Buffer.MemoryCopy(sourceRow + (nint)sourceX * bytesPerPixel, destRow + (nint)destX * bytesPerPixel,
                bytesPerPixel, bytesPerPixel);

            return;
        }

        var mask = (1 << bitsPerPixel) - 1;
        var sourceBit = sourceX * bitsPerPixel;
        var value = (sourceRow[sourceBit / 8] >> (8 - bitsPerPixel - sourceBit % 8)) & mask;
        var destBit = destX * bitsPerPixel;

        destRow[destBit / 8] |= (byte)(value << (8 - bitsPerPixel - destBit % 8));
    }
}
