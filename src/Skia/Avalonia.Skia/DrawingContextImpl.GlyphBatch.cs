using System;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        [ThreadStatic]
        private static int t_pageImagesCreated;

        [ThreadStatic]
        private static int t_atlasDraws;

        /// <summary>
        /// The number of atlas page images made on this thread, each a texture upload on a GPU
        /// context; for tests.
        /// </summary>
        internal static int PageImagesCreatedOnThread => t_pageImagesCreated;

        /// <summary>The number of atlas draw calls issued on this thread; for tests.</summary>
        internal static int AtlasDrawsOnThread => t_atlasDraws;

        /// <summary>
        /// Whether glyph atlas draws on this GPU context are collected across glyph runs and
        /// drawn with one call per page and colour. Not implemented yet.
        /// </summary>
        internal bool BatchesGlyphAtlasDraws { get; set; } = true;

        /// <summary>Draws the pending glyph batch. Not implemented yet.</summary>
        internal void FlushGlyphBatch()
        {
        }
    }
}
