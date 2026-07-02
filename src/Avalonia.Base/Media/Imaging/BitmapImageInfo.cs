using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Header-only image facts, produced without decoding any pixels.
    /// </summary>
    /// <param name="FormatName">The container format name, matching <see cref="IBitmapCodecInfo.FormatName"/>.</param>
    /// <param name="PixelSize">The image size in device pixels.</param>
    /// <param name="Dpi">The DPI stored in the header, or null when the format carries none.</param>
    /// <param name="PixelFormat">The declared native pixel format, or null when the header does not say.</param>
    /// <param name="FrameCount">The frame count, or null when unknown without a full parse (forward-only GIF).</param>
    /// <param name="HasAlpha">Whether the image carries alpha, or null when the header does not say.</param>
    public readonly record struct BitmapImageInfo(
        string FormatName,
        PixelSize PixelSize,
        Vector? Dpi,
        PixelFormat? PixelFormat,
        int? FrameCount,
        bool? HasAlpha);
}
