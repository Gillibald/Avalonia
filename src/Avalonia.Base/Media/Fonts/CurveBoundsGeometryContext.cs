using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// An <see cref="IGeometryContext"/> that accumulates the exact bounding box of the curves it is
    /// handed, from segment end points and the curve extrema, instead of building geometry.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="BoundsGeometryContext"/>, control points only count where the curve reaches
    /// them. The difference matters for a transformed outline: a font places points at its curves'
    /// horizontal and vertical extrema, but after a shear the extrema of a round contour lie between
    /// the points, and the control points overshoot them.
    /// </remarks>
    internal sealed class CurveBoundsGeometryContext : IGeometryContext
    {
        private double _minX = double.MaxValue;
        private double _minY = double.MaxValue;
        private double _maxX = double.MinValue;
        private double _maxY = double.MinValue;
        private bool _hasPoints;
        private Point _current;

        /// <summary>Whether no point has been added, as for an empty glyph.</summary>
        public bool IsEmpty => !_hasPoints;

        /// <summary>
        /// The accumulated box rounded out to whole units, or the zero box for an empty glyph.
        /// </summary>
        public GlyphBounds ToGlyphBounds()
            => _hasPoints
                ? new GlyphBounds(ClampToShort(Math.Floor(_minX)), ClampToShort(Math.Floor(_minY)),
                    ClampToShort(Math.Ceiling(_maxX)), ClampToShort(Math.Ceiling(_maxY)))
                : default;

        private static short ClampToShort(double value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

        private void Add(double x, double y)
        {
            _hasPoints = true;
            if (x < _minX) _minX = x;
            if (y < _minY) _minY = y;
            if (x > _maxX) _maxX = x;
            if (y > _maxY) _maxY = y;
        }

        private void MoveTo(Point point)
        {
            Add(point.X, point.Y);
            _current = point;
        }

        public void BeginFigure(Point startPoint, bool isFilled = true) => MoveTo(startPoint);

        public void LineTo(Point point, bool isStroked = true) => MoveTo(point);

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
        {
            var p0 = _current;

            AddQuadraticExtremum(p0, controlPoint, endPoint, p0.X - 2 * controlPoint.X + endPoint.X,
                p0.X - controlPoint.X);
            AddQuadraticExtremum(p0, controlPoint, endPoint, p0.Y - 2 * controlPoint.Y + endPoint.Y,
                p0.Y - controlPoint.Y);

            MoveTo(endPoint);
        }

        // B'(t) = 0 at t = (p0 - p1) / (p0 - 2 p1 + p2) in the axis the coefficients come from; the
        // point is added in both axes, which is exact because it lies on the curve.
        private void AddQuadraticExtremum(Point p0, Point p1, Point p2, double denominator, double numerator)
        {
            if (denominator == 0)
            {
                return;
            }

            var t = numerator / denominator;

            if (t > 0 && t < 1)
            {
                var u = 1 - t;

                Add(u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
            }
        }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
        {
            var p0 = _current;

            AddCubicExtrema(p0, controlPoint1, controlPoint2, endPoint, p0.X, controlPoint1.X, controlPoint2.X,
                endPoint.X);
            AddCubicExtrema(p0, controlPoint1, controlPoint2, endPoint, p0.Y, controlPoint1.Y, controlPoint2.Y,
                endPoint.Y);

            MoveTo(endPoint);
        }

        // B'(t) / 3 = a t^2 + b t + c with a = -v0 + 3 v1 - 3 v2 + v3, b = 2 (v0 - 2 v1 + v2), c = v1 - v0.
        private void AddCubicExtrema(Point p0, Point p1, Point p2, Point p3, double v0, double v1, double v2,
            double v3)
        {
            var a = -v0 + 3 * v1 - 3 * v2 + v3;
            var b = 2 * (v0 - 2 * v1 + v2);
            var c = v1 - v0;

            if (Math.Abs(a) < 1e-12)
            {
                if (b != 0)
                {
                    AddCubicPoint(p0, p1, p2, p3, -c / b);
                }

                return;
            }

            var discriminant = b * b - 4 * a * c;

            if (discriminant < 0)
            {
                return;
            }

            var root = Math.Sqrt(discriminant);

            AddCubicPoint(p0, p1, p2, p3, (-b + root) / (2 * a));
            AddCubicPoint(p0, p1, p2, p3, (-b - root) / (2 * a));
        }

        private void AddCubicPoint(Point p0, Point p1, Point p2, Point p3, double t)
        {
            if (!(t > 0 && t < 1))
            {
                return;
            }

            var u = 1 - t;
            var w0 = u * u * u;
            var w1 = 3 * u * u * t;
            var w2 = 3 * u * t * t;
            var w3 = t * t * t;

            Add(w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X, w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
        }

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked = true) => MoveTo(point);

        public void EndFigure(bool isClosed) { }

        public void SetFillRule(FillRule fillRule) { }

        public void Dispose() { }
    }
}
