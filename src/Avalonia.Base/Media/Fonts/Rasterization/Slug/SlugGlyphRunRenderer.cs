using System;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// Draws a <see cref="ManagedGlyphRunImpl"/> through the Slug vector tier. Sits behind the
    /// mask path in the dispatch order, so it only ever sees the draws that triage rejected —
    /// rotation, skew, and sizes past the mask ceiling — and takes them when the context is
    /// GPU-backed and every glyph in the run has (or can build) a payload.
    /// </summary>
    internal static class SlugGlyphRunRenderer
    {
        /// <summary>
        /// Attempts the run. Returns <c>false</c> when this tier cannot take it — unsupported
        /// context, non-solid foreground, color or outline-less typeface, degenerate transform,
        /// or any glyph declining payload realization — and the caller falls back to its native
        /// path. The tier is simply on wherever the GPU context supports it.
        /// Returns <c>true</c> when handled, including the nothing-to-draw cases.
        /// </summary>
        public static bool TryDraw(ISlugGlyphRunContext context, Matrix transform,
            ManagedGlyphRunImpl run, IBrush? foreground)
        {
            if (!context.SupportsSlugRendering)
            {
                return false;
            }


            if (foreground is not ISolidColorBrush solid)
            {
                return false;
            }

            var typeface = run.GlyphTypeface;

            // Color glyphs keep their existing paths: masks serve them axis-aligned, the native
            // fallback serves them under free transforms. Slug draws monochrome outlines only.
            if (typeface.OutlineType == GlyphOutlineType.None ||
                typeface.ColorTable is not null ||
                typeface.BitmapSource is not null)
            {
                return false;
            }

            // A singular transform cannot produce the per-draw em footprint; nothing sensible
            // to draw anyway.
            var determinant = transform.M11 * transform.M22 - transform.M12 * transform.M21;

            if (determinant == 0 || !double.IsFinite(determinant))
            {
                return false;
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            var store = typeface.SlugStore;
            var indices = run.GlyphIndices;

            // All or nothing: a run mixing Slug coverage with native rasterization would show
            // two different edge treatments side by side. Realizing everything up front also
            // keeps the store's version stable across the draw loop below.
            for (var i = 0; i < indices.Length; i++)
            {
                if (!store.TryRealize(typeface, indices[i], out _))
                {
                    return false;
                }
            }

            var tint = ((uint)alpha << 24) |
                ((uint)solid.Color.R << 16) | ((uint)solid.Color.G << 8) | solid.Color.B;

            // The context draws the whole run from its cached artifact; a false return (its
            // resources failed to realize) sends the run to the caller's native fallback
            // instead of rendering nothing.
            return context.TryDrawSlugRun(run, store, tint);
        }
    }
}
