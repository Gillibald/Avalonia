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
    /// lists hold global curve ordinals, sorted descending by the control-point maximum along
    /// the ray axis — the same key the pixel shader's early-out tests.
    /// </remarks>
    internal sealed class SlugGlyphData
    {
        private readonly float[] _points;
        private readonly int[] _contourStarts;
        private readonly int[] _contourCounts;
        private readonly int[] _horizontalOffsets;
        private readonly int[] _horizontalEntries;
        private readonly int[] _verticalOffsets;
        private readonly int[] _verticalEntries;

        internal SlugGlyphData(
            float[] points, int[] contourStarts, int[] contourCounts, FillRule fillRule,
            float minX, float minY, float maxX, float maxY,
            int[] horizontalOffsets, int[] horizontalEntries,
            int[] verticalOffsets, int[] verticalEntries)
        {
            _points = points;
            _contourStarts = contourStarts;
            _contourCounts = contourCounts;
            FillRule = fillRule;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            _horizontalOffsets = horizontalOffsets;
            _horizontalEntries = horizontalEntries;
            _verticalOffsets = verticalOffsets;
            _verticalEntries = verticalEntries;

            RetainedBytes = 64 +
                points.Length * sizeof(float) +
                (contourStarts.Length + contourCounts.Length) * sizeof(int) +
                (horizontalOffsets.Length + horizontalEntries.Length) * sizeof(int) +
                (verticalOffsets.Length + verticalEntries.Length) * sizeof(int);
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

        /// <summary>The curve ordinals of one vertical band (a strip of the x extent).</summary>
        public ReadOnlySpan<int> GetVerticalBand(int bandIndex)
            => _verticalEntries.AsSpan(
                _verticalOffsets[bandIndex],
                _verticalOffsets[bandIndex + 1] - _verticalOffsets[bandIndex]);
    }
}
