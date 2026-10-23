using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Reads and writes the indexed formats, whose pixels are palette indices packed most
/// significant bits first.
/// </summary>
internal static unsafe class IndexedPixelFormat
{
    /// <summary>
    /// Gets the palette's colors as straight-alpha pixels.
    /// </summary>
    public static Rgba8888Pixel[] GetEntries(BitmapPalette palette)
    {
        var colors = palette.Colors;
        var entries = new Rgba8888Pixel[colors.Count];

        for (var i = 0; i < entries.Length; i++)
        {
            var color = colors[i];

            entries[i] = new Rgba8888Pixel(color.R, color.G, color.B, color.A);
        }

        return entries;
    }

    /// <summary>
    /// Reads one row of indices as the straight-alpha palette colors they refer to. An
    /// index past the end of the palette reads as opaque black.
    /// </summary>
    public static void ReadRow(IntPtr source, PixelFormat format, ReadOnlySpan<Rgba8888Pixel> entries, Span<Rgba8888Pixel> row)
    {
        var src = (byte*)source;
        var bits = format.BitsPerPixel;
        var mask = (1 << bits) - 1;
        var pixelsPerByte = 8 / bits;

        for (var x = 0; x < row.Length; x++)
        {
            var shift = 8 - bits * (x % pixelsPerByte + 1);
            var index = (src[x / pixelsPerByte] >> shift) & mask;

            row[x] = index < entries.Length ? entries[index] : new Rgba8888Pixel(0, 0, 0, 255);
        }
    }

    /// <summary>
    /// Writes one row as the indices of the nearest palette entries. Bits after the last
    /// pixel of the row are zero.
    /// </summary>
    public static void WriteRow(ReadOnlySpan<Rgba8888Pixel> row, AlphaFormat rowAlphaFormat, IntPtr dest,
        PixelFormat format, PaletteMatcher matcher)
    {
        var dst = (byte*)dest;
        var bits = format.BitsPerPixel;
        var pixelsPerByte = 8 / bits;
        var current = 0;

        for (var x = 0; x < row.Length; x++)
        {
            var pixel = PixelFormatWriter.GetConvertedPixel(row[x], rowAlphaFormat, AlphaFormat.Unpremul);
            var slot = x % pixelsPerByte;

            current |= matcher.Nearest(pixel) << (8 - bits * (slot + 1));

            if (slot == pixelsPerByte - 1)
            {
                dst[x / pixelsPerByte] = (byte)current;
                current = 0;
            }
        }

        if (row.Length % pixelsPerByte != 0)
            dst[row.Length / pixelsPerByte] = (byte)current;
    }
}

/// <summary>
/// Finds the palette entry for a straight-alpha color the way WIC does.
/// </summary>
/// <remarks>
/// The match is the entry with the smallest squared RGB distance, where an entry's own
/// transparency counts as distance from opaque: (255 - alpha) squared is added. The
/// pixel's alpha only matters against the threshold: below it, the pixel takes the entry
/// closest to transparent black. Ties go to the lowest index, and only the first
/// 2^bpp entries are reachable by the format.
/// </remarks>
internal sealed class PaletteMatcher
{
    private readonly Rgba8888Pixel[] _entries;
    private readonly int _transparentIndex;
    private readonly int _alphaThreshold;

    public PaletteMatcher(BitmapPalette palette, PixelFormat format, double alphaThresholdPercent)
    {
        if (alphaThresholdPercent < 0 || alphaThresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(alphaThresholdPercent), "The alpha threshold is a percentage from 0 to 100.");

        var entries = IndexedPixelFormat.GetEntries(palette);
        var usable = Math.Min(entries.Length, 1 << format.BitsPerPixel);

        if (usable == 0)
            throw new ArgumentException("The palette has no colors.", nameof(palette));

        _entries = entries.AsSpan(0, usable).ToArray();
        _alphaThreshold = (int)Math.Round(alphaThresholdPercent * 255 / 100, MidpointRounding.AwayFromZero);

        var best = int.MaxValue;

        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            var distance = entry.R * entry.R + entry.G * entry.G + entry.B * entry.B + entry.A * entry.A;

            if (distance < best)
            {
                best = distance;
                _transparentIndex = i;
            }
        }
    }

    public int Nearest(Rgba8888Pixel pixel)
    {
        if (pixel.A < _alphaThreshold)
            return _transparentIndex;

        var bestIndex = 0;
        var best = int.MaxValue;

        for (var i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            var red = entry.R - pixel.R;
            var green = entry.G - pixel.G;
            var blue = entry.B - pixel.B;
            var opacity = 255 - entry.A;
            var distance = red * red + green * green + blue * blue + opacity * opacity;

            if (distance < best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
