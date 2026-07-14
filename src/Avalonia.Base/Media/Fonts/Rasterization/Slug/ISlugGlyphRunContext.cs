namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// The backend seam for the Slug vector tier — the sibling of
    /// <see cref="IAlphaGlyphMaskContext"/> for the draws the mask path cannot take: rotated,
    /// skewed, or very large text renders analytically from the size-independent per-typeface
    /// payloads instead of the backend's native glyph rasterizer. A context that does not
    /// implement this (or reports no support) keeps the native fallback.
    /// </summary>
    internal interface ISlugGlyphRunContext
    {
        /// <summary>
        /// Whether this context can render Slug payloads. GPU-backed contexts only: on the CPU
        /// raster pipeline the per-fragment curve evaluation measured ~750 µs per glyph draw,
        /// three orders beyond a mask blit, so software contexts always decline.
        /// </summary>
        bool SupportsSlugRendering { get; }

        /// <summary>
        /// Draws one glyph from its serialized payload. The baseline pen position is in the
        /// context's current (pre-transform) coordinates and <paramref name="tintArgb"/> is the
        /// straight (non-premultiplied) foreground; the context's ambient opacity applies on
        /// top, and the current transform carries rotation, skew, and zoom.
        /// </summary>
        void DrawSlugGlyph(SlugTexelStore store, in SlugGlyphPlacement placement,
            double baselineX, double baselineY, double fontRenderingEmSize, uint tintArgb);
    }
}
