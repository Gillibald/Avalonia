// The outline embolden in this file contains logic adapted to C# from the FreeType project
// (https://freetype.org), src/base/ftoutln.c (FT_Outline_EmboldenXY), and is a modified
// version of the original FreeType code, not the original.
//
// Copyright (C) 1996-2026 by David Turner, Robert Wilhelm, and Werner Lemberg.
//
// Used under the FreeType Project License (FTL); see NOTICE.md in the
// repository root for the full license text and the required credit.

using System;

namespace Avalonia.Media.Fonts.Tables.Glyf
{
    /// <summary>
    /// Algorithmic emboldening of a glyph outline by offsetting every contour outwards.
    /// </summary>
    /// <remarks>
    /// Follows FreeType's <c>FT_Outline_EmboldenXY</c>: each point, on-curve or not, moves along the
    /// bisector of its adjacent edges by a miter offset, which is limited for short edges so that
    /// thin features collapse instead of crossing over. Unlike FreeType, which then shifts the
    /// result so the lower-left corner stays put, the outline grows evenly on every side, the way
    /// the renderer's stroke-based fake bold does.
    /// <para>
    /// The one embolden of the simulated bold: glyph outlines and ink bounds apply it in design
    /// units with a size-independent strength, and the managed rasterizer applies it in device
    /// space after hinting with the size-dependent strength of the render backend.
    /// </para>
    /// </remarks>
    internal static class OutlineEmbolden
    {
        // A turn sharper than about 160 degrees leaves the point in place; the miter of such a
        // corner would spike far beyond the requested strength.
        private const double MinimumTurnCosine = -0.9375;

        /// <summary>
        /// Emboldens the outline in place.
        /// </summary>
        /// <param name="points">All points of the outline, in a y-up coordinate system.</param>
        /// <param name="contourEnds">Index of the last point of each contour.</param>
        /// <param name="xStrength">How much wider the outline gets; half on each side.</param>
        /// <param name="yStrength">How much taller the outline gets; half on each side.</param>
        public static void Embolden(Span<Point> points, ReadOnlySpan<int> contourEnds, double xStrength,
            double yStrength)
        {
            // The fill side of a contour depends on the outline's winding, which is a property of
            // the whole outline: counters wind against the outer contour and must shrink.
            var area = 0.0;
            var first = 0;

            foreach (var last in contourEnds)
            {
                if (last < first || last >= points.Length)
                {
                    return;
                }

                area += GetDoubleSignedArea(points.Slice(first, last - first + 1));

                first = last + 1;
            }

            if (area == 0)
            {
                return;
            }

            var clockwise = area < 0;

            first = 0;

            foreach (var last in contourEnds)
            {
                EmboldenContour(points.Slice(first, last - first + 1), clockwise, xStrength / 2, yStrength / 2);

                first = last + 1;
            }
        }

        private static double GetDoubleSignedArea(ReadOnlySpan<Point> contour)
        {
            var area = 0.0;
            var previous = contour[contour.Length - 1];

            foreach (var point in contour)
            {
                area += previous.X * point.Y - point.X * previous.Y;
                previous = point;
            }

            return area;
        }

        private static void EmboldenContour(Span<Point> contour, bool clockwise, double xStrength,
            double yStrength)
        {
            var count = contour.Length;

            if (count < 2)
            {
                return;
            }

            // Offsets are computed against the original points and applied afterwards, so a moved
            // point never feeds into its neighbour's edge directions.
            Span<Vector> shifts = count <= 128 ? stackalloc Vector[count] : new Vector[count];

            for (var i = 0; i < count; i++)
            {
                // Coincident points share their neighbour's edges, so skip over them to find the
                // edges that actually leave and enter this point.
                if (!TryGetEdge(contour, i, -1, out var incoming, out var incomingLength) ||
                    !TryGetEdge(contour, i, 1, out var outgoing, out var outgoingLength))
                {
                    shifts[i] = default;

                    continue;
                }

                shifts[i] = GetShift(incoming, incomingLength, outgoing, outgoingLength, clockwise, xStrength,
                    yStrength);
            }

            for (var i = 0; i < count; i++)
            {
                contour[i] += shifts[i];
            }
        }

        /// <summary>
        /// Finds the unit direction of the edge between the point at <paramref name="index"/> and
        /// the nearest distinct point in <paramref name="step"/> direction, oriented along the
        /// contour.
        /// </summary>
        private static bool TryGetEdge(ReadOnlySpan<Point> contour, int index, int step, out Vector direction,
            out double length)
        {
            var count = contour.Length;
            var origin = contour[index];

            for (var n = 1; n < count; n++)
            {
                var other = contour[((index + step * n) % count + count) % count];
                Vector delta = step > 0 ? other - origin : origin - other;

                length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

                if (length > 0)
                {
                    direction = delta / length;

                    return true;
                }
            }

            direction = default;
            length = 0;

            return false;
        }

        private static Vector GetShift(Vector incoming, double incomingLength, Vector outgoing, double outgoingLength,
            bool clockwise, double xStrength, double yStrength)
        {
            var d = incoming.X * outgoing.X + incoming.Y * outgoing.Y;

            if (d <= MinimumTurnCosine)
            {
                return default;
            }

            d += 1;

            // The lateral bisector, pointing away from the filled side.
            var shiftX = incoming.Y + outgoing.Y;
            var shiftY = incoming.X + outgoing.X;

            if (clockwise)
            {
                shiftX = -shiftX;
            }
            else
            {
                shiftY = -shiftY;
            }

            // Limit the miter at an inward corner to the shorter adjacent edge so collapsing
            // segments do not overshoot.
            var q = outgoing.X * incoming.Y - outgoing.Y * incoming.X;

            if (clockwise)
            {
                q = -q;
            }

            var l = Math.Min(incomingLength, outgoingLength);

            shiftX = xStrength * q <= l * d ? shiftX * xStrength / d : shiftX * l / q;
            shiftY = yStrength * q <= l * d ? shiftY * yStrength / d : shiftY * l / q;

            return new Vector(shiftX, shiftY);
        }
    }
}
