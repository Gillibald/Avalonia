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

/// <summary>The fallback of a <c>url(#id)</c> paint reference.</summary>
internal enum SvgPaintFallback
{
    /// <summary>No fallback given: an unresolved reference paints nothing.</summary>
    Unspecified,
    /// <summary>An explicit <c>none</c> fallback.</summary>
    None,
    /// <summary>A fallback color.</summary>
    Color,
    /// <summary>A <c>currentColor</c> fallback.</summary>
    CurrentColor,
}

/// <summary>An SVG paint value (the <c>fill</c> and <c>stroke</c> properties).</summary>
internal readonly struct SvgPaint
{
    private SvgPaint(SvgPaintKind kind, Color color, string? reference,
        SvgPaintFallback fallback = SvgPaintFallback.Unspecified, Color fallbackColor = default)
    {
        Kind = kind;
        Color = color;
        Reference = reference;
        Fallback = fallback;
        FallbackColor = fallbackColor;
    }

    public SvgPaintKind Kind { get; }

    public Color Color { get; }

    /// <summary>The fragment id of a <c>url(#id)</c> reference, without the leading '#'.</summary>
    public string? Reference { get; }

    /// <summary>The fallback used when a <c>url(#id)</c> reference does not resolve.</summary>
    public SvgPaintFallback Fallback { get; }

    public Color FallbackColor { get; }

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

        // Context paints resolve against the element a marker or use site was
        // referenced from. Without that plumbing the spec-correct degradation
        // is 'none' — not the inherited paint.
        if (value is "context-fill" or "context-stroke")
        {
            paint = None;
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

            // SVG 2 allows the reference to be quoted; stray characters inside
            // make the whole reference invalid.
            var target = value.Substring(4, close - 4).Trim().Trim('\'', '"');
            if (target.Length <= 1 || target[0] != '#' || target.IndexOf(' ') >= 0)
            {
                paint = default;
                return false;
            }

            // An optional fallback follows the reference: 'none', 'currentColor'
            // or a color (optionally annotated with a legacy icc-color(), which
            // is dropped). Used when the reference does not resolve.
            var fallback = SvgPaintFallback.Unspecified;
            Color fallbackColor = default;
            var rest = value.Substring(close + 1).Trim();

            var icc = rest.IndexOf("icc-color(", StringComparison.Ordinal);
            if (icc >= 0)
                rest = rest.Substring(0, icc).Trim();

            if (rest.Length > 0)
            {
                if (rest == "none")
                {
                    fallback = SvgPaintFallback.None;
                }
                else if (rest == "currentColor")
                {
                    fallback = SvgPaintFallback.CurrentColor;
                }
                else if (SvgColor.TryParse(rest, out fallbackColor))
                {
                    fallback = SvgPaintFallback.Color;
                }
                else
                {
                    // An invalid fallback invalidates the whole paint value.
                    paint = default;
                    return false;
                }
            }

            paint = new SvgPaint(SvgPaintKind.Reference, default, target.Substring(1), fallback, fallbackColor);
            return true;
        }

        // A color may carry a legacy icc-color() annex (SVG 1.1); the sRGB
        // part is authoritative and the annex is dropped.
        var iccSuffix = value.IndexOf("icc-color(", StringComparison.Ordinal);
        if (iccSuffix > 0)
            value = value.Substring(0, iccSuffix).Trim();

        if (SvgColor.TryParse(value, out var color))
        {
            paint = FromColor(color);
            return true;
        }

        paint = default;
        return false;
    }
}
