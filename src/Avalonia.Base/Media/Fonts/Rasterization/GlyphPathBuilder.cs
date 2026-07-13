using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The contour verbs captured by <see cref="GlyphPathBuilder"/>.
    /// </summary>
    internal enum GlyphPathVerb : byte
    {
        /// <summary>Starts a figure; consumes 2 point values.</summary>
        MoveTo,

        /// <summary>A straight segment; consumes 2 point values.</summary>
        LineTo,

        /// <summary>A quadratic segment; consumes 4 point values (control, end).</summary>
        QuadTo,

        /// <summary>A cubic segment; consumes 6 point values (control1, control2, end).</summary>
        CubicTo,

        /// <summary>Ends a figure; consumes no point values. Fill semantics close the contour.</summary>
        Close,
    }

    /// <summary>
    /// An <see cref="IGeometryContext"/> sink that captures glyph contours as a flat verb / point
    /// stream for the managed rasterizer — a third sink besides real backend geometry and
    /// <see cref="BoundsGeometryContext"/>. Points are stored exactly as emitted; the caller bakes
    /// the font-unit → device transform into the table walk, so one captured path can be
    /// rasterized several times (e.g. at different subpixel phases) without re-walking the tables.
    /// </summary>
    /// <remarks>
    /// Instances are meant to be long-lived and reused via <see cref="Reset"/> (one per thread on
    /// the rasterization path), so the backing arrays grow geometrically and are simply retained —
    /// steady-state reuse allocates nothing. Not thread-safe. <see cref="IDisposable.Dispose"/> is
    /// a no-op like the other font geometry sinks: table walkers receive the context by parameter
    /// and must not tear it down.
    /// </remarks>
    internal sealed class GlyphPathBuilder : IGeometryContext
    {
        private byte[] _verbs;
        private float[] _points;
        private int _verbCount;
        private int _pointCount;
        private bool _figureOpen;

        public GlyphPathBuilder(int initialVerbCapacity = 64)
        {
            _verbs = new byte[initialVerbCapacity];
            _points = new float[initialVerbCapacity * 4];
        }

        /// <summary>
        /// The fill rule the table walker declared. Font outline walkers emit
        /// <see cref="FillRule.NonZero"/>; the default matches so a hand-built path behaves like a
        /// glyph unless it opts out.
        /// </summary>
        public FillRule FillRule { get; private set; } = FillRule.NonZero;

        /// <summary>The captured verbs.</summary>
        public ReadOnlySpan<byte> Verbs => _verbs.AsSpan(0, _verbCount);

        /// <summary>The captured point values, consumed per verb in <see cref="Verbs"/> order.</summary>
        public ReadOnlySpan<float> Points => _points.AsSpan(0, _pointCount);

        /// <summary>Clears the captured path so the instance can record another glyph.</summary>
        public void Reset()
        {
            _verbCount = 0;
            _pointCount = 0;
            _figureOpen = false;
            FillRule = FillRule.NonZero;
        }

        public void SetFillRule(FillRule fillRule) => FillRule = fillRule;

        public void BeginFigure(Point startPoint, bool isFilled = true)
        {
            // Fill-only sink: isFilled is meaningless for glyph outlines. A dangling open figure
            // is closed implicitly so winding stays consistent for the rasterizer.
            if (_figureOpen)
            {
                AppendVerb(GlyphPathVerb.Close);
            }

            _figureOpen = true;
            AppendVerb(GlyphPathVerb.MoveTo);
            AppendPoint(startPoint);
        }

        public void LineTo(Point point, bool isStroked = true)
        {
            AppendVerb(GlyphPathVerb.LineTo);
            AppendPoint(point);
        }

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
        {
            AppendVerb(GlyphPathVerb.QuadTo);
            AppendPoint(controlPoint);
            AppendPoint(endPoint);
        }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
        {
            AppendVerb(GlyphPathVerb.CubicTo);
            AppendPoint(controlPoint1);
            AppendPoint(controlPoint2);
            AppendPoint(endPoint);
        }

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked = true)
        {
            // Font table walkers never emit arcs. Degrade to a straight segment so an unexpected
            // caller still produces bounded output instead of failing mid-rasterization.
            LineTo(point, isStroked);
        }

        public void EndFigure(bool isClosed)
        {
            // Fill semantics close every contour, so an "open" figure records Close as well.
            if (_figureOpen)
            {
                AppendVerb(GlyphPathVerb.Close);
                _figureOpen = false;
            }
        }

        public void Dispose()
        {
            // Intentionally empty — see the class remarks.
        }

        private void AppendVerb(GlyphPathVerb verb)
        {
            if (_verbCount == _verbs.Length)
            {
                Array.Resize(ref _verbs, _verbs.Length * 2);
            }

            _verbs[_verbCount++] = (byte)verb;
        }

        private void AppendPoint(Point point)
        {
            if (_pointCount + 2 > _points.Length)
            {
                Array.Resize(ref _points, _points.Length * 2);
            }

            _points[_pointCount++] = (float)point.X;
            _points[_pointCount++] = (float)point.Y;
        }
    }
}
