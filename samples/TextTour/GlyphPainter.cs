using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour
{
    /// <summary>
    /// Draws glyphs exactly as shaping produced them (ids, advances, offsets) at another size, so a
    /// cluster can be shown on its own without shaping its text again. Glyph runs are kept until the
    /// owner clears the painter, because a drawn run must outlive the frame that recorded it.
    /// </summary>
    internal sealed class GlyphPainter : IDisposable
    {
        private readonly List<GlyphRun> _runs = new();

        /// <summary>
        /// Draws <paramref name="glyphs"/> (one font, in visual order) centred in <paramref name="box"/>
        /// at <paramref name="size"/>, shrunk if they would not fit the box width.
        /// </summary>
        public void DrawCentered(DrawingContext context, string text, IReadOnlyList<GlyphBox> glyphs, Rect box,
            double size, IBrush brush)
        {
            if (glyphs.Count == 0)
            {
                return;
            }

            var typeface = glyphs[0].Typeface;
            var sourceSize = glyphs[0].FontSize;
            var advance = glyphs.Sum(g => g.Advance);
            var scale = size / sourceSize;

            if (advance * scale > box.Width * 0.92 && advance > 0)
            {
                scale = box.Width * 0.92 / advance;
            }

            var emSize = sourceSize * scale;
            var start = glyphs.Min(g => g.ClusterRange.Start);
            var end = glyphs.Max(g => g.ClusterRange.End);
            var infos = glyphs
                .Select(g => new GlyphInfo(g.GlyphIndex, g.ClusterRange.Start - start, g.Advance * scale, g.Offset * scale))
                .ToArray();

            var characters = text.AsMemory(start, Math.Max(0, end - start));

            var run = new GlyphRun(typeface, emSize, characters, infos, new Point(0, 0));
            _runs.Add(run);

            var metrics = typeface.Metrics;
            var ascent = -metrics.Ascent * emSize / metrics.DesignEmHeight;
            var descent = metrics.Descent * emSize / metrics.DesignEmHeight;
            var width = advance * scale;
            var x = box.X + (box.Width - width) / 2;
            var baseline = box.Y + (box.Height - (ascent + descent)) / 2 + ascent;

            using (context.PushTransform(Matrix.CreateTranslation(x, baseline)))
            {
                context.DrawGlyphRun(brush, run);
            }
        }

        /// <summary>Releases the runs of earlier frames.</summary>
        public void Clear()
        {
            foreach (var run in _runs)
            {
                run.Dispose();
            }

            _runs.Clear();
        }

        public void Dispose() => Clear();
    }
}
