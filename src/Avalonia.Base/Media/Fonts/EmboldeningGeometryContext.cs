using System.Collections.Generic;
using Avalonia.Media.Fonts.Tables.Glyf;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// An <see cref="IGeometryContext"/> that collects a whole glyph outline so it can be emboldened
    /// with <see cref="OutlineEmbolden"/> and then replayed into another context.
    /// </summary>
    /// <remarks>
    /// Emboldening needs every contour at once, because the outline's overall winding decides which
    /// side of a contour is filled. Curve control points are offset like on-curve points, as FreeType
    /// does for cubic outlines.
    /// </remarks>
    internal sealed class EmboldeningGeometryContext : IGeometryContext
    {
        private const byte OnCurve = 0;
        private const byte QuadraticControl = 1;
        private const byte CubicControl = 2;

        private readonly List<Point> _points = new();
        private readonly List<byte> _kinds = new();
        private readonly List<int> _contourEnds = new();
        private readonly List<bool> _contourClosed = new();
        private int _contourStart = -1;
        private FillRule? _fillRule;

        public void BeginFigure(Point startPoint, bool isFilled = true)
        {
            EndContour(false);

            _contourStart = _points.Count;

            Add(startPoint, OnCurve);
        }

        public void LineTo(Point point, bool isStroked = true) => Add(point, OnCurve);

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
        {
            Add(controlPoint, QuadraticControl);
            Add(endPoint, OnCurve);
        }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
        {
            Add(controlPoint1, CubicControl);
            Add(controlPoint2, CubicControl);
            Add(endPoint, OnCurve);
        }

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked = true) => Add(point, OnCurve);

        public void EndFigure(bool isClosed) => EndContour(isClosed);

        public void SetFillRule(FillRule fillRule) => _fillRule = fillRule;

        public void Dispose() { }

        /// <summary>
        /// Emboldens the collected outline by <paramref name="strength"/> design units in each
        /// direction and emits it into <paramref name="context"/> through <paramref name="transform"/>.
        /// </summary>
        public void Emit(IGeometryContext context, Matrix transform, double strength)
        {
            EndContour(false);

            if (_fillRule is { } fillRule)
            {
                context.SetFillRule(fillRule);
            }

            var points = _points.ToArray();

            OutlineEmbolden.Embolden(points, _contourEnds.ToArray(), strength, strength);

            var start = 0;

            for (var c = 0; c < _contourEnds.Count; c++)
            {
                var end = _contourEnds[c];

                context.BeginFigure(transform.Transform(points[start]));

                var i = start + 1;

                while (i <= end)
                {
                    switch (_kinds[i])
                    {
                        case CubicControl when i + 2 <= end:
                            context.CubicBezierTo(transform.Transform(points[i]),
                                transform.Transform(points[i + 1]), transform.Transform(points[i + 2]));
                            i += 3;
                            break;
                        case QuadraticControl when i + 1 <= end:
                            context.QuadraticBezierTo(transform.Transform(points[i]),
                                transform.Transform(points[i + 1]));
                            i += 2;
                            break;
                        default:
                            context.LineTo(transform.Transform(points[i]));
                            i++;
                            break;
                    }
                }

                context.EndFigure(_contourClosed[c]);

                start = end + 1;
            }
        }

        private void Add(Point point, byte kind)
        {
            if (_contourStart < 0)
            {
                _contourStart = _points.Count;
            }

            _points.Add(point);
            _kinds.Add(kind);
        }

        private void EndContour(bool isClosed)
        {
            if (_contourStart < 0)
            {
                return;
            }

            if (_points.Count > _contourStart)
            {
                _contourEnds.Add(_points.Count - 1);
                _contourClosed.Add(isClosed);
            }

            _contourStart = -1;
        }
    }
}
