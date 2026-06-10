using System;
using Avalonia.Media;

namespace Avalonia.Svg.Parsing;

internal enum SvgPaintKind
{
    /// <summary>No paint; the shape part is not rendered.</summary>
    None,
    /// <summary>A solid color.</summary>
    Color,
    /// <summary>The inherited <c>color</c> property value.</summary>
    CurrentColor,
    /// <summary>A <c>url(#...)</c> paint-server reference (gradients/patterns; later phases).</summary>
    Reference,
}

/// <summary>An SVG paint value (the <c>fill</c> and <c>stroke</c> properties).</summary>
internal readonly struct SvgPaint
{
    private SvgPaint(SvgPaintKind kind, Color color, string? reference)
    {
        Kind = kind;
        Color = color;
        Reference = reference;
    }

    public SvgPaintKind Kind { get; }

    public Color Color { get; }

    /// <summary>The fragment id of a <c>url(#id)</c> reference, without the leading '#'.</summary>
    public string? Reference { get; }

    public static SvgPaint None => new(SvgPaintKind.None, default, null);

    public static SvgPaint FromColor(Color color) => new(SvgPaintKind.Color, color, null);

    public static bool TryParse(string input, out SvgPaint paint)
    {
        var value = input.Trim();

        if (value.Length == 0)
        {
            paint = default;
            return false;
        }

        if (value == "none")
        {
            paint = None;
            return true;
        }

        if (value == "currentColor")
        {
            paint = new SvgPaint(SvgPaintKind.CurrentColor, default, null);
            return true;
        }

        if (value.StartsWith("url(", StringComparison.Ordinal))
        {
            var close = value.IndexOf(')');
            if (close < 0)
            {
                paint = default;
                return false;
            }

            var target = value.Substring(4, close - 4).Trim();
            if (target.Length > 1 && target[0] == '#')
            {
                paint = new SvgPaint(SvgPaintKind.Reference, default, target.Substring(1));
                return true;
            }

            paint = default;
            return false;
        }

        if (Color.TryParse(value, out var color))
        {
            paint = FromColor(color);
            return true;
        }

        paint = default;
        return false;
    }
}
