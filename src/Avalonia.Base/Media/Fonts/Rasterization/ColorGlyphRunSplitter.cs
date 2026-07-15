using System;
using Avalonia.Media.TextFormatting;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Record-time split: COLR glyphs draw through the typeface's own drawings (the "prefer our
    /// implementation" rule holds in every rasterization mode), while other stretches keep an
    /// ordinary glyph-run node. Scope differs by mode only for v0: under managed rasterization
    /// v0 stays in the run because the mask renderer composes those layers server-side more
    /// cheaply; under backend rasterization the blob would rasterize COLR itself, so v0 splits
    /// to drawings too. Direct <see cref="GlyphRun"/> draws that bypass this splitter still
    /// render correctly via the renderer's or backend's native handling.
    /// </summary>
    internal static class ColorGlyphRunSplitter
    {
        public static bool IsManagedTextRasterization()
            => (AvaloniaLocator.Current.GetService<FontManagerOptions>()?.TextRasterizationMode
                ?? TextRasterizationMode.Managed) == TextRasterizationMode.Managed;

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
                    : colr!.TryGetBaseGlyphV1Record(glyph, out _) && !colr.TryGetBaseGlyphRecord(glyph, out _);

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

            // A solid foreground rides into the paint resolver so CPAL 0xFFFF entries follow the
            // text color (with the brush opacity folded into its alpha); built once per run.
            GlyphDrawingOptions? drawingOptions = null;

            if (foreground is ISolidColorBrush solid)
            {
                var color = solid.Color;
                var alpha = (byte)Math.Clamp(color.A * solid.Opacity + 0.5, 0, 255);
                drawingOptions = new GlyphDrawingOptions
                {
                    Foreground = Color.FromArgb(alpha, color.R, color.G, color.B),
                };
            }

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

                // Fetched with the run's foreground so sentinel palette entries resolve to it
                // (foreground-bearing drawings build uncached; the plain probe above stayed on
                // the cached path). Drawings render in font design units (the Y-flip is
                // internal): scale to the run's em size and land the local origin on the pen.
                var drawing = typeface.GetGlyphDrawing(info.GlyphIndex, drawingOptions)!;
                var pen = new Point(
                    baseline.X + currentX + info.GlyphOffset.X,
                    baseline.Y + info.GlyphOffset.Y);

                using (context.PushTransform(
                    Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(pen.X, pen.Y)))
                {
                    drawing.Draw(context, default);
                }

                currentX += info.GlyphAdvance;
                segmentStart = i + 1;
                segmentStartX = currentX;
            }

            return true;
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
