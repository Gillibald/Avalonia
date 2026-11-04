using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace TextTour
{
    /// <summary>
    /// The text of a stripe layout drawn as the shaper sees it: one layout per item, so ligatures
    /// and joined letters show, placed over the cells the item spans.
    /// </summary>
    internal static class ShapedPieces
    {
        /// <param name="foreground">The brush of a piece, from its text range.</param>
        public static List<(TextRange Range, TextLayout Layout)> Create(TextAnalysis analysis, LogicalGrid grid,
            Typeface typeface, double size, Func<TextRange, IBrush> foreground)
        {
            var result = new List<(TextRange, TextLayout)>();

            foreach (var item in analysis.Items)
            {
                foreach (var piece in PerStripe(analysis, grid, item.Range))
                {
                    result.Add((piece, Fitted(analysis, grid, piece, typeface, size, foreground(piece))));
                }
            }

            return result;
        }

        // Shaped text can be wider than its cells, which only have room for the widest single part.
        private static TextLayout Fitted(TextAnalysis analysis, LogicalGrid grid, TextRange range, Typeface typeface,
            double size, IBrush foreground)
        {
            var layout = Create(analysis, range, typeface, size, foreground);
            var (_, x0, x1) = grid.Spans(range).First();
            var width = layout.WidthIncludingTrailingWhitespace;

            if (width <= x1 - x0)
            {
                return layout;
            }

            layout.Dispose();
            return Create(analysis, range, typeface, size * (x1 - x0) / width, foreground);
        }

        private static TextLayout Create(TextAnalysis analysis, TextRange range, Typeface typeface, double size,
            IBrush foreground) =>
            analysis.CreateLayout(size, double.PositiveInfinity, TextWrapping.NoWrap, typeface: typeface,
                text: analysis.Text.Substring(range.Start, range.Length), foreground: foreground);

        // An item that crosses a stripe boundary is drawn once per stripe, cut between graphemes.
        private static IEnumerable<TextRange> PerStripe(TextAnalysis analysis, LogicalGrid grid, TextRange item)
        {
            var start = item.Start;
            Stripe? current = null;
            var end = item.Start;

            foreach (var g in analysis.Graphemes.Where(g => g.Start >= item.Start && g.Start < item.Start + item.Length))
            {
                var stripe = grid.Spans(g).First().Stripe;

                if (current is not null && stripe != current)
                {
                    yield return new TextRange(start, end - start);
                    start = g.Start;
                }

                current = stripe;
                end = g.Start + g.Length;
            }

            if (end > start)
            {
                yield return new TextRange(start, end - start);
            }
        }
    }
}
