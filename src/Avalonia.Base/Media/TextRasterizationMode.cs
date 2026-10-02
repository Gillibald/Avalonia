namespace Avalonia.Media
{
    /// <summary>
    /// Selects which engine rasterizes glyphs into pixels.
    /// </summary>
    /// <remarks>
    /// The default depends on the platform; see <see cref="FontManagerOptions.TextRasterizationMode"/>.
    /// An application can choose either mode on any platform.
    /// </remarks>
    public enum TextRasterizationMode
    {
        /// <summary>
        /// The render backend's own text stack rasterizes glyphs (on Skia: <c>SKTextBlob</c>
        /// through the backend's font machinery). The default on platforms where managed
        /// rasterization has not been measured and tuned yet: Windows on ARM64, Linux, macOS,
        /// iOS, Android and other architectures.
        /// </summary>
        Backend = 0,

        /// <summary>
        /// Avalonia's managed rasterizer produces glyph coverage masks from the font tables and
        /// the backend only composites them. Fonts without outline tables (bitmap-strike or
        /// SVG-only) always fall back to <see cref="Backend"/>. The default on Windows x64 and in
        /// the browser (WebAssembly).
        /// </summary>
        Managed = 1,
    }
}
