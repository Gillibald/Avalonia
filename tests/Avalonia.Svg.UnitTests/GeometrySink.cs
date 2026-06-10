using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// An <see cref="IGeometryContext"/> that records operations as compact readable
/// strings, so parser tests can assert emitted geometry without a render platform.
/// </summary>
internal sealed class GeometrySink : IGeometryContext
{
    public List<string> Operations { get; } = new();

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string F(Point point) => $"{F(point.X)},{F(point.Y)}";

    public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked = true) =>
        Operations.Add($"A {F(size.Width)},{F(size.Height)} {F(rotationAngle)} {(isLargeArc ? 1 : 0)} {(sweepDirection == SweepDirection.Clockwise ? 1 : 0)} {F(point)}");

    public void BeginFigure(Point startPoint, bool isFilled = true) =>
        Operations.Add($"M {F(startPoint)}");

    public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true) =>
        Operations.Add($"C {F(controlPoint1)} {F(controlPoint2)} {F(endPoint)}");

    public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true) =>
        Operations.Add($"Q {F(controlPoint)} {F(endPoint)}");

    public void LineTo(Point point, bool isStroked = true) =>
        Operations.Add($"L {F(point)}");

    public void EndFigure(bool isClosed) =>
        Operations.Add(isClosed ? "Z" : "End");

    public void SetFillRule(FillRule fillRule) =>
        Operations.Add($"FillRule {fillRule}");

    public void Dispose()
    {
    }
}
