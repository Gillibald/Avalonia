using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization.Slug
{
    /// <summary>
    /// A single quadratic Bézier segment in Slug's notation: p1 = start, p2 = control, p3 = end,
    /// C(t) = (1-t)²·p1 + 2t(1-t)·p2 + t²·p3. Straight lines use the degenerate encoding
    /// {p1, p2, p2} (the second endpoint duplicated), per the upstream data-format rules.
    /// </summary>
    internal readonly struct SlugQuadCurve
    {
        public SlugQuadCurve(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            X3 = x3;
            Y3 = y3;
        }

        public float X1 { get; }
        public float Y1 { get; }
        public float X2 { get; }
        public float Y2 { get; }
        public float X3 { get; }
        public float Y3 { get; }
    }

    /// <summary>
    /// An <see cref="IGeometryContext"/> sink that captures glyph contours as closed chains of
    /// quadratic Bézier curves for the Slug vector tier — quadratics pass through, lines become
    /// {p1, p2, p2}, and cubics (CFF/CFF2) subdivide to quadratics under an error bound. The
    /// caller feeds <c>TryBuildGlyphContours</c> an em-normalizing transform (1/unitsPerEm, no
    /// y-flip), so captured coordinates are em-space, y-up, and the flatten tolerance is in em
    /// units.
    /// </summary>
    /// <remarks>
    /// Storage is chained: each curve stores only (start, control); its end point is the next
    /// curve's start, and the last curve wraps to the contour's first point. That mirrors the
    /// curve-texture layout, where consecutive curves share their endpoint texel, and makes the
    /// shared coordinates identical by construction rather than by comparison. Instances are
    /// long-lived and reused via <see cref="Reset"/> like the other capture sinks; the backing
    /// arrays grow geometrically and are retained. Not thread-safe.
    /// <see cref="IDisposable.Dispose"/> is a no-op: table walkers receive the context by
    /// parameter and must not tear it down.
    /// </remarks>
    internal sealed class SlugContourSink : IGeometryContext
    {
        /// <summary>
        /// The default cubic flatten tolerance in em units. Half-float payload quantization sits
        /// at ~1/2048 em, so a finer tolerance buys nothing; 1/1024 keeps the flattening error
        /// under the quantization noise floor times two and under 0.4 px at a 400 px em.
        /// </summary>
        public const double DefaultFlattenTolerance = 1.0 / 1024;

        private const int MaxCubicSplitDepth = 8;

        // sqrt(3)/36: bound on the max parametric deviation of the single-quad midpoint
        // approximation of a cubic, applied to |p3 - 3·p2 + 3·p1 - p0|. Halving the cubic
        // scales that third difference by 1/8, so the required split depth is direct.
        private const double CubicErrorFactor = 0.048112522432468816;

        private readonly double _flattenTolerance;

        private float[] _points;
        private int[] _contourStarts;
        private int[] _contourCounts;
        private int _pointCount;
        private int _curveCount;
        private int _contourCount;

        private bool _figureOpen;
        private int _figureFirstCurve;
        private double _startX, _startY;
        private double _currentX, _currentY;

        public SlugContourSink(double flattenTolerance = DefaultFlattenTolerance, int initialCurveCapacity = 64)
        {
            _flattenTolerance = flattenTolerance;
            _points = new float[initialCurveCapacity * 4];
            _contourStarts = new int[8];
            _contourCounts = new int[8];
        }

        /// <summary>
        /// The fill rule the table walker declared. Font outline walkers emit
        /// <see cref="FillRule.NonZero"/>; the payload carries the rule either way because the
        /// pixel shader supports both.
        /// </summary>
        public FillRule FillRule { get; private set; } = FillRule.NonZero;

        /// <summary>The number of committed (closed, non-empty) contours.</summary>
        public int ContourCount => _contourCount;

        /// <summary>The total number of curves across all committed contours.</summary>
        public int TotalCurveCount => _curveCount;

        /// <summary>The number of curves in a committed contour.</summary>
        public int GetCurveCount(int contourIndex) => _contourCounts[contourIndex];

        /// <summary>
        /// Reads one curve of a committed contour; the last curve's end point wraps to the
        /// contour's first point, so every contour is closed by construction.
        /// </summary>
        public SlugQuadCurve GetCurve(int contourIndex, int curveIndex)
        {
            var start = _contourStarts[contourIndex];
            var count = _contourCounts[contourIndex];
            var p = (start + curveIndex) * 4;
            var next = curveIndex + 1 < count ? p + 4 : start * 4;

            return new SlugQuadCurve(
                _points[p], _points[p + 1],
                _points[p + 2], _points[p + 3],
                _points[next], _points[next + 1]);
        }

        /// <summary>Clears the captured contours so the instance can record another glyph.</summary>
        public void Reset()
        {
            _pointCount = 0;
            _curveCount = 0;
            _contourCount = 0;
            _figureOpen = false;
            FillRule = FillRule.NonZero;
        }

        public void SetFillRule(FillRule fillRule) => FillRule = fillRule;

        public void BeginFigure(Point startPoint, bool isFilled = true)
        {
            // Fill-only sink: a dangling open figure is closed implicitly so winding stays
            // consistent, matching the other glyph capture sinks.
            if (_figureOpen)
            {
                EndFigure(true);
            }

            _figureOpen = true;
            _figureFirstCurve = _curveCount;
            _startX = _currentX = startPoint.X;
            _startY = _currentY = startPoint.Y;
        }

        public void LineTo(Point point, bool isStroked = true)
        {
            if (!_figureOpen || IsCurrent(point.X, point.Y))
            {
                return;
            }

            AppendCurve(point.X, point.Y, point.X, point.Y);
        }

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
        {
            if (!_figureOpen ||
                (IsCurrent(controlPoint.X, controlPoint.Y) && IsCurrent(endPoint.X, endPoint.Y)))
            {
                return;
            }

            AppendCurve(controlPoint.X, controlPoint.Y, endPoint.X, endPoint.Y);
        }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
        {
            if (!_figureOpen ||
                (IsCurrent(controlPoint1.X, controlPoint1.Y) && IsCurrent(controlPoint2.X, controlPoint2.Y) &&
                 IsCurrent(endPoint.X, endPoint.Y)))
            {
                return;
            }

            var x0 = _currentX;
            var y0 = _currentY;

            // Pick the split depth from the third-difference error bound, then emit 2^depth
            // uniform segments as single quads. The bound divides by 8 per halving.
            var dx = endPoint.X - 3 * controlPoint2.X + 3 * controlPoint1.X - x0;
            var dy = endPoint.Y - 3 * controlPoint2.Y + 3 * controlPoint1.Y - y0;
            var error = CubicErrorFactor * Math.Sqrt(dx * dx + dy * dy);

            var depth = 0;

            while (error > _flattenTolerance && depth < MaxCubicSplitDepth)
            {
                error *= 0.125;
                depth++;
            }

            EmitCubicAsQuads(
                x0, y0,
                controlPoint1.X, controlPoint1.Y,
                controlPoint2.X, controlPoint2.Y,
                endPoint.X, endPoint.Y,
                depth);
        }

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked = true)
        {
            // Font table walkers never emit arcs. Degrade to a straight segment so an unexpected
            // caller still produces bounded output.
            LineTo(point, isStroked);
        }

        public void EndFigure(bool isClosed)
        {
            if (!_figureOpen)
            {
                return;
            }

            _figureOpen = false;

            // Fill semantics close every contour: emit the closing line unless the pen already
            // returned to the start (compared in payload precision).
            if ((float)_currentX != (float)_startX || (float)_currentY != (float)_startY)
            {
                AppendCurve(_startX, _startY, _startX, _startY);
            }

            var count = _curveCount - _figureFirstCurve;

            if (count == 0)
            {
                return;
            }

            if (_contourCount == _contourStarts.Length)
            {
                Array.Resize(ref _contourStarts, _contourStarts.Length * 2);
                Array.Resize(ref _contourCounts, _contourCounts.Length * 2);
            }

            _contourStarts[_contourCount] = _figureFirstCurve;
            _contourCounts[_contourCount] = count;
            _contourCount++;
        }

        public void Dispose()
        {
            // Intentionally empty — see the class remarks.
        }

        private bool IsCurrent(double x, double y)
            => (float)x == (float)_currentX && (float)y == (float)_currentY;

        private void EmitCubicAsQuads(
            double x0, double y0, double x1, double y1,
            double x2, double y2, double x3, double y3, int depth)
        {
            if (depth == 0)
            {
                // Midpoint approximation: the quad control that matches the cubic's midpoint.
                AppendCurve(
                    (3 * (x1 + x2) - x0 - x3) * 0.25,
                    (3 * (y1 + y2) - y0 - y3) * 0.25,
                    x3, y3);
                return;
            }

            // De Casteljau halve; shared segment endpoints are computed once, so the emitted
            // chain stays exact across the split.
            var mx01 = (x0 + x1) * 0.5;
            var my01 = (y0 + y1) * 0.5;
            var mx12 = (x1 + x2) * 0.5;
            var my12 = (y1 + y2) * 0.5;
            var mx23 = (x2 + x3) * 0.5;
            var my23 = (y2 + y3) * 0.5;
            var mx012 = (mx01 + mx12) * 0.5;
            var my012 = (my01 + my12) * 0.5;
            var mx123 = (mx12 + mx23) * 0.5;
            var my123 = (my12 + my23) * 0.5;
            var mx = (mx012 + mx123) * 0.5;
            var my = (my012 + my123) * 0.5;

            EmitCubicAsQuads(x0, y0, mx01, my01, mx012, my012, mx, my, depth - 1);
            EmitCubicAsQuads(mx, my, mx123, my123, mx23, my23, x3, y3, depth - 1);
        }

        private void AppendCurve(double controlX, double controlY, double endX, double endY)
        {
            if (_pointCount + 4 > _points.Length)
            {
                Array.Resize(ref _points, _points.Length * 2);
            }

            _points[_pointCount++] = (float)_currentX;
            _points[_pointCount++] = (float)_currentY;
            _points[_pointCount++] = (float)controlX;
            _points[_pointCount++] = (float)controlY;
            _curveCount++;

            _currentX = endX;
            _currentY = endY;
        }
    }
}
