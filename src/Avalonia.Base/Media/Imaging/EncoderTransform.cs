using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The shared software path for <see cref="BitmapEncoder.Transform"/>: applies an
    /// orthogonal transform to a pixel snapshot in one pipeline pass, so every backend
    /// honors transform-on-encode whether or not its codec can transform natively.
    /// </summary>
    internal static class EncoderTransform
    {
        public static PixelBuffer Apply(PixelBuffer source, BitmapTransform transform)
        {
            if (transform.IsIdentity)
                return source;

            var orientation = PixelOrientations.FromTransform(transform);

            // Combinations can cancel out (a 180 degree rotation plus both flips).
            if (orientation == PixelOrientation.Normal)
                return source;

            using var framebuffer = source.Lock();

            var orientedSize = FusedPixelPipeline.GetOrientedSize(source.Size, orientation);
            var stride = PixelFormatHelper.GetMinRowBytes(source.Format, orientedSize.Width);
            var pixels = new byte[checked(stride * orientedSize.Height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                var plan = new FusedPlanExecution(new PixelRect(orientedSize), orientedSize,
                    source.Format, source.AlphaFormat, BitmapInterpolationMode.HighQuality,
                    orientation);

                FusedPixelPipeline.Run(framebuffer, plan, handle.AddrOfPinnedObject(), stride);
            }
            finally
            {
                handle.Free();
            }

            return PixelBuffer.TakeOwnership(pixels, orientedSize, stride, source.Format,
                source.AlphaFormat, source.Dpi);
        }
    }
}
