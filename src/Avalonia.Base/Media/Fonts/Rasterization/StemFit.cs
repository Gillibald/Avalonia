using System;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Horizontal stem snapping for Strong hinting — the x-axis analog of the vertical zone
    /// fit: straight vertical stem flanks land on pixel columns with quantized widths, so a
    /// 1.4-pixel stem renders as one solid column instead of two partial ones. Detection is
    /// deliberately conservative: only long, straight, near-vertical line segments paired by
    /// opposite winding direction at a plausible stem width qualify. Curves and diagonals are
    /// untouched — snapping either would distort shapes, and no platform hinter moves them
    /// horizontally without font instructions.
    /// </summary>
    /// <remarks>
    /// Runs per glyph on the captured device-space contours (before rasterization), producing
    /// a monotone piecewise-linear x-warp: stem edges move onto the grid, counters and spacing
    /// between stems interpolate, everything outside shifts rigidly. Ambiguity degrades to
    /// identity, never to a misfit.
    /// </remarks>
    internal static class StemFit
    {
        /// <summary>Flank straightness: at most this much x-drift, in final pixels.</summary>
        private const float MaxFlankDrift = 0.35f;

        /// <summary>Minimum flank length, in final pixels — stems are tall, serif noise is not.</summary>
        private const float MinFlankLength = 2f;

        /// <summary>Edge samples closer than this merge into one edge, in final pixels.</summary>
        private const float ClusterGap = 0.6f;

        /// <summary>Plausible stem widths, in final pixels; outside this a pair is not a stem.
        /// The ceiling grows with the font's own standards so detection follows the stems up
        /// the size ramp instead of abandoning them past a fixed width.</summary>
        private const float MinStemWidth = 0.4f;
        private const float MaxStemWidth = 3f;

        private static float PairWidthCeiling(ReadOnlySpan<float> standardsDesign, float designToPixels)
        {
            var ceiling = MaxStemWidth;

            for (var i = 0; i < standardsDesign.Length; i++)
            {
                ceiling = MathF.Max(ceiling, standardsDesign[i] * designToPixels + WidthCutIn + 0.5f);
            }

            return ceiling;
        }

        private const int MaxEdges = 16;

        /// <summary>
        /// The control-value cut-in, in final pixels: a pair whose natural width is within
        /// this of a font-wide standard renders at the standard's width (TrueType's default
        /// is 17/16 px). Natural widths win past it, so real differences come back once they
        /// are big enough to see.
        /// </summary>
        private const float WidthCutIn = 1f;

        /// <summary>
        /// Builds the x-warp for the captured contours, or identity when no stems qualify.
        /// <paramref name="subpixelFactor"/> is 3 for LCD masks, whose device space is
        /// horizontally tripled; detection and snapping happen in final-pixel units and the
        /// knots convert back.
        /// </summary>
        public static AxisWarp BuildWarp(GlyphPathBuilder contours, float subpixelFactor,
            ReadOnlySpan<float> standardWidths, float designToPixels)
        {
            Span<float> edgeX = stackalloc float[MaxEdges];
            Span<float> edgeWeight = stackalloc float[MaxEdges];
            Span<float> edgeDirection = stackalloc float[MaxEdges];
            var edgeCount = CollectEdges(contours, subpixelFactor, vertical: false, edgeX, edgeWeight, edgeDirection);

            if (edgeCount < 2)
            {
                return AxisWarp.Identity;
            }

            // Pair adjacent edges of opposite winding into stems and snap: the left edge
            // rounds to a column boundary and the width to a whole number of columns (at
            // least one). Consumed pairs cannot re-pair.
            Span<float> from = stackalloc float[MaxEdges];
            Span<float> to = stackalloc float[MaxEdges];
            var knots = 0;

            var widthCeiling = PairWidthCeiling(standardWidths, designToPixels);

            for (var i = 0; i + 1 < edgeCount; i++)
            {
                var width = edgeX[i + 1] - edgeX[i];

                if (width < MinStemWidth || width > widthCeiling)
                {
                    continue;
                }

                if (edgeDirection[i] * edgeDirection[i + 1] >= 0)
                {
                    continue;   // same winding — not the two flanks of one stem
                }

                var weaker = Math.Min(edgeWeight[i], edgeWeight[i + 1]);
                var stronger = Math.Max(edgeWeight[i], edgeWeight[i + 1]);

                if (weaker < stronger * 0.3f)
                {
                    continue;   // asymmetric evidence — likely a serif or crossbar artifact
                }

                var left = MathF.Round(edgeX[i]);
                var snappedWidth = UnifyWidth(width, standardWidths, designToPixels);

                // Monotonicity across stems: a knot may never move left of its predecessor.
                if (knots >= 2 && (edgeX[i] <= from[knots - 1] + 0.01f || left < to[knots - 1]))
                {
                    continue;
                }

                from[knots] = edgeX[i];
                to[knots] = left;
                knots++;
                from[knots] = edgeX[i + 1];
                to[knots] = left + snappedWidth;
                knots++;
                i++;   // consume the partner edge
            }

            if (knots < 2)
            {
                return AxisWarp.Identity;
            }

            var fromArray = new float[knots];
            var toArray = new float[knots];

            for (var i = 0; i < knots; i++)
            {
                fromArray[i] = from[i] * subpixelFactor;
                toArray[i] = to[i] * subpixelFactor;
            }

            return new AxisWarp(fromArray, toArray);
        }

        /// <summary>
        /// Detects horizontal strokes (crossbars, arms, bowl waists) as paired opposite-winding
        /// edges along Y and emits snap knots preserving each stroke's thickness: position
        /// rounds to a row, thickness to whole rows with a one-row floor. Zone-adjacent edges
        /// are skipped — the zone knots own those. This is what keeps interior strokes crisp
        /// instead of washing out under between-zone interpolation.
        /// </summary>
        public static int CollectStrokeKnots(GlyphPathBuilder contours,
            ReadOnlySpan<float> zoneSources, float zoneExclusion,
            ReadOnlySpan<float> standardWidths, float designToPixels,
            Span<float> knotFrom, Span<float> knotTo)
        {
            Span<float> edgeY = stackalloc float[MaxEdges];
            Span<float> edgeWeight = stackalloc float[MaxEdges];
            Span<float> edgeDirection = stackalloc float[MaxEdges];
            var edgeCount = CollectEdges(contours, 1f, vertical: true, edgeY, edgeWeight, edgeDirection);
            var widthCeiling = PairWidthCeiling(standardWidths, designToPixels);
            var knots = 0;

            for (var i = 0; i + 1 < edgeCount && knots + 1 < knotFrom.Length; i++)
            {
                var thickness = edgeY[i + 1] - edgeY[i];

                if (thickness < MinStemWidth || thickness > widthCeiling)
                {
                    continue;
                }

                if (edgeDirection[i] * edgeDirection[i + 1] >= 0)
                {
                    continue;
                }

                var weaker = Math.Min(edgeWeight[i], edgeWeight[i + 1]);
                var stronger = Math.Max(edgeWeight[i], edgeWeight[i + 1]);

                if (weaker < stronger * 0.3f)
                {
                    continue;
                }

                // A stroke edge inside a zone capture band belongs to that zone instead.
                if (NearAny(zoneSources, edgeY[i], zoneExclusion) ||
                    NearAny(zoneSources, edgeY[i + 1], zoneExclusion))
                {
                    i++;
                    continue;
                }

                var top = MathF.Round(edgeY[i]);
                var snappedThickness = UnifyWidth(thickness, standardWidths, designToPixels);

                knotFrom[knots] = edgeY[i];
                knotTo[knots] = top;
                knots++;
                knotFrom[knots] = edgeY[i + 1];
                knotTo[knots] = top + snappedThickness;
                knots++;
                i++;
            }

            return knots;
        }

        /// <summary>
        /// The unified pixel width: the nearest font-wide standard when the natural width is
        /// within the cut-in, the natural width itself otherwise. One-pixel floor either way.
        /// </summary>
        private static float UnifyWidth(float width, ReadOnlySpan<float> standardsDesign,
            float designToPixels)
        {
            var bestDistance = float.MaxValue;
            var bestStandard = 0f;

            for (var i = 0; i < standardsDesign.Length; i++)
            {
                var standard = standardsDesign[i] * designToPixels;
                var distance = MathF.Abs(width - standard);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStandard = standard;
                }
            }

            return bestDistance <= WidthCutIn
                ? MathF.Max(1f, MathF.Round(bestStandard))
                : MathF.Max(1f, MathF.Round(width));
        }

        /// <summary>
        /// Measures paired stroke widths on captured contours along either axis — the
        /// measurement half of width unification. Bounds are in capture-space pixels so
        /// callers can measure at a large reference size for precision; returned widths are
        /// in the same space. Pairing rules match the snapping detectors (opposite winding,
        /// weight symmetry), so what gets measured is what would get snapped.
        /// </summary>
        public static int MeasurePairWidths(GlyphPathBuilder contours, bool vertical,
            float minWidth, float maxWidth, Span<float> widths)
        {
            Span<float> edgePosition = stackalloc float[MaxEdges];
            Span<float> edgeWeight = stackalloc float[MaxEdges];
            Span<float> edgeDirection = stackalloc float[MaxEdges];
            var edgeCount = CollectEdges(contours, 1f, vertical, edgePosition, edgeWeight, edgeDirection);
            var count = 0;

            for (var i = 0; i + 1 < edgeCount && count < widths.Length; i++)
            {
                var width = edgePosition[i + 1] - edgePosition[i];

                if (width < minWidth || width > maxWidth)
                {
                    continue;
                }

                if (edgeDirection[i] * edgeDirection[i + 1] >= 0)
                {
                    continue;
                }

                var weaker = Math.Min(edgeWeight[i], edgeWeight[i + 1]);
                var stronger = Math.Max(edgeWeight[i], edgeWeight[i + 1]);

                if (weaker < stronger * 0.3f)
                {
                    continue;
                }

                widths[count++] = width;
                i++;
            }

            return count;
        }

        private static bool NearAny(ReadOnlySpan<float> positions, float value, float distance)
        {
            for (var i = 0; i < positions.Length; i++)
            {
                if (MathF.Abs(positions[i] - value) <= distance)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CollectEdges(GlyphPathBuilder contours, float subpixelFactor, bool vertical,
            Span<float> edgeX, Span<float> edgeWeight, Span<float> edgeDirection)
        {
            var verbs = contours.Verbs;
            var points = contours.Points;

            // Gather straight, near-vertical segments as (x, |dy|, signed dy) samples, then
            // cluster by x. Curves only move the current point — their flanks never qualify.
            Span<float> sampleX = stackalloc float[64];
            Span<float> sampleWeight = stackalloc float[64];
            Span<float> sampleDy = stackalloc float[64];
            var samples = 0;

            var pointIndex = 0;
            float currentX = 0, currentY = 0, startX = 0, startY = 0;

            static void Consider(Span<float> sampleX, Span<float> sampleWeight, Span<float> sampleDy,
                ref int samples, float subpixelFactor, bool vertical, float x0, float y0, float x1, float y1)
            {
                if (samples >= 64)
                {
                    return;
                }

                // Along-axis drift must stay small and the cross-axis run long: vertical stems
                // are (dx small, dy long); horizontal strokes swap the roles.
                var drift = vertical ? MathF.Abs(y1 - y0) : MathF.Abs(x1 - x0) / subpixelFactor;
                var run = vertical ? (x1 - x0) / subpixelFactor : y1 - y0;

                if (drift <= MaxFlankDrift && MathF.Abs(run) >= MinFlankLength)
                {
                    sampleX[samples] = vertical ? (y0 + y1) * 0.5f : (x0 + x1) * 0.5f / subpixelFactor;
                    sampleWeight[samples] = MathF.Abs(run);
                    sampleDy[samples] = run;
                    samples++;
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
                        Consider(sampleX, sampleWeight, sampleDy, ref samples, subpixelFactor, vertical, currentX, currentY, points[pointIndex], points[pointIndex + 1]);
                        currentX = points[pointIndex];
                        currentY = points[pointIndex + 1];
                        pointIndex += 2;
                        break;
                    case GlyphPathVerb.QuadTo:
                        currentX = points[pointIndex + 2];
                        currentY = points[pointIndex + 3];
                        pointIndex += 4;
                        break;
                    case GlyphPathVerb.CubicTo:
                        currentX = points[pointIndex + 4];
                        currentY = points[pointIndex + 5];
                        pointIndex += 6;
                        break;
                    case GlyphPathVerb.Close:
                        Consider(sampleX, sampleWeight, sampleDy, ref samples, subpixelFactor, vertical, currentX, currentY, startX, startY);
                        currentX = startX;
                        currentY = startY;
                        break;
                }
            }

            if (samples == 0)
            {
                return 0;
            }

            // Insertion-sort the few samples by x, then cluster.
            for (var i = 1; i < samples; i++)
            {
                var x = sampleX[i];
                var w = sampleWeight[i];
                var d = sampleDy[i];
                var j = i - 1;

                while (j >= 0 && sampleX[j] > x)
                {
                    sampleX[j + 1] = sampleX[j];
                    sampleWeight[j + 1] = sampleWeight[j];
                    sampleDy[j + 1] = sampleDy[j];
                    j--;
                }

                sampleX[j + 1] = x;
                sampleWeight[j + 1] = w;
                sampleDy[j + 1] = d;
            }

            var edges = 0;

            for (var i = 0; i < samples && edges < edgeX.Length;)
            {
                var x = sampleX[i];
                var weightedX = sampleX[i] * sampleWeight[i];
                var weight = sampleWeight[i];
                var direction = sampleDy[i];
                var j = i + 1;

                while (j < samples && sampleX[j] - x <= ClusterGap)
                {
                    weightedX += sampleX[j] * sampleWeight[j];
                    weight += sampleWeight[j];
                    direction += sampleDy[j];
                    x = sampleX[j];
                    j++;
                }

                edgeX[edges] = weightedX / weight;
                edgeWeight[edges] = weight;
                edgeDirection[edges] = direction;
                edges++;
                i = j;
            }

            return edges;
        }
    }
}
