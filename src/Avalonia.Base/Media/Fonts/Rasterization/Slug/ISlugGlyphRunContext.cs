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
        /// Draws a whole run whose glyphs are already realized in <paramref name="store"/>,
        /// reusing the run's cached artifact (per-glyph shaders and rects) when the current
        /// transform's pixel-footprint bucket and the store version still match — so
        /// same-transform redraws, translations, and foreground changes cost no rebuilds, and
        /// zoom or rotation steps pay only a cheap builder pass. <paramref name="tintArgb"/> is
        /// the straight (non-premultiplied) foreground applied at the paint level; the
        /// context's ambient opacity applies on top. Returns false when the context cannot
        /// realize its resources — the caller then falls back to its native path, so a failure
        /// can never render as missing text.
        /// </summary>
        bool TryDrawSlugRun(ManagedGlyphRunImpl run, SlugTexelStore store, uint tintArgb);
    }
}
