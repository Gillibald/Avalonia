using System;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Record-time split: COLR glyphs draw through the typeface's own drawings (the "prefer our
    /// implementation" rule holds in every rasterization mode), while other stretches keep an
    /// ordinary glyph-run node. Scope differs by mode only for v0: under managed rasterization
    /// a glyph with v0 layers and no v1 paint graph stays in the run because the mask renderer
    /// composes those layers server-side more cheaply; under backend rasterization the blob would
    /// rasterize COLR itself, so v0 splits to drawings too. Direct <see cref="GlyphRun"/> draws bypass this splitter: under managed
    /// rasterization the renderer cuts those runs at the same glyphs when it draws them (see
    /// <see cref="ColorGlyphSegments"/>), and under backend rasterization the backend's native
    /// text handling draws them.
    /// </summary>
    /// <remarks>
    /// The drawings are those of the unsimulated face, drawn without the oblique shear: font
    /// simulations never apply to colour glyphs (see <see cref="GlyphTypeface.IsColorGlyph"/>).
    /// The one exception is accepted rather than routed around: a direct glyph run draw under
    /// backend rasterization reaches the backend's native text blob, and Skia skews the colour
    /// glyphs of an oblique face along with its outlines. Text layout always comes through
    /// here, so only direct draws see it.
    /// </remarks>
    internal static class ColorGlyphRunSplitter
    {
        public static bool IsManagedTextRasterization()
            => (AvaloniaLocator.Current.GetService<FontManagerOptions>()?.TextRasterizationMode
                ?? TextRasterizationDefaults.PlatformDefault) == TextRasterizationMode.Managed;

        /// <summary>
        /// Draws <paramref name="glyphRun"/> with its color glyphs replaced by their drawings.
        /// Returns <c>false</c> without drawing anything when the run contains no glyph this
        /// mode splits (or none with a resolvable drawing), so the caller keeps its single node.
        /// </summary>
        public static bool TryDraw(DrawingContext context, GlyphRun glyphRun, IBrush foreground)
        {
            var typeface = glyphRun.GlyphTypeface;
            var colr = typeface.ColorTable;
            var bitmaps = typeface.BitmapSource;

            if (colr is null && bitmaps is null)
            {
                return false;
            }

            // Managed rasterization composes v0 layers AND bitmap strikes server-side; only v1
            // paint graphs need the drawing split there. Backend rasterization splits all of
            // them, so the backend never rasterizes color or bitmap glyph content itself.
            var includeServerSideKinds = !IsManagedTextRasterization();

            if (!includeServerSideKinds && colr is not { HasV1Data: true })
            {
                return false;
            }

            bool IsSplitGlyph(ushort glyph)
                => includeServerSideKinds
                    ? (colr is not null &&
                       (colr.HasColorLayers(glyph) ||
                        (colr.HasV1Data && colr.TryGetBaseGlyphV1Record(glyph, out _)))) ||
                      (bitmaps?.HasGlyphImage(glyph) ?? false)
                    : IsV1Glyph(typeface, colr!, glyph);

            var infos = glyphRun.GlyphInfos;
            var hasSplitGlyph = false;

            for (var i = 0; i < infos.Count; i++)
            {
                if (IsSplitGlyph(infos[i].GlyphIndex) &&
                    typeface.GetGlyphDrawing(infos[i].GlyphIndex) is not null)
                {
                    hasSplitGlyph = true;
                    break;
                }
            }

            if (!hasSplitGlyph)
            {
                return false;
            }

            var scale = glyphRun.FontRenderingEmSize / typeface.Metrics.DesignEmHeight;
            var baseline = glyphRun.BaselineOrigin;
            var currentX = 0.0;
            var segmentStart = 0;
            var segmentStartX = 0.0;

            var drawingOptions = CreateDrawingOptions(foreground);

            for (var i = 0; i <= infos.Count; i++)
            {
                var splitHere = false;
                var info = default(GlyphInfo);

                if (i < infos.Count)
                {
                    info = infos[i];
                    splitHere = IsSplitGlyph(info.GlyphIndex) &&
                        typeface.GetGlyphDrawing(info.GlyphIndex) is not null;
                }

                if (i < infos.Count && !splitHere)
                {
                    currentX += info.GlyphAdvance;
                    continue;
                }

                FlushSegment(context, foreground, glyphRun, segmentStart, i, segmentStartX);

                if (i == infos.Count)
                {
                    break;
                }

                DrawGlyph(context, typeface, info.GlyphIndex, drawingOptions, scale, new Point(
                    baseline.X + currentX + info.GlyphOffset.X,
                    baseline.Y + info.GlyphOffset.Y));

                currentX += info.GlyphAdvance;
                segmentStart = i + 1;
                segmentStartX = currentX;
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="glyph"/> has a COLR v1 paint graph and a palette to resolve it
        /// with. No mask tier renders such a glyph: it draws through its drawing. A glyph that also
        /// has v0 layers (Segoe UI Emoji has both for every emoji) draws its paint graph too, since
        /// a renderer that supports v1 prefers it over the v0 layers. Without a CPAL table the
        /// glyph has no drawing and renders as its outline, like a v0 glyph without one.
        /// </summary>
        internal static bool IsV1Glyph(GlyphTypeface typeface, Tables.Colr.ColrTable colr, ushort glyph)
            => colr.HasV1Data && typeface.ColorPaletteTable is not null && glyph < typeface.GlyphCount &&
               colr.TryGetBaseGlyphV1Record(glyph, out _);

        /// <summary>
        /// Draws a run cut at its v1 glyphs: the stretches between them as runs of their own
        /// through <paramref name="context"/>'s glyph run path, and each v1 glyph through its
        /// drawing at the run's pen, in run order.
        /// </summary>
        internal static void DrawSegments(IDrawingContextImpl context, ColorGlyphSegments segments,
            IBrush foreground)
        {
            PlatformDrawingContext? colorContext = null;
            GlyphDrawingOptions? drawingOptions = null;

            try
            {
                foreach (var segment in segments.Items)
                {
                    if (segment.Run is { } run)
                    {
                        context.DrawGlyphRun(foreground, run);
                        continue;
                    }

                    if (colorContext is null)
                    {
                        colorContext = new PlatformDrawingContext(context, ownsImpl: false);
                        drawingOptions = CreateDrawingOptions(foreground);
                    }

                    DrawGlyph(colorContext, segments.Typeface, segment.Glyph, drawingOptions, segments.Scale,
                        segment.Pen);
                }
            }
            finally
            {
                colorContext?.Dispose();
            }
        }

        /// <summary>
        /// Whether <paramref name="glyph"/> draws through its colour drawing rather than as an
        /// outline: a colour glyph whose drawing resolves.
        /// </summary>
        internal static bool IsDrawnAsColor(GlyphTypeface typeface, ushort glyph)
            => typeface.IsColorGlyph(glyph) && typeface.GetGlyphDrawing(glyph) is not null;

        /// <summary>
        /// Draws only the colour glyphs of <paramref name="run"/> through their drawings, at the
        /// run's positions; the caller draws every other glyph (see <see cref="IsDrawnAsColor"/>).
        /// Used where a managed run falls back to plain outlines, which carry no colour.
        /// </summary>
        internal static void DrawColorGlyphs(DrawingContext context, ManagedGlyphRunImpl run, IBrush? foreground)
        {
            var typeface = run.GlyphTypeface;
            var scale = run.FontRenderingEmSize / typeface.Metrics.DesignEmHeight;
            var baseline = run.BaselineOrigin;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            GlyphDrawingOptions? drawingOptions = null;
            var hasOptions = false;

            for (var i = 0; i < indices.Length; i++)
            {
                if (!IsDrawnAsColor(typeface, indices[i]))
                {
                    continue;
                }

                if (!hasOptions)
                {
                    drawingOptions = CreateDrawingOptions(foreground);
                    hasOptions = true;
                }

                DrawGlyph(context, typeface, indices[i], drawingOptions, scale,
                    new Point(baseline.X + positions[i * 2], baseline.Y + positions[i * 2 + 1]));
            }
        }

        // A solid foreground rides into the paint resolver so CPAL 0xFFFF entries follow the
        // text color (with the brush opacity folded into its alpha); built once per run.
        private static GlyphDrawingOptions? CreateDrawingOptions(IBrush? foreground)
        {
            if (foreground is not ISolidColorBrush solid)
            {
                return null;
            }

            var color = solid.Color;
            var alpha = (byte)Math.Clamp(color.A * solid.Opacity + 0.5, 0, 255);

            return new GlyphDrawingOptions
            {
                Foreground = Color.FromArgb(alpha, color.R, color.G, color.B),
            };
        }

        private static void DrawGlyph(DrawingContext context, GlyphTypeface typeface, ushort glyph,
            GlyphDrawingOptions? drawingOptions, double scale, Point pen)
        {
            // Fetched with the run's foreground so sentinel palette entries resolve to it
            // (foreground-bearing drawings build uncached; the plain probe stayed on the cached
            // path). Drawings render in font design units (the Y-flip is internal): scale to
            // the run's em size and land the local origin on the pen.
            var drawing = typeface.GetGlyphDrawing(glyph, drawingOptions)!;

            using (context.PushTransform(
                Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(pen.X, pen.Y)))
            {
                drawing.Draw(context, default);
            }
        }

        private static void FlushSegment(DrawingContext context, IBrush foreground, GlyphRun run,
            int start, int end, double startX)
        {
            var length = end - start;

            if (length <= 0)
            {
                return;
            }

            var infos = run.GlyphInfos;
            var slice = new GlyphInfo[length];

            for (var i = 0; i < length; i++)
            {
                slice[i] = infos[start + i];
            }

            // Draw-only sub-run with empty characters: hit-testing and metrics stay with the
            // original run, and the recorded node clones the platform impl, so disposing here
            // is safe and required.
            using var subRun = new GlyphRun(run.GlyphTypeface, run.FontRenderingEmSize,
                default, slice, new Point(run.BaselineOrigin.X + startX, run.BaselineOrigin.Y),
                run.BiDiLevel);

            context.DrawGlyphRun(foreground, subRun);
        }
    }
}
