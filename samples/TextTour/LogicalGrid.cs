using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace TextTour
{
    /// <summary>One line of the logical grid: a range of the string drawn as equal-width cells.</summary>
    internal sealed record Stripe(int Index, TextRange Range, double Top);

    /// <summary>
    /// Places the string in logical order as one equal-width cell per char, because logical order
    /// has no real x positions. When the cells do not fit the width, the grid wraps into stripes at
    /// grapheme boundaries, preferring the end of a space, so no grapheme is ever cut in two.
    /// Every band in logical order maps its text ranges to x through this grid.
    /// </summary>
    internal sealed class LogicalGrid
    {
        private readonly List<Stripe> _stripes = new();

        public LogicalGrid(TextAnalysis analysis, double left, double top, double width, double cellWidth,
            double stripeHeight, double stripeGap, double maxCellWidth = 0)
        {
            Left = left;
            CellWidth = cellWidth;
            StripeHeight = stripeHeight;

            var perStripe = Math.Max(1, (int)Math.Floor(width / cellWidth));
            var text = analysis.Text;

            // Stripes share the string evenly instead of leaving a short last stripe.
            var stripeCount = Math.Max(1, (int)Math.Ceiling(text.Length / (double)perStripe));
            var target = (int)Math.Ceiling(text.Length / (double)stripeCount);
            var start = 0;
            var y = top;

            while (start < text.Length)
            {
                var end = text.Length - start <= perStripe ? text.Length : ChooseEnd(analysis, start, start + target, start + perStripe);

                _stripes.Add(new Stripe(_stripes.Count, new TextRange(start, end - start), y));
                y += stripeHeight + stripeGap;
                start = end;
            }

            if (_stripes.Count == 0)
            {
                _stripes.Add(new Stripe(0, new TextRange(0, 0), top));
                y += stripeHeight + stripeGap;
            }

            Bottom = y - stripeGap;

            // Stripes end at spaces, so the longest one is often shorter than the width allows;
            // the cells grow to use the width, up to the caller's limit.
            var longest = 0;

            foreach (var stripe in _stripes)
            {
                longest = Math.Max(longest, stripe.Range.Length);
            }

            if (maxCellWidth > cellWidth && longest > 0)
            {
                CellWidth = Math.Min(maxCellWidth, Math.Max(cellWidth, width / longest));
            }
        }

        public double Left { get; }

        public double CellWidth { get; }

        public double StripeHeight { get; }

        public double Bottom { get; }

        public IReadOnlyList<Stripe> Stripes => _stripes;

        /// <summary>The x of the left edge of the cell at <paramref name="index"/> within its stripe.</summary>
        public double X(Stripe stripe, int index) => Left + (index - stripe.Range.Start) * CellWidth;

        /// <summary>The parts of <paramref name="range"/> per stripe, as x extents.</summary>
        public IEnumerable<(Stripe Stripe, double X0, double X1)> Spans(TextRange range)
        {
            foreach (var stripe in _stripes)
            {
                var start = Math.Max(range.Start, stripe.Range.Start);
                var end = Math.Min(range.End, stripe.Range.End);

                if (end > start || (range.Length == 0 && range.Start >= stripe.Range.Start && range.Start < stripe.Range.End))
                {
                    yield return (stripe, X(stripe, start), X(stripe, Math.Max(end, start)));
                }
            }
        }

        public Stripe? StripeOf(int index)
        {
            foreach (var stripe in _stripes)
            {
                if (stripe.Range.Contains(index) || (index == stripe.Range.End && stripe == _stripes[^1]))
                {
                    return stripe;
                }
            }

            return null;
        }

        /// <summary>
        /// Draws one band of segments, one per range, in every stripe the range touches. The band sits
        /// <paramref name="offset"/> below each stripe's top.
        /// </summary>
        public void Band<T>(DrawingContext context, IEnumerable<T> items, Func<T, TextRange> range, double offset,
            double height, Func<T, IBrush?> fill, Func<T, IBrush?> stroke, Func<T, string?> label, double labelSize,
            IBrush labelBrush, Typeface? typeface = null)
        {
            foreach (var item in items)
            {
                foreach (var (stripe, x0, x1) in Spans(range(item)))
                {
                    var box = new Rect(x0, stripe.Top + offset, Math.Max(0, x1 - x0), height);
                    Draw.Segment(context, box, fill(item), stroke(item), label(item), labelSize, labelBrush, typeface);
                }
            }
        }

        /// <summary>The char index under a point, or -1.</summary>
        public int IndexAt(Point point, Func<Stripe, (double Top, double Bottom)> verticalExtent)
        {
            foreach (var stripe in _stripes)
            {
                var (top, bottom) = verticalExtent(stripe);

                if (point.Y < top || point.Y > bottom)
                {
                    continue;
                }

                var index = stripe.Range.Start + (int)Math.Floor((point.X - Left) / CellWidth);

                if (index >= stripe.Range.Start && index < stripe.Range.End)
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// The natural cell width for the space available: the cells shrink until the whole string
        /// fits one stripe, but never below <paramref name="min"/> (labels stay legible) or above
        /// <paramref name="max"/>.
        /// </summary>
        public static double FitCellWidth(int length, double width, double min, double max)
        {
            return length == 0 ? max : Math.Clamp(width / length, min, max);
        }

        private static bool AtParagraphLevel(TextAnalysis analysis, int index)
        {
            var cp = analysis.CodepointIndexAt(index);

            return cp < 0 || analysis.Levels[cp] == analysis.ParagraphLevel;
        }

        private static int ChooseEnd(TextAnalysis analysis, int start, int target, int limit)
        {
            var bestSpace = -1;
            var bestGrapheme = -1;

            foreach (var g in analysis.Graphemes)
            {
                if (g.End <= start)
                {
                    continue;
                }

                if (g.End > limit)
                {
                    break;
                }

                if (bestGrapheme < 0 || Math.Abs(g.End - target) < Math.Abs(bestGrapheme - target))
                {
                    bestGrapheme = g.End;
                }

                // A space only counts as a stripe end outside right-to-left text, so an Arabic word
                // and the number inside it stay on one stripe.
                var isSpace = char.IsWhiteSpace(analysis.Text[g.End - 1]) && AtParagraphLevel(analysis, g.End - 1) &&
                              (g.End >= analysis.Text.Length || AtParagraphLevel(analysis, g.End));

                if (isSpace && g.End - start >= (target - start) / 2 &&
                    (bestSpace < 0 || Math.Abs(g.End - target) < Math.Abs(bestSpace - target)))
                {
                    bestSpace = g.End;
                }
            }

            if (bestSpace > start)
            {
                return bestSpace;
            }

            if (bestGrapheme > start)
            {
                return bestGrapheme;
            }

            // A single grapheme wider than the stripe still gets a stripe of its own.
            foreach (var g in analysis.Graphemes)
            {
                if (g.End > start)
                {
                    return g.End;
                }
            }

            return limit;
        }
    }
}
