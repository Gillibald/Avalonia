using System;
using Avalonia.Platform;
using Avalonia.Platform.Internal;
namespace Avalonia.Media.Imaging;

internal static unsafe class PixelFormatTranscoder
{
    // Rows at or below this size are staged on the stack instead of renting pool memory.
    private const int MaxStackRowBytes = 1024;

    /// <summary>
    /// Converts pixels between formats and alpha representations.
    /// </summary>
    /// <remarks>
    /// The source and destination memory must not overlap: rows are streamed through a
    /// single-row staging buffer, so the destination is written while the source is
    /// still being read.
    /// </remarks>
    public static void Transcode(
        IntPtr source,
        PixelSize srcSize,
        int sourceStride,
        PixelFormat srcFormat,
        AlphaFormat srcAlphaFormat,
        IntPtr dest,
        int destStride,
        PixelFormat destFormat,
        AlphaFormat destAlphaFormat)
    {
        var width = srcSize.Width;
        var rowBytes = (long)width * sizeof(Rgba8888Pixel);

        if (rowBytes <= MaxStackRowBytes)
        {
            Span<Rgba8888Pixel> row = stackalloc Rgba8888Pixel[width];

            Transcode(row, source, srcSize.Height, sourceStride, srcFormat, srcAlphaFormat, dest, destStride, destFormat, destAlphaFormat);
        }
        else
        {
            using var staging = BitmapMemoryPool.Shared.Rent(rowBytes);

            Transcode(new Span<Rgba8888Pixel>((void*)staging.Address, width), source, srcSize.Height, sourceStride, srcFormat, srcAlphaFormat, dest, destStride, destFormat, destAlphaFormat);
        }
    }

    private static void Transcode(
        Span<Rgba8888Pixel> row,
        IntPtr source,
        int height,
        int sourceStride,
        PixelFormat srcFormat,
        AlphaFormat srcAlphaFormat,
        IntPtr dest,
        int destStride,
        PixelFormat destFormat,
        AlphaFormat destAlphaFormat)
    {
        for (var y = 0; y < height; y++)
        {
            ReadRow(source + (nint)sourceStride * y, srcFormat, row);

            WriteRow(row, dest + (nint)destStride * y, destFormat, destAlphaFormat, srcAlphaFormat);
        }
    }

    /// <summary>
    /// Reads one source row into canonical Rgba8888 pixels. The alpha keeps the source
    /// interpretation; conversion happens on write.
    /// </summary>
    public static void ReadRow(IntPtr sourceRow, PixelFormat srcFormat, Span<Rgba8888Pixel> row)
        => PixelFormatReader.ReadRow(row, sourceRow, srcFormat);

    /// <summary>
    /// Writes one canonical Rgba8888 row into a destination row, converting each pixel
    /// from <paramref name="rowAlphaFormat"/> to <paramref name="destAlphaFormat"/>.
    /// </summary>
    public static void WriteRow(ReadOnlySpan<Rgba8888Pixel> row, IntPtr destRow, PixelFormat destFormat, AlphaFormat destAlphaFormat, AlphaFormat rowAlphaFormat)
        => PixelFormatWriter.WriteRow(row, destRow, destFormat, destAlphaFormat, rowAlphaFormat);
}
