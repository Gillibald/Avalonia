namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// An orthogonal transform (rotation in 90 degree steps plus flips) applied on encode,
    /// or natively by a backend that supports it.
    /// </summary>
    /// <param name="Rotation">The clockwise rotation.</param>
    /// <param name="FlipHorizontal">Whether to mirror horizontally.</param>
    /// <param name="FlipVertical">Whether to mirror vertically.</param>
    public readonly record struct BitmapTransform(
        BitmapRotation Rotation,
        bool FlipHorizontal,
        bool FlipVertical)
    {
        /// <summary>
        /// Gets whether this transform changes nothing.
        /// </summary>
        public bool IsIdentity => Rotation == BitmapRotation.None && !FlipHorizontal && !FlipVertical;
    }

    /// <summary>
    /// A clockwise rotation in 90 degree steps.
    /// </summary>
    public enum BitmapRotation
    {
        /// <summary>No rotation.</summary>
        None = 0,

        /// <summary>Rotate 90 degrees clockwise.</summary>
        Rotate90 = 90,

        /// <summary>Rotate 180 degrees.</summary>
        Rotate180 = 180,

        /// <summary>Rotate 270 degrees clockwise.</summary>
        Rotate270 = 270,
    }
}
