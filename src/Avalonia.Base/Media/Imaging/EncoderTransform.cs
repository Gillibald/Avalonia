using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The shared software path for <see cref="BitmapEncoder.Transform"/>: applies an
    /// orthogonal transform to the source pixels in one remapping pass, so every backend
    /// honors transform-on-encode whether or not its codec can transform natively.
    /// </summary>
    internal static class EncoderTransform
    {
        /// <summary>
        /// Applies the transform to a snapshot, returning the same instance when the
        /// transform changes nothing.
        /// </summary>
        public static PixelBuffer Apply(PixelBuffer source, BitmapTransform transform)
        {
            if (transform.IsIdentity || PixelOrientations.FromTransform(transform) == PixelOrientation.Normal)
                return source;

            using var framebuffer = source.Lock();

            return Apply(framebuffer, transform)!;
        }

        /// <summary>
        /// Applies the transform to a framebuffer view, returning a new snapshot holding
        /// the transformed pixels - or null when the transform changes nothing and the
        /// source view can be consumed as is.
        /// </summary>
        public static PixelBuffer? Apply(ILockedFramebuffer source, BitmapTransform transform)
        {
            if (transform.IsIdentity)
                return null;

            var orientation = PixelOrientations.FromTransform(transform);

            // Combinations can cancel out (a 180 degree rotation plus both flips).
            if (orientation == PixelOrientation.Normal)
                return null;

            var orientedSize = FusedPixelPipeline.GetOrientedSize(source.Size, orientation);
            var stride = PixelFormatArithmetic.GetMinRowBytes(source.Format, orientedSize.Width);
            var pixels = new byte[checked(stride * orientedSize.Height)];

            // The remap moves pixels without converting them, so every format keeps its exact bits.
            unsafe
            {
                fixed (byte* address = pixels)
                    PixelLayout.Orient(source, orientation, address, stride);
            }

            return PixelBuffer.TakeOwnership(pixels, orientedSize, stride, source.Format,
                source.AlphaFormat, source.Dpi);
        }
    }
}
