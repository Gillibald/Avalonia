using Avalonia.Media;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Glyph paths taken from <see cref="GlyphTypeface.GetGlyphOutline"/> instead of the Skia
    /// typeface, for the typefaces whose Skia face draws the wrong outlines.
    /// </summary>
    internal static class ManagedGlyphOutlines
    {
        /// <summary>
        /// Whether Skia must not rasterize <paramref name="typeface"/> from its own face. SkiaSharp
        /// cannot create a typeface at variation coordinates, so the Skia face of a varied clone
        /// is the default instance; the managed outlines carry the clone's variation and
        /// simulations. A typeface Skia cannot load at all has no face of its own to draw from.
        /// </summary>
        public static bool AreRequired(GlyphTypeface typeface)
            => typeface.OutlineType != GlyphOutlineType.None &&
               (!typeface.VariationPosition.IsDefault || !typeface.TryGetPlatformTypeface(out _));

        /// <summary>
        /// Creates an empty path with the fill rule glyph outlines are designed for: contours of
        /// variable glyphs overlap, so even-odd filling would punch holes where they meet.
        /// </summary>
        public static SKPath CreatePath() => new() { FillType = SKPathFillType.Winding };

        /// <summary>
        /// Appends the outline of <paramref name="glyph"/> at an em size of
        /// <paramref name="emSize"/>, with its origin on the baseline point
        /// (<paramref name="x"/>, <paramref name="y"/>).
        /// </summary>
        public static void AddGlyph(SKPath path, GlyphTypeface typeface, double emSize, ushort glyph,
            float x, float y)
        {
            var outline = typeface.GetGlyphOutline(glyph);

            if (outline is ImmutableGeometryImpl immutable)
            {
                outline = immutable.Inner;
            }

            if (outline is not GeometryImpl { FillPath: { IsEmpty: false } glyphPath })
            {
                return;
            }

            // Outlines are in font design units with y up.
            var scale = (float)(emSize / typeface.Metrics.DesignEmHeight);
            var matrix = SKMatrix.CreateScale(scale, -scale).PostConcat(SKMatrix.CreateTranslation(x, y));

            path.AddPath(glyphPath, in matrix);
        }
    }
}
