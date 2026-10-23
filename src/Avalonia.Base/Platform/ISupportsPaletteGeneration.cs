using Avalonia.Media.Imaging;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// An imaging backend capability: builds optimal palettes with the backend's own
    /// quantizer. Without it, the core median-cut quantizer is used.
    /// </summary>
    [Unstable]
    public interface ISupportsPaletteGeneration
    {
        /// <summary>
        /// Builds a palette of at most <paramref name="maxColors"/> entries for the pixels.
        /// </summary>
        /// <param name="source">The pixels to quantize.</param>
        /// <param name="sourcePalette">The palette of an indexed source; null otherwise.</param>
        /// <param name="maxColors">The maximum number of entries, from 1 to 256, including a transparent one.</param>
        /// <param name="addTransparentColor">Whether the last entry is transparent.</param>
        BitmapPalette CreateOptimalPalette(ILockedFramebuffer source, BitmapPalette? sourcePalette, int maxColors,
            bool addTransparentColor);
    }
}
