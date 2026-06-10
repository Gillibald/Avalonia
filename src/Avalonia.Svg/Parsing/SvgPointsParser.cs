using System;
using Avalonia.Platform;

namespace Avalonia.Svg.Parsing;

/// <summary>
/// Parses the <c>points</c> attribute of <c>polyline</c>/<c>polygon</c> into an
/// <see cref="IGeometryContext"/>.
/// </summary>
internal static class SvgPointsParser
{
    /// <summary>
    /// Emits the point list as a single figure. Per the SVG error-handling rules an
    /// odd trailing coordinate drops only the incomplete pair; the parsed prefix
    /// still renders.
    /// </summary>
    /// <returns>True when at least one point was emitted.</returns>
    public static bool Parse(ReadOnlySpan<char> input, IGeometryContext sink, bool close)
    {
        var tokenizer = new SvgTokenizer(input);
        var figureOpen = false;

        try
        {
            while (tokenizer.TryReadNumber(out var x))
            {
                if (!tokenizer.TryReadNumber(out var y))
                    break;

                var point = new Point(x, y);
                if (!figureOpen)
                {
                    sink.BeginFigure(point, true);
                    figureOpen = true;
                }
                else
                {
                    sink.LineTo(point);
                }
            }
        }
        finally
        {
            if (figureOpen)
                sink.EndFigure(close);
        }

        return figureOpen;
    }
}
