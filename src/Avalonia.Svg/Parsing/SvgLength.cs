using System;

namespace Avalonia.Media.Svg.Parsing;

internal enum SvgLengthUnit
{
    /// <summary>User units; identical to <c>px</c> (1/96 inch, i.e. one DIP).</summary>
    User,
    Pixels,
    Points,
    Picas,
    Millimeters,
    Centimeters,
    /// <summary>CSS <c>Q</c>: quarter-millimeters.</summary>
    QuarterMillimeters,
    Inches,
    Em,
    Ex,
    /// <summary>CSS <c>rem</c>: relative to the root element's font size.</summary>
    Rem,
    /// <summary>CSS <c>ch</c>: the advance of "0"; resolved with the spec's 0.5em fallback.</summary>
    Ch,
    ViewportWidth,
    ViewportHeight,
    ViewportMin,
    ViewportMax,
    Percent,
}

/// <summary>The viewport axis a percentage length resolves against.</summary>
internal enum SvgLengthAxis
{
    Horizontal,
    Vertical,
    /// <summary>Resolves against the normalized viewport diagonal, per the SVG spec.</summary>
    Other,
}

/// <summary>An SVG length: a number with an optional unit.</summary>
/// <remarks>
/// SVG 2 turned the geometry attributes into CSS length-percentage values, so
/// the accepted units are the CSS set (matched ASCII case-insensitively, which
/// also covers the SVG 1.1 grammar). Units no major renderer supports in this
/// context (<c>cap</c>, <c>ic</c>, <c>lh</c>, <c>rlh</c>, <c>vi</c>, <c>vb</c>)
/// are rejected, which drops the attribute per the error-handling rules.
/// </remarks>
internal readonly struct SvgLength
{
    public SvgLength(double value, SvgLengthUnit unit)
    {
        Value = value;
        Unit = unit;
    }

    public double Value { get; }

    public SvgLengthUnit Unit { get; }

    public static bool TryParse(ReadOnlySpan<char> input, out SvgLength length)
    {
        var tokenizer = new SvgTokenizer(input);
        if (!tokenizer.TryReadNumber(out var value))
        {
            length = default;
            return false;
        }

        var unit = SvgLengthUnit.User;
        if (tokenizer.TryReadIdentifier(out var identifier))
        {
            if (Is(identifier, "px"))
                unit = SvgLengthUnit.Pixels;
            else if (Is(identifier, "pt"))
                unit = SvgLengthUnit.Points;
            else if (Is(identifier, "pc"))
                unit = SvgLengthUnit.Picas;
            else if (Is(identifier, "mm"))
                unit = SvgLengthUnit.Millimeters;
            else if (Is(identifier, "cm"))
                unit = SvgLengthUnit.Centimeters;
            else if (Is(identifier, "q"))
                unit = SvgLengthUnit.QuarterMillimeters;
            else if (Is(identifier, "in"))
                unit = SvgLengthUnit.Inches;
            else if (Is(identifier, "em"))
                unit = SvgLengthUnit.Em;
            else if (Is(identifier, "ex"))
                unit = SvgLengthUnit.Ex;
            else if (Is(identifier, "rem"))
                unit = SvgLengthUnit.Rem;
            else if (Is(identifier, "ch"))
                unit = SvgLengthUnit.Ch;
            else if (Is(identifier, "vw"))
                unit = SvgLengthUnit.ViewportWidth;
            else if (Is(identifier, "vh"))
                unit = SvgLengthUnit.ViewportHeight;
            else if (Is(identifier, "vmin"))
                unit = SvgLengthUnit.ViewportMin;
            else if (Is(identifier, "vmax"))
                unit = SvgLengthUnit.ViewportMax;
            else
            {
                length = default;
                return false;
            }
        }
        else if (tokenizer.TryConsume('%'))
        {
            unit = SvgLengthUnit.Percent;
        }

        if (!tokenizer.IsAtEnd)
        {
            length = default;
            return false;
        }

        length = new SvgLength(value, unit);
        return true;
    }

    private static bool Is(ReadOnlySpan<char> identifier, string unit) =>
        identifier.Equals(unit.AsSpan(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the length to DIPs (CSS pixels at 96 dpi).</summary>
    /// <param name="axis">The axis percentages resolve against.</param>
    /// <param name="viewport">The viewport size for percentages and viewport units.</param>
    /// <param name="fontSize">The font size for <c>em</c>/<c>ex</c>/<c>ch</c>.</param>
    /// <param name="rootFontSize">The root element's font size for <c>rem</c>.</param>
    public double Resolve(SvgLengthAxis axis, Size viewport, double fontSize = 16, double rootFontSize = 16)
    {
        switch (Unit)
        {
            case SvgLengthUnit.User:
            case SvgLengthUnit.Pixels:
                return Value;
            case SvgLengthUnit.Points:
                return Value * (96.0 / 72.0);
            case SvgLengthUnit.Picas:
                return Value * 16.0;
            case SvgLengthUnit.Millimeters:
                return Value * (96.0 / 25.4);
            case SvgLengthUnit.Centimeters:
                return Value * (96.0 / 2.54);
            case SvgLengthUnit.QuarterMillimeters:
                return Value * (96.0 / 25.4 / 4.0);
            case SvgLengthUnit.Inches:
                return Value * 96.0;
            case SvgLengthUnit.Em:
                return Value * fontSize;
            // The x-height and zero-advance measures use the spec-sanctioned
            // 0.5em fallback; glyph metrics are not consulted at parse level.
            case SvgLengthUnit.Ex:
            case SvgLengthUnit.Ch:
                return Value * fontSize * 0.5;
            case SvgLengthUnit.Rem:
                return Value * rootFontSize;
            case SvgLengthUnit.ViewportWidth:
                return Value / 100.0 * viewport.Width;
            case SvgLengthUnit.ViewportHeight:
                return Value / 100.0 * viewport.Height;
            case SvgLengthUnit.ViewportMin:
                return Value / 100.0 * Math.Min(viewport.Width, viewport.Height);
            case SvgLengthUnit.ViewportMax:
                return Value / 100.0 * Math.Max(viewport.Width, viewport.Height);
            case SvgLengthUnit.Percent:
                var reference = axis switch
                {
                    SvgLengthAxis.Horizontal => viewport.Width,
                    SvgLengthAxis.Vertical => viewport.Height,
                    _ => Math.Sqrt((viewport.Width * viewport.Width + viewport.Height * viewport.Height) / 2.0),
                };
                return Value / 100.0 * reference;
            default:
                return Value;
        }
    }
}
