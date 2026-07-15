namespace Avalonia.Media
{
    /// <summary>
    /// Selects which engine rasterizes glyphs into pixels.
    /// </summary>
    public enum TextRasterizationMode
    {
        /// <summary>
        /// The render backend's own text stack rasterizes glyphs (on Skia: <c>SKTextBlob</c>
        /// through the backend's font machinery). The escape hatch; the default is
        /// <see cref="Managed"/>.
        /// </summary>
        Backend = 0,

        /// <summary>
        /// Avalonia's managed rasterizer produces glyph coverage masks from the font tables and
        /// the backend only composites them. Fonts without outline tables (bitmap-strike or
        /// SVG-only) always fall back to <see cref="Backend"/>. Experimental; subpixel
        /// antialiasing renders as grayscale on this path.
        /// </summary>
        Managed = 1,
    }
}
