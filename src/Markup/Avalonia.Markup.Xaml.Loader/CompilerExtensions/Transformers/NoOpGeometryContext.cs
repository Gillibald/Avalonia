using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// A geometry sink that discards every segment. <see cref="Avalonia.Media.Svg.Parsing.SvgPathParser"/>
    /// decides a path's validity purely from the token stream — it throws a
    /// <see cref="System.FormatException"/> on malformed data — so the emitted
    /// geometry is not needed when validating, only the exception.
    /// </summary>
    internal sealed class NoOpGeometryContext : IGeometryContext
    {
        public static readonly NoOpGeometryContext Instance = new();

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked = true) { }

        public void BeginFigure(Point startPoint, bool isFilled = true) { }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true) { }

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true) { }

        public void LineTo(Point point, bool isStroked = true) { }

        public void EndFigure(bool isClosed) { }

        public void SetFillRule(FillRule fillRule) { }

        public void Dispose() { }
    }
}
