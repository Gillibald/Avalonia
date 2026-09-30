using System;
using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// Organizes a captured quadratic outline into Slug's band structure: horizontal bands
    /// (uniform strips of the y extent, consulted by horizontal winding rays) and vertical bands
    /// (strips of the x extent). Runs once per glyph ever, on payload build — a cold path.
    /// </summary>
    /// <remarks>
    /// The rules follow the upstream data format: a curve joins every band its exact per-axis
    /// extent overlaps, widened by <see cref="BandEpsilon"/> so band edges never drop a curve;
    /// straight horizontal lines never join horizontal bands (they cannot contribute winding to
    /// a parallel ray), vertical lines never join vertical bands. Band counts are chosen per
    /// axis by sweeping every candidate up to <see cref="MaxBandCount"/> and keeping the one that
    /// minimizes the largest per-band list, breaking ties toward fewer total entries, then
    /// fewer bands.
    /// <para>
    /// Each band's list is then partitioned around the glyph's midpoint along the ray axis, the
    /// split point s, into three contiguous segments: curves only a forward ray (toward +axis)
    /// from a pixel at or past s can reach, curves both directions can reach, and curves only a
    /// backward ray (toward -axis) from a pixel before s can reach. A pixel at or past s walks
    /// the first two segments with a forward ray, any other pixel the last two with a backward
    /// ray, so neither walks the curves on the far side of the glyph; <see cref="SplitMargin"/>
    /// says when this is exact. Within each segment curves are sorted descending by the
    /// control-point maximum along the ray axis, ordinal breaking ties.
    /// </para>
    /// </remarks>
    internal static class SlugBandEncoder
    {
        /// <summary>The band-overlap epsilon in em units, per the upstream recommendation.</summary>
        public const double BandEpsilon = 1.0 / 1024;

        /// <summary>
        /// The band-count cap per axis. Beyond this, header and duplicated-entry growth outweighs
        /// shorter lists for any realistic glyph.
        /// </summary>
        public const int MaxBandCount = 32;

        /// <summary>
        /// How far past the split point, in em units, a curve still joins the other side's
        /// segment. A forward ray from a pixel at or past the split point s only accumulates
        /// curves whose hull reaches within half a pixel left of it, so every curve with a hull
        /// maximum below s minus the margin contributes nothing to it as long as half a pixel
        /// is at most the margin; the backward side mirrors this. The split therefore only
        /// applies to draws with at least 0.5 / <see cref="SplitMargin"/> (32) pixels per em
        /// along the ray axis, and smaller draws walk the whole list. A wider margin enables
        /// the split at smaller sizes but shares more curves between both sides.
        /// </summary>
        public const double SplitMargin = 1.0 / 64;

        private struct CurveInfo
        {
            public double MinX, MaxX, MinY, MaxY;
            public double HullMaxX, HullMaxY;

            // The control-point hull as the shader reads it back from half-float texels, so
            // the side a curve is assigned to agrees with the shader's own comparison.
            public float TexelMinX, TexelMaxX, TexelMinY, TexelMaxY;
            public bool IsHorizontalLine, IsVerticalLine;
        }

        /// <summary>
        /// Builds the per-glyph payload from a captured outline, or returns null when nothing
        /// was captured. The optional band-count overrides exist for tests and tuning runs;
        /// production callers leave them unset.
        /// </summary>
        public static SlugGlyphData? Encode(
            SlugContourSink sink,
            double epsilon = BandEpsilon,
            int? horizontalBandCount = null,
            int? verticalBandCount = null)
        {
            var curveCount = sink.TotalCurveCount;

            if (curveCount == 0)
            {
                return null;
            }

            // Snapshot the geometry in the sink's contour-major chained layout.
            var points = new float[curveCount * 4];
            var contourStarts = new int[sink.ContourCount];
            var contourCounts = new int[sink.ContourCount];
            var ordinal = 0;

            for (var contour = 0; contour < sink.ContourCount; contour++)
            {
                contourStarts[contour] = ordinal;
                contourCounts[contour] = sink.GetCurveCount(contour);

                for (var i = 0; i < contourCounts[contour]; i++, ordinal++)
                {
                    var curve = sink.GetCurve(contour, i);

                    points[ordinal * 4] = curve.X1;
                    points[ordinal * 4 + 1] = curve.Y1;
                    points[ordinal * 4 + 2] = curve.X2;
                    points[ordinal * 4 + 3] = curve.Y2;
                }
            }

            var info = new CurveInfo[curveCount];
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            ordinal = 0;

            for (var contour = 0; contour < contourStarts.Length; contour++)
            {
                for (var i = 0; i < contourCounts[contour]; i++, ordinal++)
                {
                    var curve = sink.GetCurve(contour, i);

                    (info[ordinal].MinX, info[ordinal].MaxX) = AxisExtent(curve.X1, curve.X2, curve.X3);
                    (info[ordinal].MinY, info[ordinal].MaxY) = AxisExtent(curve.Y1, curve.Y2, curve.Y3);
                    info[ordinal].HullMaxX = Math.Max(curve.X1, Math.Max(curve.X2, curve.X3));
                    info[ordinal].HullMaxY = Math.Max(curve.Y1, Math.Max(curve.Y2, curve.Y3));
                    info[ordinal].IsHorizontalLine = curve.Y1 == curve.Y2 && curve.Y2 == curve.Y3;
                    info[ordinal].IsVerticalLine = curve.X1 == curve.X2 && curve.X2 == curve.X3;
                    (info[ordinal].TexelMinX, info[ordinal].TexelMaxX) = TexelHull(curve.X1, curve.X2, curve.X3);
                    (info[ordinal].TexelMinY, info[ordinal].TexelMaxY) = TexelHull(curve.Y1, curve.Y2, curve.Y3);

                    minX = Math.Min(minX, Math.Min(curve.X1, Math.Min(curve.X2, curve.X3)));
                    maxX = Math.Max(maxX, info[ordinal].HullMaxX);
                    minY = Math.Min(minY, Math.Min(curve.Y1, Math.Min(curve.Y2, curve.Y3)));
                    maxY = Math.Max(maxY, info[ordinal].HullMaxY);
                }
            }

            var hCount = horizontalBandCount ??
                ChooseBandCount(info, minY, maxY, epsilon, horizontal: true);
            var vCount = verticalBandCount ??
                ChooseBandCount(info, minX, maxX, epsilon, horizontal: false);

            // Computed in single precision from the stored bounds: this is the exact value the
            // shader receives as a uniform and compares pixel coordinates against.
            var horizontalSplit = ((float)minX + (float)maxX) * 0.5f;
            var verticalSplit = ((float)minY + (float)maxY) * 0.5f;

            var (hOffsets, hEntries, hSegments) = BuildBands(
                info, hCount, minY, maxY, epsilon, horizontalSplit, horizontal: true);
            var (vOffsets, vEntries, vSegments) = BuildBands(
                info, vCount, minX, maxX, epsilon, verticalSplit, horizontal: false);

            return new SlugGlyphData(
                points, contourStarts, contourCounts, sink.FillRule,
                (float)minX, (float)minY, (float)maxX, (float)maxY,
                horizontalSplit, verticalSplit,
                hOffsets, hEntries, hSegments, vOffsets, vEntries, vSegments);
        }

        private static (float Min, float Max) TexelHull(float p1, float p2, float p3)
        {
            var q1 = (float)(Half)p1;
            var q2 = (float)(Half)p2;
            var q3 = (float)(Half)p3;

            return (MathF.Min(q1, MathF.Min(q2, q3)), MathF.Max(q1, MathF.Max(q2, q3)));
        }

        /// <summary>
        /// The exact value range of one quadratic coordinate over t in [0, 1]: the endpoints,
        /// plus the interior extremum when the control point pushes one out.
        /// </summary>
        private static (double Min, double Max) AxisExtent(double p1, double p2, double p3)
        {
            var min = Math.Min(p1, p3);
            var max = Math.Max(p1, p3);
            var denom = p1 - 2 * p2 + p3;

            if (denom != 0)
            {
                var t = (p1 - p2) / denom;

                if (t > 0 && t < 1)
                {
                    var s = 1 - t;
                    var v = s * s * p1 + 2 * t * s * p2 + t * t * p3;

                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }

            return (min, max);
        }

        private static void GetBandRange(
            in CurveInfo curve, int bandCount, double boundsMin, double bandSize, double epsilon,
            bool horizontal, out int lo, out int hi)
        {
            var extentMin = horizontal ? curve.MinY : curve.MinX;
            var extentMax = horizontal ? curve.MaxY : curve.MaxX;

            lo = Math.Max(0, (int)Math.Floor((extentMin - epsilon - boundsMin) / bandSize));
            hi = Math.Min(bandCount - 1, (int)Math.Floor((extentMax + epsilon - boundsMin) / bandSize));
        }

        private static bool IsEligible(in CurveInfo curve, bool horizontal)
            => horizontal ? !curve.IsHorizontalLine : !curve.IsVerticalLine;

        private static int ChooseBandCount(
            CurveInfo[] info, double boundsMin, double boundsMax, double epsilon, bool horizontal)
        {
            var extent = boundsMax - boundsMin;

            if (extent <= 0)
            {
                return 1;
            }

            var bestCount = 1;
            var bestMax = int.MaxValue;
            var bestTotal = int.MaxValue;
            Span<int> population = stackalloc int[MaxBandCount];

            for (var candidate = 1; candidate <= MaxBandCount; candidate++)
            {
                population.Slice(0, candidate).Clear();

                var bandSize = extent / candidate;

                foreach (ref readonly var curve in info.AsSpan())
                {
                    if (!IsEligible(in curve, horizontal))
                    {
                        continue;
                    }

                    GetBandRange(in curve, candidate, boundsMin, bandSize, epsilon, horizontal,
                        out var lo, out var hi);

                    for (var b = lo; b <= hi; b++)
                    {
                        population[b]++;
                    }
                }

                var worst = 0;
                var total = 0;

                for (var b = 0; b < candidate; b++)
                {
                    worst = Math.Max(worst, population[b]);
                    total += population[b];
                }

                if (worst < bestMax || (worst == bestMax && total < bestTotal))
                {
                    bestCount = candidate;
                    bestMax = worst;
                    bestTotal = total;
                }
            }

            return bestCount;
        }

        /// <summary>
        /// Builds one axis's band lists: per-band offsets into one entry array, and per band the
        /// forward-only and shared segment lengths (the backward-only length is the rest).
        /// </summary>
        private static (int[] Offsets, int[] Entries, int[] Segments) BuildBands(
            CurveInfo[] info, int bandCount, double boundsMin, double boundsMax, double epsilon,
            float split, bool horizontal)
        {
            var extent = boundsMax - boundsMin;
            var bandSize = extent > 0 ? extent / bandCount : 1;
            var offsets = new int[bandCount + 1];

            for (var ordinal = 0; ordinal < info.Length; ordinal++)
            {
                if (!IsEligible(in info[ordinal], horizontal))
                {
                    continue;
                }

                GetBandRange(in info[ordinal], bandCount, boundsMin, bandSize, epsilon, horizontal,
                    out var lo, out var hi);

                for (var b = lo; b <= hi; b++)
                {
                    offsets[b + 1]++;
                }
            }

            for (var b = 0; b < bandCount; b++)
            {
                offsets[b + 1] += offsets[b];
            }

            var entries = new int[offsets[bandCount]];
            var cursor = new int[bandCount];

            for (var ordinal = 0; ordinal < info.Length; ordinal++)
            {
                if (!IsEligible(in info[ordinal], horizontal))
                {
                    continue;
                }

                GetBandRange(in info[ordinal], bandCount, boundsMin, bandSize, epsilon, horizontal,
                    out var lo, out var hi);

                for (var b = lo; b <= hi; b++)
                {
                    entries[offsets[b] + cursor[b]++] = ordinal;
                }
            }

            // Segment first (forward-only, shared, backward-only), then descending by the hull
            // maximum along the ray axis, then ordinal, so the layout is deterministic.
            var segment = new int[info.Length];
            var lowEdge = split - SplitMargin;
            var highEdge = split + SplitMargin;

            for (var ordinal = 0; ordinal < info.Length; ordinal++)
            {
                var texelMin = horizontal ? info[ordinal].TexelMinX : info[ordinal].TexelMinY;
                var texelMax = horizontal ? info[ordinal].TexelMaxX : info[ordinal].TexelMaxY;
                var forward = texelMax >= lowEdge;
                var backward = texelMin <= highEdge;

                // A curve short of lowEdge ends below highEdge, so every curve is on at least
                // one side.
                segment[ordinal] = forward ? (backward ? 1 : 0) : 2;
            }

            var keys = info;
            Comparison<int> comparison = (a, b) =>
            {
                if (segment[a] != segment[b])
                {
                    return segment[a].CompareTo(segment[b]);
                }

                var keyA = horizontal ? keys[a].HullMaxX : keys[a].HullMaxY;
                var keyB = horizontal ? keys[b].HullMaxX : keys[b].HullMaxY;

                return keyA != keyB ? keyB.CompareTo(keyA) : a.CompareTo(b);
            };
            var comparer = Comparer<int>.Create(comparison);
            var segments = new int[bandCount * 2];

            for (var b = 0; b < bandCount; b++)
            {
                Array.Sort(entries, offsets[b], offsets[b + 1] - offsets[b], comparer);

                for (var k = offsets[b]; k < offsets[b + 1]; k++)
                {
                    switch (segment[entries[k]])
                    {
                        case 0:
                            segments[b * 2]++;
                            break;
                        case 1:
                            segments[b * 2 + 1]++;
                            break;
                    }
                }
            }

            return (offsets, entries, segments);
        }
    }
}
