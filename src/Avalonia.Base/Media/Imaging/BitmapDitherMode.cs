namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// How a conversion to a format with few colors (an indexed format,
    /// <see cref="Platform.PixelFormats.BlackWhite"/>, <see cref="Platform.PixelFormats.Gray2"/> or
    /// <see cref="Platform.PixelFormats.Gray4"/>) handles the difference between a pixel and the color it gets.
    /// </summary>
    public enum BitmapDitherMode
    {
        /// <summary>
        /// Each pixel takes its nearest color.
        /// </summary>
        None,

        /// <summary>
        /// The difference is diffused to the neighboring pixels the way WIC's
        /// <c>WICBitmapDitherTypeErrorDiffusion</c> does it, which WPF's <c>FormatConvertedBitmap</c> uses.
        /// </summary>
        ErrorDiffusion,
    }
}
