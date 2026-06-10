using System;
using Avalonia.Platform;
using Avalonia.Media;

namespace Avalonia.Svg.Parsing;

/// <summary>
/// Parses SVG path data (the <c>d</c> attribute) into an <see cref="IGeometryContext"/>.
/// Implements the SVG 1.1 / SVG 2 path grammar: absolute and relative commands,
/// implicit command repetition, smooth curve reflection, juxtaposed arc flags and
/// scientific notation.
/// </summary>
/// <remarks>
/// On malformed input a <see cref="FormatException"/> is thrown <i>after</i> the
/// segments preceding the error have been emitted — per the SVG error-handling
/// rules the valid prefix of a path is still rendered, which callers get by
/// catching the exception and keeping the partially-built geometry.
/// </remarks>
internal static class SvgPathParser
{
    public static void Parse(ReadOnlySpan<char> data, IGeometryContext sink)
    {
        var tokenizer = new SvgTokenizer(data);

        var current = default(Point);
        var subpathStart = default(Point);
        var lastCubicControl = default(Point);
        var lastQuadControl = default(Point);
        var command = '\0';
        var previousCommand = '\0';
        var figureOpen = false;

        try
        {
            var first = true;
            while (!tokenizer.IsAtEnd)
            {
                if (tokenizer.TryReadCommand(out var c))
                {
                    command = c;
                }
                else if (command == '\0')
                {
                    throw new FormatException("SVG path data must start with a command.");
                }
                else
                {
                    // Implicit repetition: extra coordinate sets repeat the previous
                    // command; after a moveto they continue as linetos.
                    command = command switch
                    {
                        'M' => 'L',
                        'm' => 'l',
                        'Z' or 'z' => throw new FormatException("A close-path command cannot be repeated implicitly."),
                        _ => command,
                    };
                }

                if (first && command is not ('M' or 'm'))
                    throw new FormatException("SVG path data must start with a moveto command.");
                first = false;

                var relative = char.IsLower(command);
                switch (char.ToUpperInvariant(command))
                {
                    case 'M':
                    {
                        var p = ReadPoint(ref tokenizer, current, relative);
                        if (figureOpen)
                            sink.EndFigure(false);
                        current = subpathStart = p;
                        sink.BeginFigure(current, true);
                        figureOpen = true;
                        break;
                    }
                    case 'Z':
                    {
                        if (figureOpen)
                            sink.EndFigure(true);
                        figureOpen = false;
                        current = subpathStart;
                        break;
                    }
                    case 'L':
                    {
                        var p = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.LineTo(p);
                        current = p;
                        break;
                    }
                    case 'H':
                    {
                        var x = ReadNumber(ref tokenizer);
                        var p = new Point(relative ? current.X + x : x, current.Y);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.LineTo(p);
                        current = p;
                        break;
                    }
                    case 'V':
                    {
                        var y = ReadNumber(ref tokenizer);
                        var p = new Point(current.X, relative ? current.Y + y : y);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.LineTo(p);
                        current = p;
                        break;
                    }
                    case 'C':
                    {
                        var c1 = ReadPoint(ref tokenizer, current, relative);
                        var c2 = ReadPoint(ref tokenizer, current, relative);
                        var end = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.CubicBezierTo(c1, c2, end);
                        lastCubicControl = c2;
                        current = end;
                        break;
                    }
                    case 'S':
                    {
                        var c1 = IsCubic(previousCommand) ? Reflect(current, lastCubicControl) : current;
                        var c2 = ReadPoint(ref tokenizer, current, relative);
                        var end = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.CubicBezierTo(c1, c2, end);
                        lastCubicControl = c2;
                        current = end;
                        break;
                    }
                    case 'Q':
                    {
                        var c1 = ReadPoint(ref tokenizer, current, relative);
                        var end = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.QuadraticBezierTo(c1, end);
                        lastQuadControl = c1;
                        current = end;
                        break;
                    }
                    case 'T':
                    {
                        var c1 = IsQuadratic(previousCommand) ? Reflect(current, lastQuadControl) : current;
                        var end = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);
                        sink.QuadraticBezierTo(c1, end);
                        lastQuadControl = c1;
                        current = end;
                        break;
                    }
                    case 'A':
                    {
                        var rx = Math.Abs(ReadNumber(ref tokenizer));
                        var ry = Math.Abs(ReadNumber(ref tokenizer));
                        var rotation = ReadNumber(ref tokenizer);
                        var largeArc = ReadFlag(ref tokenizer);
                        var sweep = ReadFlag(ref tokenizer);
                        var end = ReadPoint(ref tokenizer, current, relative);
                        EnsureFigure(sink, ref figureOpen, current);

                        // Per spec a zero radius degenerates to a straight line.
                        if (rx == 0 || ry == 0)
                        {
                            sink.LineTo(end);
                        }
                        else
                        {
                            sink.ArcTo(
                                end,
                                new Size(rx, ry),
                                Matrix.ToRadians(rotation),
                                largeArc,
                                sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                        }

                        current = end;
                        break;
                    }
                    default:
                        throw new FormatException($"Unknown SVG path command '{command}'.");
                }

                previousCommand = command;
            }
        }
        finally
        {
            if (figureOpen)
                sink.EndFigure(false);
        }
    }

    private static void EnsureFigure(IGeometryContext sink, ref bool figureOpen, Point current)
    {
        // A drawing command after a close-path implicitly starts a new subpath
        // at the previous subpath's start point.
        if (!figureOpen)
        {
            sink.BeginFigure(current, true);
            figureOpen = true;
        }
    }

    private static bool IsCubic(char command) => command is 'C' or 'c' or 'S' or 's';

    private static bool IsQuadratic(char command) => command is 'Q' or 'q' or 'T' or 't';

    private static Point Reflect(Point current, Point control) =>
        new(2 * current.X - control.X, 2 * current.Y - control.Y);

    private static double ReadNumber(ref SvgTokenizer tokenizer)
    {
        if (!tokenizer.TryReadNumber(out var value))
            throw new FormatException("Expected a number in SVG path data.");
        return value;
    }

    private static bool ReadFlag(ref SvgTokenizer tokenizer)
    {
        if (!tokenizer.TryReadFlag(out var flag))
            throw new FormatException("Expected an arc flag ('0' or '1') in SVG path data.");
        return flag;
    }

    private static Point ReadPoint(ref SvgTokenizer tokenizer, Point current, bool relative)
    {
        var x = ReadNumber(ref tokenizer);
        var y = ReadNumber(ref tokenizer);
        return relative ? new Point(current.X + x, current.Y + y) : new Point(x, y);
    }
}
