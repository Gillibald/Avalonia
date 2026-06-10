using System;
using System.Globalization;

namespace Avalonia.Svg.Parsing;

/// <summary>
/// A forward-only tokenizer over SVG attribute micro-syntax (path data, transform
/// lists, point lists, lengths). Implements the SVG/CSS number grammar, including
/// scientific notation and the implicit separators SVG allows ("1.5.5" is two
/// numbers, "1-2" is two numbers, arc flags may be juxtaposed).
/// </summary>
internal ref struct SvgTokenizer
{
    private ReadOnlySpan<char> _remaining;

    public SvgTokenizer(ReadOnlySpan<char> input) => _remaining = input;

    /// <summary>The number of characters not yet consumed (diagnostics only).</summary>
    public int Remaining => _remaining.Length;

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    public void SkipWhitespace()
    {
        var i = 0;
        while (i < _remaining.Length && IsWhitespace(_remaining[i]))
            i++;
        _remaining = _remaining.Slice(i);
    }

    /// <summary>Skips optional whitespace, at most one comma, then optional whitespace.</summary>
    public void SkipCommaWhitespace()
    {
        SkipWhitespace();
        if (_remaining.Length > 0 && _remaining[0] == ',')
        {
            _remaining = _remaining.Slice(1);
            SkipWhitespace();
        }
    }

    /// <summary>True when only whitespace (and no further tokens) remains.</summary>
    public bool IsAtEnd
    {
        get
        {
            foreach (var c in _remaining)
            {
                if (!IsWhitespace(c))
                    return false;
            }

            return true;
        }
    }

    public bool TryPeek(out char c)
    {
        SkipWhitespace();
        if (_remaining.Length == 0)
        {
            c = default;
            return false;
        }

        c = _remaining[0];
        return true;
    }

    /// <summary>Consumes <paramref name="c"/> (after optional leading whitespace).</summary>
    public bool TryConsume(char c)
    {
        SkipWhitespace();
        if (_remaining.Length == 0 || _remaining[0] != c)
            return false;
        _remaining = _remaining.Slice(1);
        return true;
    }

    /// <summary>
    /// Reads a number per the SVG grammar: optional sign, integer and/or fractional
    /// digits, optional exponent. Leading comma-whitespace is consumed. The number
    /// ends at the first character that cannot extend it, which makes "1.5.5",
    /// "1-2" and "1e2.5" tokenize as two numbers each.
    /// </summary>
    public bool TryReadNumber(out double value)
    {
        SkipCommaWhitespace();

        var s = _remaining;
        var i = 0;

        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            i++;

        var intDigits = 0;
        while (i < s.Length && IsDigit(s[i]))
        {
            i++;
            intDigits++;
        }

        var fracDigits = 0;
        if (i < s.Length && s[i] == '.')
        {
            var j = i + 1;
            while (j < s.Length && IsDigit(s[j]))
            {
                j++;
                fracDigits++;
            }

            // A bare "." is not a number; only consume it with at least one
            // fractional digit (or when integer digits already exist: "1." is valid).
            if (fracDigits > 0 || intDigits > 0)
                i = j;
        }

        if (intDigits == 0 && fracDigits == 0)
        {
            value = default;
            return false;
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            var j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                j++;
            var expDigits = 0;
            while (j < s.Length && IsDigit(s[j]))
            {
                j++;
                expDigits++;
            }

            // "1em" must not consume the 'e' as an exponent.
            if (expDigits > 0)
                i = j;
        }

        value = ParseDouble(s.Slice(0, i));
        _remaining = s.Slice(i);
        return true;
    }

    /// <summary>
    /// Reads an SVG arc flag: a single '0' or '1' character after optional
    /// comma-whitespace. Flags may be juxtaposed ("011" is two flags and the
    /// start of a number), so exactly one character is consumed.
    /// </summary>
    public bool TryReadFlag(out bool flag)
    {
        SkipCommaWhitespace();
        if (_remaining.Length > 0 && (_remaining[0] == '0' || _remaining[0] == '1'))
        {
            flag = _remaining[0] == '1';
            _remaining = _remaining.Slice(1);
            return true;
        }

        flag = default;
        return false;
    }

    /// <summary>Reads a run of ASCII letters (transform function names, keywords).</summary>
    public bool TryReadIdentifier(out ReadOnlySpan<char> identifier)
    {
        SkipWhitespace();
        var i = 0;
        while (i < _remaining.Length && _remaining[i] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-')
            i++;

        if (i == 0)
        {
            identifier = default;
            return false;
        }

        identifier = _remaining.Slice(0, i);
        _remaining = _remaining.Slice(i);
        return true;
    }

    /// <summary>Reads a single ASCII letter (path command).</summary>
    public bool TryReadCommand(out char command)
    {
        SkipWhitespace();
        if (_remaining.Length > 0 && _remaining[0] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
        {
            command = _remaining[0];
            _remaining = _remaining.Slice(1);
            return true;
        }

        command = default;
        return false;
    }

    internal static double ParseDouble(ReadOnlySpan<char> s)
    {
#if NETSTANDARD2_0
        return double.Parse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
#else
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
#endif
    }
}
