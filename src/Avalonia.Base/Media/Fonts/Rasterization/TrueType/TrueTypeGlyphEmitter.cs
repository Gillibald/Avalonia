using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Emits a hinted zone's outline (excluding the phantom points) as quadratic contours
    /// into a geometry sink. The walk mirrors the float outline walker exactly - on-curve
    /// starts, implied midpoints between consecutive off-curve points, and the off-curve
    /// start case - so hinted and unhinted outlines produce identical verb sequences and
    /// the only difference is the 26.6 quantization and whatever the instructions moved.
    /// Zone coordinates are 26.6 device pixels; the transform applies any residual mapping
    /// (y-flip, subpixel stretch) on top.
    /// </summary>
    internal static class TrueTypeGlyphEmitter
    {
        public static void Emit(TrueTypeZone zone, Matrix transform, IGeometryContext sink)
        {
            sink.SetFillRule(FillRule.NonZero);

            var startPointIndex = 0;

            for (var contourIndex = 0; contourIndex < zone.ContourCount; contourIndex++)
            {
                int endPointIndex = zone.ContourEnds[contourIndex];

                if (endPointIndex >= zone.PointCount)
                {
                    endPointIndex = zone.PointCount - 1;
                }

                var contourPointCount = endPointIndex - startPointIndex + 1;

                if (contourPointCount <= 0)
                {
                    startPointIndex = endPointIndex + 1;
                    continue;
                }

                EmitContour(zone, transform, sink, startPointIndex, endPointIndex, contourPointCount);
                startPointIndex = endPointIndex + 1;
            }
        }

        private static void EmitContour(
            TrueTypeZone zone,
            Matrix transform,
            IGeometryContext sink,
            int startPointIndex,
            int endPointIndex,
            int contourPointCount)
        {
            Point At(int index) => new(zone.CurX[index] / 64.0, zone.CurY[index] / 64.0);
            bool OnCurve(int index) => (zone.Tags[index] & TrueTypeZone.OnCurve) != 0;

            if (OnCurve(startPointIndex))
            {
                sink.BeginFigure(transform.Transform(At(startPointIndex)), true);

                var i = contourPointCount == 1 ? startPointIndex : startPointIndex + 1;
                var processingStartIndex = i;
                var maxSegments = Math.Max(1, contourPointCount * 3);
                var segmentsProcessed = 0;

                while (segmentsProcessed++ < maxSegments)
                {
                    var currentIdx = startPointIndex + (i - startPointIndex) % contourPointCount;
                    var curPoint = At(currentIdx);

                    if (OnCurve(currentIdx))
                    {
                        sink.LineTo(transform.Transform(curPoint));
                        i++;
                    }
                    else
                    {
                        var nextIdx = startPointIndex + (i + 1 - startPointIndex) % contourPointCount;
                        var nextPoint = At(nextIdx);

                        if (OnCurve(nextIdx))
                        {
                            sink.QuadraticBezierTo(transform.Transform(curPoint), transform.Transform(nextPoint));
                            i += 2;
                        }
                        else
                        {
                            var implied = new Point((curPoint.X + nextPoint.X) / 2.0, (curPoint.Y + nextPoint.Y) / 2.0);

                            sink.QuadraticBezierTo(transform.Transform(curPoint), transform.Transform(implied));
                            i++;
                        }
                    }

                    var checkIdx = startPointIndex + (i - startPointIndex) % contourPointCount;

                    if (checkIdx == processingStartIndex && segmentsProcessed > 0)
                    {
                        break;
                    }
                }

                sink.EndFigure(true);
            }
            else
            {
                // An off-curve start opens on the implied midpoint between the last and
                // first points of the contour.
                var first = At(startPointIndex);
                var last = At(endPointIndex);
                var impliedStart = new Point((last.X + first.X) / 2.0, (last.Y + first.Y) / 2.0);

                sink.BeginFigure(transform.Transform(impliedStart), true);

                var idxWalker = 0;
                var maxSegments = contourPointCount * 3;
                var segmentsProcessed = 0;

                while (segmentsProcessed++ < maxSegments)
                {
                    var curIdx = startPointIndex + idxWalker;
                    var nextIdxOffset = idxWalker == contourPointCount - 1 ? 0 : idxWalker + 1;
                    var nextIdx = startPointIndex + nextIdxOffset;
                    var curPoint = At(curIdx);

                    if (OnCurve(curIdx))
                    {
                        sink.LineTo(transform.Transform(curPoint));
                        idxWalker = nextIdxOffset;
                    }
                    else
                    {
                        var nextPoint = At(nextIdx);

                        if (OnCurve(nextIdx))
                        {
                            sink.QuadraticBezierTo(transform.Transform(curPoint), transform.Transform(nextPoint));
                            idxWalker = nextIdxOffset == contourPointCount - 1 ? 0 : nextIdxOffset + 1;
                        }
                        else
                        {
                            var implied = new Point((curPoint.X + nextPoint.X) / 2.0, (curPoint.Y + nextPoint.Y) / 2.0);

                            sink.QuadraticBezierTo(transform.Transform(curPoint), transform.Transform(implied));
                            idxWalker = nextIdxOffset == contourPointCount - 1 ? 0 : nextIdxOffset + 1;
                        }
                    }

                    if (idxWalker == 0 && segmentsProcessed > 1)
                    {
                        break;
                    }
                }

                sink.EndFigure(true);
            }
        }
    }
}
