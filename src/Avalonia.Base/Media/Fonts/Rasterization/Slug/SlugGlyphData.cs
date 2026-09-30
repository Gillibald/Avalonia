using System;
using Avalonia.Media;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// The immutable, size-independent Slug payload for one glyph: em-space quadratic chains
    /// plus the horizontal and vertical band lists produced by <see cref="SlugBandEncoder"/>.
    /// Built once per glyph ever and cached; texel serialization happens downstream when a
    /// payload is placed into a texture.
    /// </summary>
    /// <remarks>
    /// Geometry keeps the sink's chained layout — curve ordinals are contour-major, each curve
    /// stores (start, control) and borrows its end point from the next curve (wrapping to the
    /// contour's first point). Horizontal bands partition the y extent of the control-point
    /// bounds and are consulted by horizontal winding rays; vertical bands partition x. Band
    /// lists hold global curve ordinals in three contiguous segments around the axis's split
    /// point: forward-only, shared, backward-only (see <see cref="SlugBandEncoder"/>).
    /// </remarks>
    internal sealed class SlugGlyphData
    {
        private readonly float[] _points;
        private readonly int[] _contourStarts;
        private readonly int[] _contourCounts;
        private readonly int[] _horizontalOffsets;
        private readonly int[] _horizontalEntries;
        private readonly int[] _horizontalSegments;
        private readonly int[] _verticalOffsets;
        private readonly int[] _verticalEntries;
        private readonly int[] _verticalSegments;

        internal SlugGlyphData(
            float[] points, int[] contourStarts, int[] contourCounts, FillRule fillRule,
            float minX, float minY, float maxX, float maxY,
            float horizontalSplit, float verticalSplit,
            int[] horizontalOffsets, int[] horizontalEntries, int[] horizontalSegments,
            int[] verticalOffsets, int[] verticalEntries, int[] verticalSegments)
        {
            _points = points;
            _contourStarts = contourStarts;
            _contourCounts = contourCounts;
            FillRule = fillRule;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            HorizontalSplit = horizontalSplit;
            VerticalSplit = verticalSplit;
            _horizontalOffsets = horizontalOffsets;
            _horizontalEntries = horizontalEntries;
            _horizontalSegments = horizontalSegments;
            _verticalOffsets = verticalOffsets;
            _verticalEntries = verticalEntries;
            _verticalSegments = verticalSegments;

            RetainedBytes = 64 +
                points.Length * sizeof(float) +
                (contourStarts.Length + contourCounts.Length) * sizeof(int) +
                (horizontalOffsets.Length + horizontalEntries.Length + horizontalSegments.Length) * sizeof(int) +
                (verticalOffsets.Length + verticalEntries.Length + verticalSegments.Length) * sizeof(int);
        }

        /// <summary>The fill rule the outline walker declared.</summary>
        public FillRule FillRule { get; }

        /// <summary>Em-space control-point bounds — the rectangle the bands partition.</summary>
        public float MinX { get; }

        /// <inheritdoc cref="MinX"/>
        public float MinY { get; }

        /// <inheritdoc cref="MinX"/>
        public float MaxX { get; }

        /// <inheritdoc cref="MinX"/>
        public float MaxY { get; }

        /// <summary>
        /// The em-space x coordinate that splits every horizontal band list into its forward
        /// and backward runs: the midpoint of the x bounds, in the single precision the shader
        /// compares against.
        /// </summary>
        public float HorizontalSplit { get; }

        /// <summary>The em-space y coordinate that splits every vertical band list.</summary>
        public float VerticalSplit { get; }

        /// <summary>The approximate managed size of the payload, for cache budgeting.</summary>
        public int RetainedBytes { get; }

        public int ContourCount => _contourStarts.Length;

        public int TotalCurveCount => _points.Length / 4;

        public int HorizontalBandCount => _horizontalOffsets.Length - 1;

        public int VerticalBandCount => _verticalOffsets.Length - 1;

        public int GetContourStart(int contourIndex) => _contourStarts[contourIndex];

        public int GetContourCurveCount(int contourIndex) => _contourCounts[contourIndex];

        /// <summary>
        /// Reads the curve with the given global ordinal; the end point wraps within the owning
        /// contour, so every contour stays closed.
        /// </summary>
        public SlugQuadCurve GetCurve(int curveIndex)
        {
            // Contours are few (typically 1-4); a forward scan beats any index structure here.
            var contour = 0;

            while (curveIndex >= _contourStarts[contour] + _contourCounts[contour])
            {
                contour++;
            }

            var start = _contourStarts[contour];
            var next = curveIndex + 1 < start + _contourCounts[contour] ? curveIndex + 1 : start;
            var p = curveIndex * 4;
            var q = next * 4;

            return new SlugQuadCurve(
                _points[p], _points[p + 1],
                _points[p + 2], _points[p + 3],
                _points[q], _points[q + 1]);
        }

        /// <summary>The curve ordinals of one horizontal band (a strip of the y extent).</summary>
        public ReadOnlySpan<int> GetHorizontalBand(int bandIndex)
            => _horizontalEntries.AsSpan(
                _horizontalOffsets[bandIndex],
                _horizontalOffsets[bandIndex + 1] - _horizontalOffsets[bandIndex]);

        /// <summary>
        /// The segment lengths of one horizontal band list, in list order: curves only the
        /// forward ray reaches, curves both rays reach, curves only the backward ray reaches.
        /// </summary>
        public (int ForwardOnly, int Shared, int BackwardOnly) GetHorizontalSegments(int bandIndex)
            => GetSegments(_horizontalSegments, bandIndex, GetHorizontalBand(bandIndex).Length);

        /// <summary>The curve ordinals of one vertical band (a strip of the x extent).</summary>
        public ReadOnlySpan<int> GetVerticalBand(int bandIndex)
            => _verticalEntries.AsSpan(
                _verticalOffsets[bandIndex],
                _verticalOffsets[bandIndex + 1] - _verticalOffsets[bandIndex]);

        /// <summary>The segment lengths of one vertical band list, in list order.</summary>
        public (int ForwardOnly, int Shared, int BackwardOnly) GetVerticalSegments(int bandIndex)
            => GetSegments(_verticalSegments, bandIndex, GetVerticalBand(bandIndex).Length);

        private static (int, int, int) GetSegments(int[] segments, int bandIndex, int length)
        {
            var forwardOnly = segments[bandIndex * 2];
            var shared = segments[bandIndex * 2 + 1];

            return (forwardOnly, shared, length - forwardOnly - shared);
        }
    }
}
