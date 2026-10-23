using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Pixel operations on framebuffer views whose memory the caller already holds, such as
    /// a locked <see cref="WriteableBitmap"/>.
    /// </summary>
    public static class LockedFramebufferExtensions
    {
        /// <summary>
        /// Converts a rectangle of the framebuffer into caller memory, starting at the
        /// destination origin.
        /// </summary>
        /// <param name="source">The framebuffer to read.</param>
        /// <param name="sourcePalette">The palette of an indexed framebuffer; null otherwise.</param>
        /// <param name="sourceRect">The rectangle to copy, within the framebuffer.</param>
        /// <param name="destination">The destination memory.</param>
        /// <param name="destinationSize">The number of bytes available at <paramref name="destination"/>.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <param name="format">The destination pixel format.</param>
        /// <param name="alphaFormat">The destination alpha format.</param>
        /// <param name="options">Palette and alpha threshold for indexed destinations.</param>
        /// <remarks>
        /// Copying to the source's own format and alpha format moves the bits unchanged.
        /// Validation happens before any byte is written.
        /// </remarks>
        public static void CopyPixels(this ILockedFramebuffer source, BitmapPalette? sourcePalette, PixelRect sourceRect,
            IntPtr destination, int destinationSize, int destinationStride, PixelFormat format, AlphaFormat alphaFormat,
            PixelConversionOptions? options = null)
            => PixelOperations.CopyPixels(source, sourcePalette, sourceRect, destination, destinationSize,
                destinationStride, format, alphaFormat, options);
    }
}
