using System;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Algebra over the eight orthogonal pixel transforms: the EXIF orientations form
    /// the dihedral group of the square, so every rotate/flip combination reduces to
    /// exactly one <see cref="PixelOrientation"/>.
    /// </summary>
    internal static class PixelOrientations
    {
        /// <summary>
        /// Reduces an encoder transform to a single orientation. The transform applies
        /// its parts in a fixed order: rotation first, then the horizontal flip, then
        /// the vertical flip.
        /// </summary>
        public static PixelOrientation FromTransform(BitmapTransform transform)
        {
            var orientation = transform.Rotation switch
            {
                BitmapRotation.None => PixelOrientation.Normal,
                BitmapRotation.Rotate90 => PixelOrientation.Rotate90,
                BitmapRotation.Rotate180 => PixelOrientation.Rotate180,
                BitmapRotation.Rotate270 => PixelOrientation.Rotate270,
                _ => throw new ArgumentOutOfRangeException(nameof(transform)),
            };

            if (transform.FlipHorizontal)
                orientation = Compose(orientation, PixelOrientation.FlipHorizontal);

            if (transform.FlipVertical)
                orientation = Compose(orientation, PixelOrientation.FlipVertical);

            return orientation;
        }

        /// <summary>
        /// Returns the orientation equivalent to applying <paramref name="first"/> and
        /// then <paramref name="then"/>.
        /// </summary>
        public static PixelOrientation Compose(PixelOrientation first, PixelOrientation then)
        {
            var (swapF, negXF, negYF) = Decompose(first);
            var (swapG, negXG, negYG) = Decompose(then);

            // Normal form: negate after swapping. Pulling the first transform's
            // negations through the second's swap permutes them.
            var swap = swapF ^ swapG;
            var (negXFp, negYFp) = swapG ? (negYF, negXF) : (negXF, negYF);

            return Recompose(swap, negXG ^ negXFp, negYG ^ negYFp);
        }

        // Each orientation maps input to output coordinates by optionally swapping the
        // axes and then negating (mirroring) the output axes.
        private static (bool Swap, bool NegX, bool NegY) Decompose(PixelOrientation orientation) => orientation switch
        {
            PixelOrientation.Normal => (false, false, false),
            PixelOrientation.FlipHorizontal => (false, true, false),
            PixelOrientation.Rotate180 => (false, true, true),
            PixelOrientation.FlipVertical => (false, false, true),
            PixelOrientation.Transpose => (true, false, false),
            PixelOrientation.Rotate90 => (true, true, false),
            PixelOrientation.Transverse => (true, true, true),
            PixelOrientation.Rotate270 => (true, false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };

        private static PixelOrientation Recompose(bool swap, bool negX, bool negY) => (swap, negX, negY) switch
        {
            (false, false, false) => PixelOrientation.Normal,
            (false, true, false) => PixelOrientation.FlipHorizontal,
            (false, true, true) => PixelOrientation.Rotate180,
            (false, false, true) => PixelOrientation.FlipVertical,
            (true, false, false) => PixelOrientation.Transpose,
            (true, true, false) => PixelOrientation.Rotate90,
            (true, true, true) => PixelOrientation.Transverse,
            (true, false, true) => PixelOrientation.Rotate270,
        };
    }
}
