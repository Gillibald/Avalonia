using System;
using System.Numerics;
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
        => Transcode(source, srcSize, sourceStride, srcFormat, srcAlphaFormat, dest, destStride, destFormat,
            destAlphaFormat, null, null, 0);

    /// <summary>
    /// Converts pixels between formats and alpha representations, including indexed formats.
    /// </summary>
    /// <remarks>
    /// An indexed source needs <paramref name="sourcePalette"/>, and an indexed destination
    /// needs <paramref name="destPalette"/> to match against. For an indexed destination,
    /// pixels whose alpha is below <paramref name="alphaThresholdPercent"/> percent of full
    /// opacity take the palette entry closest to transparent black.
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
        AlphaFormat destAlphaFormat,
        BitmapPalette? sourcePalette,
        BitmapPalette? destPalette,
        double alphaThresholdPercent)
    {
        var endpoints = new RowEndpoints(srcFormat, srcAlphaFormat, sourcePalette, destFormat, destAlphaFormat,
            destPalette, alphaThresholdPercent);

        var width = srcSize.Width;

        // Formats wider than eight bits per channel, floating-point and CMYK formats would
        // lose precision or range in the 8-bit row, so either side being one of them moves
        // the whole conversion to the wide row.
        if (WidePixelFormat.IsWide(srcFormat) || WidePixelFormat.IsWide(destFormat))
        {
            TranscodeWide(endpoints, source, srcSize, sourceStride, dest, destStride);

            return;
        }

        var rowBytes = (long)width * sizeof(Rgba8888Pixel);

        if (rowBytes <= MaxStackRowBytes)
        {
            Span<Rgba8888Pixel> row = stackalloc Rgba8888Pixel[width];

            Transcode(endpoints, row, source, srcSize.Height, sourceStride, dest, destStride);
        }
        else
        {
            using var staging = BitmapMemoryPool.Shared.Rent(rowBytes);

            Transcode(endpoints, new Span<Rgba8888Pixel>((void*)staging.Address, width), source, srcSize.Height,
                sourceStride, dest, destStride);
        }
    }

    private static void Transcode(
        in RowEndpoints endpoints,
        Span<Rgba8888Pixel> row,
        IntPtr source,
        int height,
        int sourceStride,
        IntPtr dest,
        int destStride)
    {
        for (var y = 0; y < height; y++)
        {
            endpoints.Read(source + (nint)sourceStride * y, row);
            endpoints.Write(row, endpoints.RowAlphaFormat, dest + (nint)destStride * y);
        }
    }

    private static void TranscodeWide(
        in RowEndpoints endpoints,
        IntPtr source,
        PixelSize srcSize,
        int sourceStride,
        IntPtr dest,
        int destStride)
    {
        var width = srcSize.Width;
        var wideSource = WidePixelFormat.IsWide(endpoints.SourceFormat);
        var wideDest = WidePixelFormat.IsWide(endpoints.DestFormat);

        using var wideStaging = BitmapMemoryPool.Shared.Rent((long)width * sizeof(Vector4));
        using var narrowStaging = wideSource && wideDest ? null : BitmapMemoryPool.Shared.Rent((long)width * sizeof(Rgba8888Pixel));

        var wide = new Span<Vector4>((void*)wideStaging.Address, width);
        var narrow = narrowStaging is null ? default : new Span<Rgba8888Pixel>((void*)narrowStaging.Address, width);

        for (var y = 0; y < srcSize.Height; y++)
        {
            var sourceRow = source + (nint)sourceStride * y;
            var destRow = dest + (nint)destStride * y;

            if (wideSource)
            {
                WidePixelFormat.ReadRow(sourceRow, endpoints.SourceFormat, endpoints.SourceAlphaFormat, wide);
            }
            else
            {
                endpoints.Read(sourceRow, narrow);
                WidePixelFormat.Widen(narrow, endpoints.RowAlphaFormat, wide);
            }

            if (wideDest)
            {
                WidePixelFormat.WriteRow(wide, destRow, endpoints.DestFormat, endpoints.DestAlphaFormat);
            }
            else
            {
                WidePixelFormat.Narrow(wide, narrow);
                endpoints.Write(narrow, AlphaFormat.Unpremul, destRow);
            }
        }
    }

    // The two ends of a conversion on the 8-bit row, with palettes resolved once per call.
    private readonly struct RowEndpoints
    {
        private readonly Rgba8888Pixel[]? _sourceEntries;
        private readonly PaletteMatcher? _matcher;

        public RowEndpoints(PixelFormat sourceFormat, AlphaFormat sourceAlphaFormat, BitmapPalette? sourcePalette,
            PixelFormat destFormat, AlphaFormat destAlphaFormat, BitmapPalette? destPalette, double alphaThresholdPercent)
        {
            SourceFormat = sourceFormat;
            SourceAlphaFormat = sourceAlphaFormat;
            DestFormat = destFormat;
            DestAlphaFormat = destAlphaFormat;

            if (sourceFormat.IsIndexed)
            {
                _sourceEntries = IndexedPixelFormat.GetEntries(sourcePalette ??
                    throw new ArgumentException($"An indexed source ({sourceFormat}) needs a palette.", nameof(sourcePalette)));
            }

            if (destFormat.IsIndexed)
            {
                _matcher = new PaletteMatcher(destPalette ??
                    throw new ArgumentException($"An indexed destination ({destFormat}) needs a palette.", nameof(destPalette)),
                    destFormat, alphaThresholdPercent);
            }

            // Palette colors carry straight alpha regardless of the alpha format passed for the source.
            RowAlphaFormat = sourceFormat.IsIndexed ? AlphaFormat.Unpremul : sourceAlphaFormat;
        }

        public PixelFormat SourceFormat { get; }
        public AlphaFormat SourceAlphaFormat { get; }
        public PixelFormat DestFormat { get; }
        public AlphaFormat DestAlphaFormat { get; }
        public AlphaFormat RowAlphaFormat { get; }

        public void Read(IntPtr sourceRow, Span<Rgba8888Pixel> row)
        {
            if (_sourceEntries is null)
                ReadRow(sourceRow, SourceFormat, row);
            else
                IndexedPixelFormat.ReadRow(sourceRow, SourceFormat, _sourceEntries, row);
        }

        public void Write(ReadOnlySpan<Rgba8888Pixel> row, AlphaFormat rowAlphaFormat, IntPtr destRow)
        {
            if (_matcher is null)
                WriteRow(row, destRow, DestFormat, DestAlphaFormat, rowAlphaFormat);
            else
                IndexedPixelFormat.WriteRow(row, rowAlphaFormat, destRow, DestFormat, _matcher);
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
