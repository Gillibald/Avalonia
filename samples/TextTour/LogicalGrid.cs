using System;
using System.Collections.Generic;
using Avalonia;

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
            double stripeHeight, double stripeGap)
        {
            Left = left;
            CellWidth = cellWidth;
            StripeHeight = stripeHeight;

            var perStripe = Math.Max(1, (int)Math.Floor(width / cellWidth));
            var text = analysis.Text;
            var start = 0;
            var y = top;

            while (start < text.Length)
            {
                var end = Math.Min(text.Length, start + perStripe);

                if (end < text.Length)
                {
                    end = ChooseEnd(analysis, start, end);
                }

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

        private static int ChooseEnd(TextAnalysis analysis, int start, int limit)
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

                bestGrapheme = g.End;

                if (char.IsWhiteSpace(analysis.Text[g.End - 1]))
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
                if (g.Start >= start)
                {
                    return g.End;
                }

                if (g.End > start)
                {
                    return g.End;
                }
            }

            return limit;
        }
    }
}
