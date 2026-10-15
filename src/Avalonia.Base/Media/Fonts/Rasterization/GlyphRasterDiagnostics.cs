namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The running totals of <see cref="TextRasterCounters"/> of the calling thread in the current
    /// application, 0 when it has no font manager.
    /// </summary>
    internal static class GlyphRasterDiagnostics
    {
        /// <summary>Glyph outlines filled into coverage.</summary>
        public static long GlyphRasterizationsOnThread => TextRasterCounters.TryGetCurrent()?.GlyphRasterizations ?? 0;

        /// <summary>Glyph mask cache lookups that found the mask.</summary>
        public static long MaskCacheHitsOnThread => TextRasterCounters.TryGetCurrent()?.MaskCacheHits ?? 0;

        /// <summary>Glyph mask cache lookups that built the mask.</summary>
        public static long MaskCacheMissesOnThread => TextRasterCounters.TryGetCurrent()?.MaskCacheMisses ?? 0;

        /// <summary>Sprite sets laid out for a run.</summary>
        public static long SpriteSetBuildsOnThread => TextRasterCounters.TryGetCurrent()?.SpriteSetBuilds ?? 0;

        /// <summary>Sprite sets placed in an atlas and split into per-page batches.</summary>
        public static long AtlasBatchBuildsOnThread => TextRasterCounters.TryGetCurrent()?.AtlasBatchBuilds ?? 0;

        /// <summary>Glyph atlas lookups that found the entry.</summary>
        public static long AtlasHitsOnThread => TextRasterCounters.TryGetCurrent()?.AtlasHits ?? 0;

        /// <summary>Glyph atlas lookups that did not find the entry.</summary>
        public static long AtlasMissesOnThread => TextRasterCounters.TryGetCurrent()?.AtlasMisses ?? 0;

        /// <summary>Glyph masks written into an atlas page.</summary>
        public static long AtlasPlacementsOnThread => TextRasterCounters.TryGetCurrent()?.AtlasPlacements ?? 0;

        /// <summary>COLR v1 paint graphs rasterized into colour masks.</summary>
        public static long ColorMaskRasterizationsOnThread => TextRasterCounters.TryGetCurrent()?.ColorMaskRasterizations ?? 0;

        /// <summary>Colour glyphs drawn as vectors.</summary>
        public static long ColorGlyphVectorDrawsOnThread => TextRasterCounters.TryGetCurrent()?.ColorGlyphVectorDraws ?? 0;

        /// <summary>Backend images made of colour masks.</summary>
        public static long ColorFallbackImagesOnThread => TextRasterCounters.TryGetCurrent()?.ColorFallbackImages ?? 0;
    }
}
