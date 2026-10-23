using System;
using Avalonia.Platform;
using Avalonia.Platform.Internal;

namespace Avalonia.Media.Imaging;

/// <summary>
/// The shared implementation behind the public pixel copy and conversion operations.
/// </summary>
internal static unsafe class PixelOperations
{
    /// <summary>
    /// Converts a rectangle of the source into caller memory, starting at the destination
    /// origin. Everything is validated before the first byte is written.
    /// </summary>
    public static void CopyPixels(ILockedFramebuffer source, BitmapPalette? sourcePalette, PixelRect sourceRect,
        IntPtr destination, long destinationSize, int destinationStride, PixelFormat format,
        AlphaFormat alphaFormat, PixelConversionOptions? options)
    {
        _ = source ?? throw new ArgumentNullException(nameof(source));

        if (destination == IntPtr.Zero)
            throw new ArgumentNullException(nameof(destination));

        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            throw new ArgumentException("The source rectangle is empty.", nameof(sourceRect));

        if (sourceRect.X < 0 || sourceRect.Y < 0 ||
            sourceRect.Right > source.Size.Width || sourceRect.Bottom > source.Size.Height)
            throw new ArgumentOutOfRangeException(nameof(sourceRect), "The source rectangle must lie within the source.");

        var rowBytes = PixelFormatArithmetic.GetMinRowBytes(format, sourceRect.Width);

        if (destinationStride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "The stride is smaller than one row of pixels.");

        if (destinationSize < (long)destinationStride * (sourceRect.Height - 1) + rowBytes)
            throw new ArgumentOutOfRangeException(nameof(destinationSize),
                "The destination is smaller than stride * (height - 1) plus one row of pixels.");

        var targetPalette = options?.Palette;
        var raw = format == source.Format && (alphaFormat == source.AlphaFormat || !format.HasAlpha) &&
                  (!format.IsIndexed || targetPalette is null || ReferenceEquals(targetPalette, sourcePalette));

        if (raw)
        {
            CopyRaw(source, sourceRect, (byte*)destination, destinationStride);
            return;
        }

        if (source.Format.IsIndexed && sourcePalette is null)
            throw new ArgumentException($"An indexed source ({source.Format}) needs a palette.", nameof(sourcePalette));

        if (format.IsIndexed && targetPalette is null)
            throw new ArgumentException($"Converting to an indexed format ({format}) needs a palette.", nameof(options));

        var bits = source.Format.BitsPerPixel;
        var threshold = options?.AlphaThresholdPercent ?? 0;

        if ((long)sourceRect.X * bits % 8 == 0)
        {
            var start = source.Address + (nint)sourceRect.Y * source.RowBytes + (nint)((long)sourceRect.X * bits / 8);

            PixelFormatTranscoder.Transcode(start, sourceRect.Size, source.RowBytes, source.Format,
                source.AlphaFormat, destination, destinationStride, format, alphaFormat, sourcePalette,
                targetPalette, threshold);

            return;
        }

        // A sub-byte rectangle that does not start on a byte boundary is first shifted into
        // byte-aligned rows, which the transcoder can address.
        var alignedStride = PixelFormatArithmetic.GetMinRowBytes(source.Format, sourceRect.Width);

        using var aligned = BitmapMemoryPool.Shared.Rent((long)alignedStride * sourceRect.Height);

        CopyRaw(source, sourceRect, (byte*)aligned.Address, alignedStride);

        PixelFormatTranscoder.Transcode(aligned.Address, sourceRect.Size, alignedStride, source.Format,
            source.AlphaFormat, destination, destinationStride, format, alphaFormat, sourcePalette,
            targetPalette, threshold);
    }

    private static void CopyRaw(ILockedFramebuffer source, PixelRect sourceRect, byte* dest, int destStride)
    {
        var bits = source.Format.BitsPerPixel;

        for (var y = 0; y < sourceRect.Height; y++)
        {
            var sourceRow = (byte*)source.Address + (nint)(sourceRect.Y + y) * source.RowBytes;

            PixelLayout.CopyRow(sourceRow, sourceRect.X, dest + (nint)y * destStride, sourceRect.Width, bits);
        }
    }
}
