using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// An imaging backend capability: maps a decode to an indexed
    /// <see cref="BitmapDecodeOptions.TargetFormat"/> with <see cref="BitmapDecodeOptions.Palette"/>,
    /// or dithers one to <see cref="PixelFormats.BlackWhite"/>, <see cref="PixelFormats.Gray2"/> or
    /// <see cref="PixelFormats.Gray4"/>, inside its own decode. Without it, or when it declines, the
    /// core asks the backend for straight BGRA and maps the frame afterwards.
    /// </summary>
    /// <remarks>
    /// A backend that accepts receives the options as the caller set them and must produce what
    /// the core mapping would: the same indices, the given palette and the target format. The
    /// core validates <see cref="BitmapDecodeOptions.AlphaThresholdPercent"/> before asking.
    /// </remarks>
    [Unstable]
    public interface ISupportsPaletteDecode
    {
        /// <summary>
        /// Whether the backend's decode of <paramref name="stream"/> with <paramref name="options"/>
        /// maps to the palette, or dithers, itself. It may read the header to decide, for example
        /// whether the image needs reorienting first, and must leave the stream at the position it
        /// found it.
        /// </summary>
        bool DecodesToPalette(Stream stream, BitmapDecodeOptions options);
    }
}
