using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The vertical fit of an ideograph: the edges of its horizontal strokes move onto row
    /// boundaries so that every stroke and every gap between strokes keeps at least one row of its
    /// own, between the top and the bottom of the ideographic em box, which snap to their nearest
    /// rows. Edges of side-by-side components closer than half a pixel share one boundary; a
    /// stroke thinner than half a pixel, whose top and bottom edges fall together, centres on a
    /// row instead, so its ink stays in one row rather than splitting over two.
    /// </summary>
    /// <remarks>
    /// An ideograph whose strokes do not fit that way, because the em box has fewer rows than its
    /// strokes and gaps need or a stroke would move by most of a pixel, stays unfitted: moving it
    /// without a row for each stroke and gap only shifts its strokes against the pixel grid, which
    /// fills the narrow gaps the outline has.
    /// </remarks>
    internal static class IdeographFit
    {
        /// <summary>Edges closer than this, in pixels, share one row boundary.</summary>
        private const float GroupDistance = 0.5f;

        /// <summary>The furthest an edge may move, in pixels, before the glyph stays unfitted.</summary>
        private const float MaxShift = 0.75f;

        /// <summary>The shortest straight run, in pixels, that counts as a stroke edge.</summary>
        private const float MinEdgeLength = 0.75f;

        /// <summary>The most a stroke edge may rise or fall along its run, in pixels.</summary>
        private const float MaxEdgeDrift = 0.15f;

        private const int MaxEdges = 512;

        /// <param name="contours">The glyph outline in device space, y down, baseline at 0.</param>
        /// <param name="top">The top of the ideographic em box.</param>
        /// <param name="bottom">The bottom of the ideographic em box.</param>
        public static AxisWarp Build(GlyphPathBuilder contours, float top, float bottom)
        {
            var topRow = MathF.Floor(top + 0.5f);
            var bottomRow = MathF.Floor(bottom + 0.5f);

            if (bottomRow <= topRow)
            {
                return AxisWarp.Identity;
            }

            var edges = new float[MaxEdges];
            var directions = new float[MaxEdges];
            var edgeCount = CollectEdges(contours, top, bottom, edges, directions);

            if (edgeCount == 0)
            {
                return new AxisWarp(new[] { top, bottom }, new[] { topRow, bottomRow });
            }

            Array.Sort(edges, directions, 0, edgeCount);

            // Groups of edges closer than GroupDistance, each placed at its mean; a group with
            // edges of both directions is a stroke thinner than GroupDistance.
            var groups = new float[edgeCount];
            var centred = new bool[edgeCount];
            var groupCount = 0;
            var start = 0;

            for (var i = 1; i <= edgeCount; i++)
            {
                if (i == edgeCount || edges[i] - edges[i - 1] >= GroupDistance)
                {
                    var sum = 0f;
                    bool rising = false, falling = false;

                    for (var j = start; j < i; j++)
                    {
                        sum += edges[j];
                        rising |= directions[j] > 0;
                        falling |= directions[j] < 0;
                    }

                    groups[groupCount] = sum / (i - start);
                    centred[groupCount] = rising && falling;
                    groupCount++;
                    start = i;
                }
            }

            // Top to bottom, every group takes its nearest row boundary, or a thin stroke its
            // nearest row centre, at least a row of gap below the one before it: one row between
            // two boundaries, one and a half between a boundary and a centre, two between two
            // centres. Groups within GroupDistance of the em box's top or bottom share its
            // boundary.
            var from = new float[groupCount + 2];
            var to = new float[groupCount + 2];
            var knots = 0;

            from[knots] = top;
            to[knots] = topRow;
            knots++;

            var previous = topRow;
            var previousCentred = false;

            for (var i = 0; i < groupCount; i++)
            {
                var y = groups[i];

                if (y - top < GroupDistance || bottom - y < GroupDistance)
                {
                    continue;
                }

                var spacing = 1f + (previousCentred ? 0.5f : 0f) + (centred[i] ? 0.5f : 0f);
                var nearest = centred[i] ? MathF.Floor(y) + 0.5f : MathF.Floor(y + 0.5f);
                var row = MathF.Max(nearest, previous + spacing);

                if (MathF.Abs(row - y) > MaxShift)
                {
                    return AxisWarp.Identity;
                }

                from[knots] = y;
                to[knots] = row;
                knots++;
                previous = row;
                previousCentred = centred[i];
            }

            if (bottomRow < previous + 1 + (previousCentred ? 0.5f : 0f))
            {
                return AxisWarp.Identity;
            }

            from[knots] = bottom;
            to[knots] = bottomRow;
            knots++;

            Array.Resize(ref from, knots);
            Array.Resize(ref to, knots);

            return new AxisWarp(from, to);
        }

        /// <summary>
        /// The heights of the straight, near-horizontal runs of the outline between the em box's
        /// top and bottom, with the sign of each run's direction. A curve counts when all its
        /// points lie on one such run, as outlines drawn in curves give their flat strokes.
        /// </summary>
        private static int CollectEdges(GlyphPathBuilder contours, float top, float bottom, float[] edges,
            float[] directions)
        {
            var verbs = contours.Verbs;
            var points = contours.Points;
            var count = 0;
            var pointIndex = 0;
            float currentX = 0, currentY = 0, startX = 0, startY = 0;

            void Consider(float x0, float y0, float x1, float y1, float yLow, float yHigh)
            {
                if (count < edges.Length && MathF.Abs(x1 - x0) >= MinEdgeLength &&
                    yHigh - yLow <= MaxEdgeDrift)
                {
                    var y = (y0 + y1) * 0.5f;

                    if (y > top && y < bottom)
                    {
                        edges[count] = y;
                        directions[count] = MathF.Sign(x1 - x0);
                        count++;
                    }
                }
            }

            for (var v = 0; v < verbs.Length; v++)
            {
                switch ((GlyphPathVerb)verbs[v])
                {
                    case GlyphPathVerb.MoveTo:
                        currentX = startX = points[pointIndex];
                        currentY = startY = points[pointIndex + 1];
                        pointIndex += 2;
                        break;
                    case GlyphPathVerb.LineTo:
                        Consider(currentX, currentY, points[pointIndex], points[pointIndex + 1],
                            MathF.Min(currentY, points[pointIndex + 1]), MathF.Max(currentY, points[pointIndex + 1]));
                        currentX = points[pointIndex];
                        currentY = points[pointIndex + 1];
                        pointIndex += 2;
                        break;
                    case GlyphPathVerb.QuadTo:
                        Consider(currentX, currentY, points[pointIndex + 2], points[pointIndex + 3],
                            MathF.Min(currentY, MathF.Min(points[pointIndex + 1], points[pointIndex + 3])),
                            MathF.Max(currentY, MathF.Max(points[pointIndex + 1], points[pointIndex + 3])));
                        currentX = points[pointIndex + 2];
                        currentY = points[pointIndex + 3];
                        pointIndex += 4;
                        break;
                    case GlyphPathVerb.CubicTo:
                        Consider(currentX, currentY, points[pointIndex + 4], points[pointIndex + 5],
                            MathF.Min(MathF.Min(currentY, points[pointIndex + 1]), MathF.Min(points[pointIndex + 3], points[pointIndex + 5])),
                            MathF.Max(MathF.Max(currentY, points[pointIndex + 1]), MathF.Max(points[pointIndex + 3], points[pointIndex + 5])));
                        currentX = points[pointIndex + 4];
                        currentY = points[pointIndex + 5];
                        pointIndex += 6;
                        break;
                    case GlyphPathVerb.Close:
                        Consider(currentX, currentY, startX, startY, MathF.Min(currentY, startY), MathF.Max(currentY, startY));
                        currentX = startX;
                        currentY = startY;
                        break;
                }
            }

            return count;
        }
    }
}
