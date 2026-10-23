using System;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// An imaging backend capability: converts pixels with the backend's own converter, for
    /// backends whose conversions must match a reference such as the Windows Imaging
    /// Component exactly. Without it, or when it declines, the core converter is used.
    /// </summary>
    /// <remarks>
    /// The core validates every argument before calling, and copies between identical
    /// formats itself, so the backend is only asked for real conversions.
    /// </remarks>
    [Unstable]
    public interface ISupportsPixelConversion
    {
        /// <summary>
        /// Converts a rectangle of the source into the destination, starting at the
        /// destination origin.
        /// </summary>
        /// <param name="source">The pixels to convert.</param>
        /// <param name="sourcePalette">The palette of an indexed source; null otherwise.</param>
        /// <param name="sourceRect">The rectangle to convert, within the source.</param>
        /// <param name="destination">The destination memory.</param>
        /// <param name="destinationSize">The number of bytes available at <paramref name="destination"/>.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        /// <returns>
        /// True when the backend converted the pixels; false to let the core converter do it.
        /// Return false before writing anything.
        /// </returns>
        bool TryCopyPixels(ILockedFramebuffer source, BitmapPalette? sourcePalette, PixelRect sourceRect,
            IntPtr destination, long destinationSize, int destinationStride, PixelFormat format,
            AlphaFormat alphaFormat, PixelConversionOptions? options);
    }
}
