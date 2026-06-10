using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Animation;

/// <summary>The <c>animateTransform</c> transform kind.</summary>
internal enum SvgAnimationTransformType
{
    Translate,
    Scale,
    Rotate,
    SkewX,
    SkewY,
}

/// <summary>
/// One parsed SMIL animation (<c>animate</c>, <c>set</c> or
/// <c>animateTransform</c>) with deterministic value sampling:
/// <see cref="Sample"/> maps a document time to the attribute's current
/// animated value, or null while the animation is inactive.
/// </summary>
internal sealed class SvgAnimationEntry
{
    public SvgAnimationEntry(SvgElement target, string attributeName, string[] values)
    {
        Target = target;
        AttributeName = attributeName;
        Values = values;
    }

    public SvgElement Target { get; }

    /// <summary>The animated attribute; <c>transform</c> for <c>animateTransform</c>.</summary>
    public string AttributeName { get; }

    /// <summary>The value list (a from/to pair, a <c>values</c> list, or a single <c>set</c>/to value).</summary>
    public string[] Values { get; }

    public TimeSpan Begin { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Number of repeat iterations; <see cref="double.PositiveInfinity"/> for indefinite.</summary>
    public double RepeatCount { get; init; } = 1;

    /// <summary>True for <c>fill="freeze"</c>: holds the final value after the active duration.</summary>
    public bool Freeze { get; init; }

    /// <summary>True for <c>calcMode="discrete"</c> (and for non-interpolable values).</summary>
    public bool Discrete { get; init; }

    /// <summary>True for <c>&lt;set&gt;</c>: a single value applied over the active duration.</summary>
    public bool IsSet { get; init; }

    public SvgAnimationTransformType? TransformType { get; init; }

    /// <summary>
    /// The mutable brush this entry drives when it runs on the paint channel
    /// (compositor-bound recording); null on the structural channel.
    /// </summary>
    public SolidColorBrush? PaintBrush { get; set; }

    /// <summary>The brush color to restore when the animation deactivates.</summary>
    public Color PaintBaseColor { get; set; }

    /// <summary>
    /// True when every value parses as a color — eligible for the paint channel
    /// when it targets a directly rendered shape's fill or stroke.
    /// </summary>
    public bool IsColor
    {
        get
        {
            foreach (var value in Values)
            {
                if (!SvgColor.TryParse(value, out _))
                    return false;
            }

            return Values.Length > 0;
        }
    }

    /// <summary>
    /// Samples the animated value at <paramref name="time"/> (document time).
    /// Returns null while inactive: before <see cref="Begin"/>, or after the
    /// active duration without <c>fill="freeze"</c>.
    /// </summary>
    public string? Sample(TimeSpan time)
    {
        var local = time - Begin;
        if (local < TimeSpan.Zero || Duration <= TimeSpan.Zero || Values.Length == 0)
            return null;

        var cycles = local.Ticks / (double)Duration.Ticks;
        if (cycles >= RepeatCount)
        {
            if (!Freeze)
                return null;

            // Freeze at the value reached at the end of the active duration —
            // mid-cycle for fractional repeat counts.
            var endProgress = RepeatCount % 1.0;
            return ValueAt(endProgress == 0 ? 1 : endProgress);
        }

        return ValueAt(cycles % 1.0);
    }

    private string ValueAt(double progress)
    {
        if (IsSet || Values.Length == 1)
            return Finalize(Values[0]);

        if (Discrete)
        {
            // N values split the duration into N equal steps.
            var step = (int)(progress * Values.Length);
            return Finalize(Values[Math.Min(step, Values.Length - 1)]);
        }

        if (progress >= 1)
            return Finalize(Values[Values.Length - 1]);

        var scaled = progress * (Values.Length - 1);
        var index = (int)scaled;
        var fraction = scaled - index;

        return Interpolate(Values[index], Values[index + 1], fraction);
    }

    /// <summary>
    /// Transform values are bare component lists ("100 50"); whenever one is
    /// emitted without interpolation it still needs wrapping into the
    /// transform-list function the compiler parses.
    /// </summary>
    private string Finalize(string value) =>
        TransformType is { } type ? FormatTransform(type, ParseNumberList(value)) : value;

    private string Interpolate(string from, string to, double t)
    {
        if (TransformType is { } transformType)
            return InterpolateTransform(transformType, from, to, t);

        if (SvgColor.TryParse(from, out var fromColor) && SvgColor.TryParse(to, out var toColor))
            return LerpColor(fromColor, toColor, t).ToString();

        if (TryParseNumber(from, out var fromNumber) && TryParseNumber(to, out var toNumber))
            return (fromNumber + (toNumber - fromNumber) * t).ToString("G", CultureInfo.InvariantCulture);

        // Non-interpolable values fall back to discrete halves, per SMIL error handling.
        return t < 0.5 ? from : to;
    }

    private static Color LerpColor(Color from, Color to, double t) => Color.FromArgb(
        LerpByte(from.A, to.A, t),
        LerpByte(from.R, to.R, t),
        LerpByte(from.G, to.G, t),
        LerpByte(from.B, to.B, t));

    private static byte LerpByte(byte from, byte to, double t) =>
        (byte)Math.Round(from + (to - from) * t);

    private static string InterpolateTransform(SvgAnimationTransformType type, string from, string to, double t)
    {
        var fromParts = ParseNumberList(from);
        var toParts = ParseNumberList(to);
        var count = Math.Max(fromParts.Length, toParts.Length);
        if (count == 0)
            return FormatTransform(type, Array.Empty<double>());

        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            // Missing components animate from/to their defaults (0; an omitted
            // scale y matches x, handled in FormatTransform).
            var a = i < fromParts.Length ? fromParts[i] : 0;
            var b = i < toParts.Length ? toParts[i] : 0;
            result[i] = a + (b - a) * t;
        }

        return FormatTransform(type, result);
    }

    /// <summary>Formats interpolated components back into a transform-list function.</summary>
    internal static string FormatTransform(SvgAnimationTransformType type, double[] components)
    {
        string N(int index, double fallback = 0) => (index < components.Length ? components[index] : fallback)
            .ToString("G", CultureInfo.InvariantCulture);

        return type switch
        {
            SvgAnimationTransformType.Translate => $"translate({N(0)} {N(1)})",
            SvgAnimationTransformType.Scale => components.Length >= 2
                ? $"scale({N(0)} {N(1)})"
                : $"scale({N(0)})",
            SvgAnimationTransformType.Rotate => components.Length >= 3
                ? $"rotate({N(0)} {N(1)} {N(2)})"
                : $"rotate({N(0)})",
            SvgAnimationTransformType.SkewX => $"skewX({N(0)})",
            _ => $"skewY({N(0)})",
        };
    }

    internal static double[] ParseNumberList(string value)
    {
        var parts = value.Split(s_listSeparators, StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseNumber(parts[i], out result[i]))
                return Array.Empty<double>();
        }

        return result;
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static readonly char[] s_listSeparators = { ' ', '\t', '\r', '\n', ',' };
}
