using System;
using System.Globalization;
using Avalonia.Media;

namespace Avalonia.Svg.Parsing;

/// <summary>
/// Parses CSS color values as SVG content uses them. Differs from
/// <see cref="Color.TryParse(string, out Color)"/> where the CSS syntax does:
/// 4- and 8-digit hex carry alpha <b>last</b> (<c>#RGBA</c>/<c>#RRGGBBAA</c>,
/// Avalonia reads alpha first), <c>rgb()</c>/<c>rgba()</c> accept floats and
/// percentages and clamp out-of-range components, and <c>hsl()</c>/<c>hsla()</c>
/// wrap the hue. Everything else (named colors, 3/6-digit hex) delegates to
/// Avalonia's parser.
/// </summary>
internal static class SvgColor
{
    public static bool TryParse(string value, out Color color)
    {
        color = default;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '#')
            return TryParseHex(trimmed, out color);

        if (StartsWithFunction(trimmed, "rgb") || StartsWithFunction(trimmed, "rgba"))
            return TryParseRgb(trimmed, out color);

        if (StartsWithFunction(trimmed, "hsl") || StartsWithFunction(trimmed, "hsla"))
            return TryParseHsl(trimmed, out color);

        if (Color.TryParse(trimmed, out color))
            return true;

        // CSS defines both spellings of the gray color names; Avalonia's
        // table only carries the 'a' variants.
        if (trimmed.IndexOf("grey", StringComparison.OrdinalIgnoreCase) >= 0)
            return Color.TryParse(trimmed.Replace("grey", "gray").Replace("GREY", "GRAY").Replace("Grey", "Gray"), out color);

        return false;
    }

    private static bool StartsWithFunction(string value, string name) =>
        value.Length > name.Length
        && value.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && value[name.Length] == '(';

    private static bool TryParseHex(string value, out Color color)
    {
        color = default;
        switch (value.Length)
        {
            // CSS hex-with-alpha puts alpha last; reorder for Avalonia.
            case 5: // #RGBA
            {
                if (!TryHexDigit(value[1], out var r) || !TryHexDigit(value[2], out var g)
                    || !TryHexDigit(value[3], out var b) || !TryHexDigit(value[4], out var a))
                    return false;
                color = Color.FromArgb((byte)(a * 17), (byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                return true;
            }
            case 9: // #RRGGBBAA
            {
                if (!TryHexByte(value, 1, out var r) || !TryHexByte(value, 3, out var g)
                    || !TryHexByte(value, 5, out var b) || !TryHexByte(value, 7, out var a))
                    return false;
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            default:
                // #RGB and #RRGGBB have no alpha ambiguity.
                return Color.TryParse(value, out color);
        }
    }

    private static bool TryHexByte(string value, int index, out byte result)
    {
        result = 0;
        if (!TryHexDigit(value[index], out var high) || !TryHexDigit(value[index + 1], out var low))
            return false;
        result = (byte)(high * 16 + low);
        return true;
    }

    private static bool TryHexDigit(char c, out int digit)
    {
        digit = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return digit >= 0;
    }

    private static bool TryParseRgb(string value, out Color color)
    {
        color = default;
        if (!TrySplitArguments(value, out var parts) || parts.Length is < 3 or > 4)
            return false;

        if (!TryParseChannel(parts[0], out var r)
            || !TryParseChannel(parts[1], out var g)
            || !TryParseChannel(parts[2], out var b))
        {
            return false;
        }

        // CSS forbids mixing numbers and percentages between color channels
        // (the alpha component is independent).
        var percentCount = 0;
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].EndsWith("%", StringComparison.Ordinal))
                percentCount++;
        }

        if (percentCount is not (0 or 3))
            return false;

        var a = (byte)255;
        if (parts.Length == 4)
        {
            if (!TryParseAlpha(parts[3], out var alpha))
                return false;
            a = alpha;
        }

        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    /// <summary>A color channel: a number (float allowed) or a percentage, clamped to 0–255.</summary>
    private static bool TryParseChannel(string part, out byte channel)
    {
        channel = 0;
        double scaled;
        if (part.EndsWith("%", StringComparison.Ordinal))
        {
            if (!TryParseNumber(part.Substring(0, part.Length - 1), out var percent))
                return false;
            scaled = percent / 100.0 * 255.0;
        }
        else
        {
            if (!TryParseNumber(part, out scaled))
                return false;
        }

        channel = (byte)Math.Round(Math.Min(255, Math.Max(0, scaled)));
        return true;
    }

    /// <summary>Alpha: a number or percentage, clamped to 0–1.</summary>
    private static bool TryParseAlpha(string part, out byte alpha)
    {
        alpha = 0;
        double scaled;
        if (part.EndsWith("%", StringComparison.Ordinal))
        {
            if (!TryParseNumber(part.Substring(0, part.Length - 1), out var percent))
                return false;
            scaled = percent / 100.0;
        }
        else
        {
            if (!TryParseNumber(part, out scaled))
                return false;
        }

        alpha = (byte)Math.Round(Math.Min(1, Math.Max(0, scaled)) * 255.0);
        return true;
    }

    private static bool TryParseHsl(string value, out Color color)
    {
        color = default;
        if (!TrySplitArguments(value, out var parts) || parts.Length is < 3 or > 4)
            return false;

        // The hue is an angle: any number, wrapped into [0, 360).
        var huePart = parts[0];
        if (huePart.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
            huePart = huePart.Substring(0, huePart.Length - 3);
        if (!TryParseNumber(huePart, out var hue))
            return false;
        hue %= 360.0;
        if (hue < 0)
            hue += 360.0;

        if (!TryParsePercentage(parts[1], out var saturation) || !TryParsePercentage(parts[2], out var lightness))
            return false;

        var a = (byte)255;
        if (parts.Length == 4)
        {
            if (!TryParseAlpha(parts[3], out var alpha))
                return false;
            a = alpha;
        }

        var hsl = HslColor.FromHsl(hue, saturation, lightness).ToRgb();
        color = Color.FromArgb(a, hsl.R, hsl.G, hsl.B);
        return true;
    }

    private static bool TryParsePercentage(string part, out double result)
    {
        result = 0;
        if (!part.EndsWith("%", StringComparison.Ordinal)
            || !TryParseNumber(part.Substring(0, part.Length - 1), out var percent))
            return false;

        result = Math.Min(1, Math.Max(0, percent / 100.0));
        return true;
    }

    private static bool TrySplitArguments(string value, out string[] parts)
    {
        parts = Array.Empty<string>();
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close != value.Length - 1 || close <= open)
            return false;

        // CSS allows comma-separated and whitespace-separated arguments, the
        // latter with an optional '/' before the alpha component.
        var arguments = value.Substring(open + 1, close - open - 1)
            .Replace('/', ' ')
            .Split(s_argumentSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length == 0)
            return false;

        parts = arguments;
        return true;
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static readonly char[] s_argumentSeparators = { ',', ' ', '\t', '\r', '\n' };
}
