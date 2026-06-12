using System;
using System.Collections.Generic;
using Avalonia.Platform;
using Avalonia.Media;

namespace Avalonia.Svg.Parsing;

/// <summary>A path vertex with its incoming and outgoing tangent directions.</summary>
internal readonly struct SvgPathVertex
{
    public SvgPathVertex(Point position, Vector? inDirection, Vector? outDirection)
    {
        Position = position;
        InDirection = inDirection;
        OutDirection = outDirection;
    }

    public Point Position { get; }

    /// <summary>Unit direction of the segment arriving at this vertex, or null at a subpath start.</summary>
    public Vector? InDirection { get; }

    /// <summary>Unit direction of the segment leaving this vertex, or null at a subpath end.</summary>
    public Vector? OutDirection { get; }

    /// <summary>
    /// The marker orientation angle (radians): the bisector of in/out at interior
    /// vertices, the single direction at the ends.
    /// </summary>
    public double Angle
    {
        get
        {
            if (InDirection is { } incoming && OutDirection is { } outgoing)
            {
                var sum = incoming + outgoing;
                if (sum.Length > 1e-9)
                    return Math.Atan2(sum.Y, sum.X);
                // Opposite directions (a cusp): fall back to the incoming direction.
                return Math.Atan2(incoming.Y, incoming.X);
            }

            var direction = OutDirection ?? InDirection ?? new Vector(1, 0);
            return Math.Atan2(direction.Y, direction.X);
        }
    }
}

/// <summary>
/// Receives parsed path commands (via <see cref="SvgPathParser"/>) and produces
/// a flattened arc-length parameterization plus the vertex/tangent list — the
/// inputs for marker placement and text-on-path layout. Curve flattening
/// adapts the subdivision to the curve's length: text-on-path takes both the
/// position and the tangent from the flat chords, so a chord must stay well
/// below a glyph advance even when a single command spans a whole circle.
/// </summary>
internal sealed class SvgPathSampler : IGeometryContext
{
    /// <summary>Target chord length, in user units.</summary>
    private const double SampleSpacing = 2.0;
    private const int MinCurveSteps = 8;
    private const int MaxCurveSteps = 1024;

    private static int StepsForLength(double estimatedLength)
    {
        if (double.IsNaN(estimatedLength) || estimatedLength <= SampleSpacing * MinCurveSteps)
            return MinCurveSteps;

        return (int)Math.Min(MaxCurveSteps, Math.Ceiling(estimatedLength / SampleSpacing));
    }

    private readonly List<Point> _points = new();
    private readonly List<double> _lengths = new();
    private readonly List<SvgPathVertex> _vertices = new();

    private Point _current;
    private Point _subpathStart;
    private int _subpathFirstVertex = -1;
    private Vector? _pendingIn;

    public static SvgPathSampler Parse(ReadOnlySpan<char> data)
    {
        var sampler = new SvgPathSampler();
        try
        {
            SvgPathParser.Parse(data, sampler);
        }
        catch (FormatException)
        {
            // The valid prefix is sampled, mirroring the renderer's behavior.
        }

        return sampler;
    }

    public IReadOnlyList<SvgPathVertex> Vertices => _vertices;

    public double TotalLength => _lengths.Count > 0 ? _lengths[_lengths.Count - 1] : 0;

    /// <summary>Samples the position and tangent angle at an arc-length distance.</summary>
    public bool TryGetPointAtLength(double length, out Point position, out double angle)
    {
        if (_points.Count < 2 || length < 0 || length > TotalLength)
        {
            position = default;
            angle = 0;
            return false;
        }

        // Binary search the cumulative length table.
        var low = 0;
        var high = _lengths.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_lengths[mid] < length)
                low = mid + 1;
            else
                high = mid;
        }

        var index = Math.Max(1, low);
        var segmentStart = _points[index - 1];
        var segmentEnd = _points[index];
        var startLength = _lengths[index - 1];
        var segmentLength = _lengths[index] - startLength;

        var t = segmentLength > 1e-12 ? (length - startLength) / segmentLength : 0;
        position = new Point(
            segmentStart.X + (segmentEnd.X - segmentStart.X) * t,
            segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * t);
        angle = Math.Atan2(segmentEnd.Y - segmentStart.Y, segmentEnd.X - segmentStart.X);
        return true;
    }

    private static Vector? Direction(Point from, Point to)
    {
        var v = new Vector(to.X - from.X, to.Y - from.Y);
        var length = v.Length;
        return length > 1e-9 ? v / length : null;
    }

    private static double PointDistance(Point from, Point to)
        => new Vector(to.X - from.X, to.Y - from.Y).Length;

    private void AddSamplePoint(Point point)
    {
        if (_points.Count == 0)
        {
            _points.Add(point);
            _lengths.Add(0);
            return;
        }

        var previous = _points[_points.Count - 1];
        var distance = new Vector(point.X - previous.X, point.Y - previous.Y).Length;
        _points.Add(point);
        _lengths.Add(_lengths[_lengths.Count - 1] + distance);
    }

    private void AddVertex(Point position, Vector? outDirection)
    {
        _vertices.Add(new SvgPathVertex(position, _pendingIn, outDirection));
        _pendingIn = null;
    }

    private void CompleteSegment(Point end, Vector? startDirection, Vector? endDirection)
    {
        // Patch the previous vertex's out-direction (it was added before the
        // segment's direction was known).
        if (_vertices.Count > 0)
        {
            var last = _vertices[_vertices.Count - 1];
            if (last.OutDirection == null && startDirection != null)
                _vertices[_vertices.Count - 1] = new SvgPathVertex(last.Position, last.InDirection, startDirection);
        }

        _pendingIn = endDirection;
        AddVertex(end, null);
        _current = end;
    }

    public void BeginFigure(Point startPoint, bool isFilled = true)
    {
        _current = _subpathStart = startPoint;
        _subpathFirstVertex = _vertices.Count;
        _pendingIn = null;
        AddSamplePoint(startPoint);
        AddVertex(startPoint, null);
    }

    public void LineTo(Point point, bool isStroked = true)
    {
        var direction = Direction(_current, point);
        AddSamplePoint(point);
        CompleteSegment(point, direction, direction);
    }

    public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
    {
        var p0 = _current;
        // The control polygon length bounds the curve length from above.
        var steps = StepsForLength(
            PointDistance(p0, controlPoint1) + PointDistance(controlPoint1, controlPoint2)
            + PointDistance(controlPoint2, endPoint));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var u = 1 - t;
            var point = new Point(
                u * u * u * p0.X + 3 * u * u * t * controlPoint1.X + 3 * u * t * t * controlPoint2.X + t * t * t * endPoint.X,
                u * u * u * p0.Y + 3 * u * u * t * controlPoint1.Y + 3 * u * t * t * controlPoint2.Y + t * t * t * endPoint.Y);
            AddSamplePoint(point);
        }

        // Endpoint tangents from the control polygon, skipping degenerate legs.
        var startDirection = Direction(p0, controlPoint1) ?? Direction(p0, controlPoint2) ?? Direction(p0, endPoint);
        var endDirection = Direction(controlPoint2, endPoint) ?? Direction(controlPoint1, endPoint) ?? Direction(p0, endPoint);
        CompleteSegment(endPoint, startDirection, endDirection);
    }

    public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
    {
        var p0 = _current;
        var steps = StepsForLength(PointDistance(p0, controlPoint) + PointDistance(controlPoint, endPoint));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var u = 1 - t;
            var point = new Point(
                u * u * p0.X + 2 * u * t * controlPoint.X + t * t * endPoint.X,
                u * u * p0.Y + 2 * u * t * controlPoint.Y + t * t * endPoint.Y);
            AddSamplePoint(point);
        }

        var startDirection = Direction(p0, controlPoint) ?? Direction(p0, endPoint);
        var endDirection = Direction(controlPoint, endPoint) ?? Direction(p0, endPoint);
        CompleteSegment(endPoint, startDirection, endDirection);
    }

    public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked = true)
    {
        // Endpoint to center parameterization per SVG implementation notes F.6.5.
        var p0 = _current;
        double rx = Math.Abs(size.Width), ry = Math.Abs(size.Height);
        var cos = Math.Cos(rotationAngle);
        var sin = Math.Sin(rotationAngle);

        var dx = (p0.X - point.X) / 2;
        var dy = (p0.Y - point.Y) / 2;
        var x1p = cos * dx + sin * dy;
        var y1p = -sin * dx + cos * dy;

        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var sign = isLargeArc != (sweepDirection == SweepDirection.Clockwise) ? 1.0 : -1.0;
        var numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        var denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coefficient = sign * Math.Sqrt(Math.Max(0, numerator / denominator));

        var cxp = coefficient * rx * y1p / ry;
        var cyp = -coefficient * ry * x1p / rx;
        var cx = cos * cxp - sin * cyp + (p0.X + point.X) / 2;
        var cy = sin * cxp + cos * cyp + (p0.Y + point.Y) / 2;

        var theta1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
        var theta2 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
        var delta = theta2 - theta1;

        if (sweepDirection == SweepDirection.Clockwise && delta < 0)
            delta += 2 * Math.PI;
        else if (sweepDirection == SweepDirection.CounterClockwise && delta > 0)
            delta -= 2 * Math.PI;

        Point At(double theta) => new(
            cx + rx * Math.Cos(theta) * cos - ry * Math.Sin(theta) * sin,
            cy + rx * Math.Cos(theta) * sin + ry * Math.Sin(theta) * cos);

        Vector TangentAt(double theta)
        {
            var tx = -rx * Math.Sin(theta) * cos - ry * Math.Cos(theta) * sin;
            var ty = -rx * Math.Sin(theta) * sin + ry * Math.Cos(theta) * cos;
            var v = new Vector(tx, ty) * Math.Sign(delta == 0 ? 1 : delta);
            var length = v.Length;
            return length > 1e-9 ? v / length : new Vector(1, 0);
        }

        var steps = StepsForLength(Math.Abs(delta) * Math.Max(rx, ry));
        for (var i = 1; i <= steps; i++)
            AddSamplePoint(At(theta1 + delta * i / steps));

        CompleteSegment(point, TangentAt(theta1), TangentAt(theta2));
    }

    public void EndFigure(bool isClosed)
    {
        if (isClosed && _subpathFirstVertex >= 0)
        {
            var direction = Direction(_current, _subpathStart);
            if (direction != null)
            {
                AddSamplePoint(_subpathStart);
                CompleteSegment(_subpathStart, direction, direction);
                // The closing vertex coincides with the subpath start: merge the
                // closure's incoming direction into the start vertex (which keeps
                // its position as Vertices[0] for marker-start) and drop the
                // trailing duplicate, so 'auto' markers bisect the closure.
                var first = _vertices[_subpathFirstVertex];
                _vertices[_subpathFirstVertex] = new SvgPathVertex(
                    first.Position, direction, first.OutDirection);
                _vertices.RemoveAt(_vertices.Count - 1);
            }

            _current = _subpathStart;
        }

        _subpathFirstVertex = -1;
        _pendingIn = null;
    }

    public void SetFillRule(FillRule fillRule)
    {
    }

    public void Dispose()
    {
    }
}
