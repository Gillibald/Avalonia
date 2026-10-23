namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// Options for converting pixels to another format.
    /// </summary>
    public sealed class PixelConversionOptions
    {
        /// <summary>
        /// Gets the palette that pixels are matched against when converting to an indexed
        /// format. Required for indexed targets unless the source is already in that format.
        /// </summary>
        public BitmapPalette? Palette { get; init; }

        /// <summary>
        /// Gets the alpha threshold for indexed targets, as a percentage from 0 to 100:
        /// pixels less opaque than this take the palette entry closest to transparent black.
        /// </summary>
        public double AlphaThresholdPercent { get; init; }
    }
}
