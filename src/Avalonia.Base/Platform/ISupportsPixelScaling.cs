using System;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// An imaging backend capability: scales pixels as the reference a format profile names,
    /// for backends whose results must match it exactly, such as WIC's Fant scaler for
    /// <see cref="BitmapFormatProfile.Wic"/>. Without it, or when it declines, the core filters
    /// are used.
    /// </summary>
    /// <remarks>
    /// The core validates every argument before calling and asks only for real resizes of
    /// non-indexed pixels into the source's own format.
    /// </remarks>
    [Unstable]
    public interface ISupportsPixelScaling
    {
        /// <summary>
        /// Scales the whole source to <paramref name="targetSize"/> into the destination, in the
        /// source's pixel and alpha format.
        /// </summary>
        /// <param name="source">The pixels to scale.</param>
        /// <param name="targetSize">The size to scale to.</param>
        /// <param name="interpolation">The interpolation the caller asked for.</param>
        /// <param name="profile">The reference whose scaling to reproduce.</param>
        /// <param name="destination">The destination memory.</param>
        /// <param name="destinationSize">The number of bytes available at <paramref name="destination"/>.</param>
        /// <param name="destinationStride">The number of bytes per destination row.</param>
        /// <returns>
        /// True when the backend scaled the pixels; false to let the core filters do it. Return
        /// false before writing anything.
        /// </returns>
        bool TryScalePixels(ILockedFramebuffer source, PixelSize targetSize, BitmapInterpolationMode interpolation,
            BitmapFormatProfile profile, IntPtr destination, long destinationSize, int destinationStride);
    }
}
