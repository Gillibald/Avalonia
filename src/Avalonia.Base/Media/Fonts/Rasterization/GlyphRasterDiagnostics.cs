using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Running totals of the managed glyph rasterization work done on the calling thread, for
    /// profiling tools and tests. The totals only grow; a reader takes differences around the
    /// span it measures, a frame say. They are plain per-thread increments, cheap enough to stay
    /// compiled into every build, and nothing reads them back to decide anything.
    /// </summary>
    internal static class GlyphRasterDiagnostics
    {
        [ThreadStatic]
        private static long t_glyphRasterizations;

        [ThreadStatic]
        private static long t_maskCacheHits;

        [ThreadStatic]
        private static long t_maskCacheMisses;

        [ThreadStatic]
        private static long t_spriteSetBuilds;

        [ThreadStatic]
        private static long t_atlasBatchBuilds;

        [ThreadStatic]
        private static long t_atlasHits;

        [ThreadStatic]
        private static long t_atlasMisses;

        [ThreadStatic]
        private static long t_atlasPlacements;

        [ThreadStatic]
        private static long t_colorMaskRasterizations;

        [ThreadStatic]
        private static long t_colorGlyphVectorDraws;

        /// <summary>Glyph outlines filled into coverage, upright or transformed, cached or transient.</summary>
        public static long GlyphRasterizationsOnThread => t_glyphRasterizations;

        /// <summary>Glyph mask cache lookups that found the mask.</summary>
        public static long MaskCacheHitsOnThread => t_maskCacheHits;

        /// <summary>Glyph mask cache lookups that built the mask.</summary>
        public static long MaskCacheMissesOnThread => t_maskCacheMisses;

        /// <summary>Sprite sets laid out for a run at a scale, phase and transform.</summary>
        public static long SpriteSetBuildsOnThread => t_spriteSetBuilds;

        /// <summary>Sprite sets placed in an atlas and split into per-page batches.</summary>
        public static long AtlasBatchBuildsOnThread => t_atlasBatchBuilds;

        /// <summary>Glyph atlas lookups that found the glyph's entry.</summary>
        public static long AtlasHitsOnThread => t_atlasHits;

        /// <summary>Glyph atlas lookups that did not find the glyph's entry.</summary>
        public static long AtlasMissesOnThread => t_atlasMisses;

        /// <summary>Glyph masks written into an atlas page, each a new version of the page.</summary>
        public static long AtlasPlacementsOnThread => t_atlasPlacements;

        /// <summary>COLR v1 paint graphs rasterized into colour masks.</summary>
        public static long ColorMaskRasterizationsOnThread => t_colorMaskRasterizations;

        /// <summary>Colour glyphs drawn as vectors, from their recordings or live drawings.</summary>
        public static long ColorGlyphVectorDrawsOnThread => t_colorGlyphVectorDraws;

        internal static void CountGlyphRasterization() => t_glyphRasterizations++;

        internal static void CountColorMaskRasterization() => t_colorMaskRasterizations++;

        internal static void CountColorGlyphVectorDraw() => t_colorGlyphVectorDraws++;

        internal static void CountMaskCacheHit() => t_maskCacheHits++;

        internal static void CountMaskCacheMiss() => t_maskCacheMisses++;

        internal static void CountSpriteSetBuild() => t_spriteSetBuilds++;

        internal static void CountAtlasBatchBuild() => t_atlasBatchBuilds++;

        internal static void CountAtlasLookup(bool hit)
        {
            if (hit)
            {
                t_atlasHits++;
            }
            else
            {
                t_atlasMisses++;
            }
        }

        internal static void CountAtlasPlacement() => t_atlasPlacements++;
    }
}
