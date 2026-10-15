namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Running totals of the managed glyph rasterization work one thread did for one application
    /// (<see cref="TextRasterThreadState.Counters"/>), for profiling tools and tests. The totals only
    /// grow; a reader takes differences around the span it measures, a frame say. They are plain
    /// single-writer increments, cheap enough to stay compiled into every build, and nothing reads
    /// them back to decide anything.
    /// </summary>
    internal sealed class TextRasterCounters
    {
        /// <summary>Glyph outlines filled into coverage, upright or transformed, cached or transient.</summary>
        public long GlyphRasterizations;

        /// <summary>Glyph mask cache lookups that found the mask.</summary>
        public long MaskCacheHits;

        /// <summary>Glyph mask cache lookups that built the mask.</summary>
        public long MaskCacheMisses;

        /// <summary>Sprite sets laid out for a run at a scale, phase and transform.</summary>
        public long SpriteSetBuilds;

        /// <summary>Sprite sets placed in an atlas and split into per-page batches.</summary>
        public long AtlasBatchBuilds;

        /// <summary>Glyph atlas lookups that found the glyph's entry.</summary>
        public long AtlasHits;

        /// <summary>Glyph atlas lookups that did not find the glyph's entry.</summary>
        public long AtlasMisses;

        /// <summary>Glyph masks written into an atlas page, each a new version of the page.</summary>
        public long AtlasPlacements;

        /// <summary>COLR v1 paint graphs rasterized into colour masks.</summary>
        public long ColorMaskRasterizations;

        /// <summary>Colour glyphs drawn as vectors, from their recordings or live drawings.</summary>
        public long ColorGlyphVectorDraws;

        /// <summary>Backend images made of colour masks for draws that cannot write the surface directly.</summary>
        public long ColorFallbackImages;

        /// <summary>
        /// The counters of the rendering backend that draws for this thread, made by the backend on
        /// first use; a type the backend owns, so this assembly needs no knowledge of it.
        /// </summary>
        public object? BackendCounters;

        /// <summary>
        /// The counters of the calling thread in the current application, or <c>null</c> when it has
        /// no font manager. For code that has no raster state at hand; drawing code passes its own.
        /// </summary>
        public static TextRasterCounters? TryGetCurrent() => TextRasterThreadState.TryGetCurrent()?.Counters;
    }
}
