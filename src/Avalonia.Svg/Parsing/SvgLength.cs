using System;

namespace Avalonia.Svg.Parsing;

internal enum SvgLengthUnit
{
    /// <summary>User units; identical to <c>px</c> (1/96 inch, i.e. one DIP).</summary>
    User,
    Pixels,
    Points,
    Picas,
    Millimeters,
    Centimeters,
    Inches,
    Em,
    Ex,
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
            if (identifier.SequenceEqual("px".AsSpan()))
                unit = SvgLengthUnit.Pixels;
            else if (identifier.SequenceEqual("pt".AsSpan()))
                unit = SvgLengthUnit.Points;
            else if (identifier.SequenceEqual("pc".AsSpan()))
                unit = SvgLengthUnit.Picas;
            else if (identifier.SequenceEqual("mm".AsSpan()))
                unit = SvgLengthUnit.Millimeters;
            else if (identifier.SequenceEqual("cm".AsSpan()))
                unit = SvgLengthUnit.Centimeters;
            else if (identifier.SequenceEqual("in".AsSpan()))
                unit = SvgLengthUnit.Inches;
            else if (identifier.SequenceEqual("em".AsSpan()))
                unit = SvgLengthUnit.Em;
            else if (identifier.SequenceEqual("ex".AsSpan()))
                unit = SvgLengthUnit.Ex;
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

    /// <summary>Resolves the length to DIPs (CSS pixels at 96 dpi).</summary>
    /// <param name="axis">The axis percentages resolve against.</param>
    /// <param name="viewport">The viewport size for percentages.</param>
    /// <param name="fontSize">The font size for <c>em</c>/<c>ex</c>.</param>
    public double Resolve(SvgLengthAxis axis, Size viewport, double fontSize = 16)
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
            case SvgLengthUnit.Inches:
                return Value * 96.0;
            case SvgLengthUnit.Em:
                return Value * fontSize;
            case SvgLengthUnit.Ex:
                return Value * fontSize * 0.5;
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
