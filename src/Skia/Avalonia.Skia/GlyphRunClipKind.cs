namespace Avalonia.Skia
{
    /// <summary>
    /// The clip a batched glyph run is drawn under, as counted by
    /// <see cref="DrawingContextImpl.GetClippedRunsOnThread"/>: the kind of the innermost clip
    /// pushed on the drawing context.
    /// </summary>
    internal enum GlyphRunClipKind
    {
        /// <summary>No clip pushed.</summary>
        None,

        /// <summary>
        /// A rectangle that the transform keeps axis-aligned and whose device edges lie on whole
        /// pixels.
        /// </summary>
        PixelAlignedRect,

        /// <summary>A rectangle that the transform keeps axis-aligned, with a device edge between pixels.</summary>
        FractionalRect,

        /// <summary>A region: whole device pixels, unaffected by the transform.</summary>
        Region,

        /// <summary>A rounded rectangle or a geometry, both clipped with antialiased edges.</summary>
        RoundedRectOrGeometry,

        /// <summary>A rectangle, rounded rectangle or geometry under a rotating or skewing transform.</summary>
        Transformed,
    }
}
