using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

namespace Avalonia.Svg.Animation;

/// <summary>
/// Parses a document's SMIL animations and applies sampled values, splitting
/// work across the two channels of the recording architecture:
/// <list type="bullet">
/// <item><description><b>Paint channel</b> — color animations on a shape's own
/// <c>fill</c>/<c>stroke</c> mutate <see cref="SolidColorBrush"/> instances
/// captured by a compositor-bound recording; the compositor's change tracking
/// propagates them without re-recording.</description></item>
/// <item><description><b>Structural channel</b> — everything else writes
/// animated attribute overrides onto the target elements; the host re-compiles
/// the root recording, replaying cached shared sub-recordings.</description></item>
/// </list>
/// Sampling is purely time-driven and deterministic: <see cref="Apply"/> with
/// the same timestamp always produces the same state.
/// </summary>
internal sealed class SvgAnimator
{
    private static readonly string[] s_shapeNames =
        { "rect", "circle", "ellipse", "line", "polyline", "polygon", "path" };

    private readonly List<SvgAnimationEntry> _entries;

    private SvgAnimator(List<SvgAnimationEntry> entries)
    {
        _entries = entries;

        foreach (var entry in _entries)
        {
            if (!IsPaintEligible(entry))
            {
                HasStructural = true;
                break;
            }
        }
    }

    /// <summary>The parsed animations, in document order.</summary>
    public IReadOnlyList<SvgAnimationEntry> Entries => _entries;

    /// <summary>
    /// True when any animation needs the structural channel (re-compilation);
    /// false when everything runs on mutable paint resources.
    /// </summary>
    public bool HasStructural { get; }

    /// <summary>The (element, attribute) pairs eligible for mutable paint brushes.</summary>
    public IReadOnlyCollection<(SvgElement Element, string Attribute)> GetPaintTargets()
    {
        var targets = new HashSet<(SvgElement, string)>();
        foreach (var entry in _entries)
        {
            if (IsPaintEligible(entry))
                targets.Add((entry.Target, entry.AttributeName));
        }

        return targets;
    }

    /// <summary>
    /// Binds the mutable brushes a compile registered for the paint targets.
    /// Entries without a brush stay on the structural channel.
    /// </summary>
    public void BindPaintBrushes(IReadOnlyDictionary<(SvgElement Element, string Attribute), SolidColorBrush>? brushes)
    {
        foreach (var entry in _entries)
        {
            if (brushes != null && brushes.TryGetValue((entry.Target, entry.AttributeName), out var brush))
            {
                entry.PaintBrush = brush;
                entry.PaintBaseColor = brush.Color;
            }
            else
            {
                entry.PaintBrush = null;
            }
        }
    }

    /// <summary>
    /// Color animations on a shape's own fill/stroke can run on the paint
    /// channel. Inherited paints (animating a group's fill) and non-color
    /// attributes resolve at arbitrary descendants and re-compile instead.
    /// </summary>
    private static bool IsPaintEligible(SvgAnimationEntry entry) =>
        entry.AttributeName is "fill" or "stroke"
        && Array.IndexOf(s_shapeNames, entry.Target.Name) >= 0
        && entry.IsColor;

    /// <summary>
    /// Applies all animations at <paramref name="time"/>. Paint-bound entries
    /// mutate their brushes; the rest write attribute overrides. Returns true
    /// when any structural override changed, i.e. the host must re-compile.
    /// </summary>
    public bool Apply(TimeSpan time)
    {
        var structuralChanged = false;

        foreach (var entry in _entries)
        {
            var value = entry.Sample(time);

            if (entry.PaintBrush is { } brush)
            {
                var color = value != null && Color.TryParse(value, out var parsed)
                    ? parsed
                    : entry.PaintBaseColor;
                if (brush.Color != color)
                    brush.Color = color;
                continue;
            }

            var target = entry.Target;
            var current = target.GetAnimatedValue(entry.AttributeName);
            if (!string.Equals(current, value, StringComparison.Ordinal))
            {
                target.SetAnimatedValue(entry.AttributeName, value);
                structuralChanged = true;
            }
        }

        return structuralChanged;
    }

    /// <summary>
    /// Scans the document for supported SMIL animations; returns null when it
    /// has none. Animations targeting content inside shared-compiled containers
    /// (symbol, marker, pattern, mask) are skipped — those recordings are
    /// cached and replayed at multiple sites, so per-element animation there is
    /// out of scope.
    /// </summary>
    public static SvgAnimator? TryCreate(SvgDocument document)
    {
        var entries = new List<SvgAnimationEntry>();
        Scan(document, document.Root, entries);
        return entries.Count > 0 ? new SvgAnimator(entries) : null;
    }

    private static void Scan(SvgDocument document, SvgElement element, List<SvgAnimationEntry> entries)
    {
        foreach (var child in element.Children)
        {
            if (child.Name is "animate" or "set" or "animateTransform")
            {
                if (TryParseEntry(document, child, out var entry))
                    entries.Add(entry);
                continue;
            }

            Scan(document, child, entries);
        }
    }

    private static bool TryParseEntry(SvgDocument document, SvgElement element, out SvgAnimationEntry entry)
    {
        entry = null!;

        // The target is the parent unless href points elsewhere.
        var target = element.Parent;
        if (element.Href is { Length: > 1 } href && href[0] == '#')
            target = document.GetElementById(href.Substring(1));
        if (target == null || IsInsideSharedContainer(target))
            return false;

        SvgAnimationTransformType? transformType = null;
        string? attributeName;
        if (element.Name == "animateTransform")
        {
            attributeName = "transform";
            transformType = element.GetAttribute("type") switch
            {
                null or "translate" => SvgAnimationTransformType.Translate,
                "scale" => SvgAnimationTransformType.Scale,
                "rotate" => SvgAnimationTransformType.Rotate,
                "skewX" => SvgAnimationTransformType.SkewX,
                "skewY" => SvgAnimationTransformType.SkewY,
                _ => null,
            };
            if (transformType == null)
                return false;
        }
        else
        {
            attributeName = element.GetAttribute("attributeName");
            if (string.IsNullOrEmpty(attributeName))
                return false;
        }

        if (!TryParseClockValue(element.GetAttribute("dur"), out var duration) || duration <= TimeSpan.Zero)
            return false;

        var begin = TimeSpan.Zero;
        if (element.GetAttribute("begin") is { } beginValue)
        {
            // Offset begins only; event- and syncbase-driven begins are out of scope.
            if (!TryParseClockValue(beginValue, out begin))
                return false;
        }

        var isSet = element.Name == "set";
        string[] values;
        if (!isSet && element.GetAttribute("values") is { Length: > 0 } valuesList)
        {
            values = SplitValues(valuesList);
        }
        else
        {
            var from = element.GetAttribute("from");
            var to = element.GetAttribute("to");
            if (to == null)
                return false;

            if (isSet)
            {
                values = new[] { to };
            }
            else if (from != null)
            {
                values = new[] { from, to };
            }
            else
            {
                // A to-animation interpolates from the underlying value when one
                // is declared on the element; otherwise it degrades to a set.
                var baseValue = target.GetStyleOrAttribute(attributeName!);
                values = baseValue != null ? new[] { baseValue, to } : new[] { to };
            }
        }

        if (values.Length == 0)
            return false;

        var repeatCount = 1.0;
        if (element.GetAttribute("repeatCount") is { } repeat)
        {
            if (repeat == "indefinite")
                repeatCount = double.PositiveInfinity;
            else if (double.TryParse(repeat, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                     && parsed > 0)
                repeatCount = parsed;
        }

        entry = new SvgAnimationEntry(target, attributeName!, values)
        {
            Begin = begin,
            Duration = duration,
            RepeatCount = repeatCount,
            Freeze = element.GetAttribute("fill") == "freeze",
            Discrete = element.GetAttribute("calcMode") == "discrete",
            IsSet = isSet,
            TransformType = transformType,
        };
        return true;
    }

    private static bool IsInsideSharedContainer(SvgElement element)
    {
        for (var current = element.Parent; current != null; current = current.Parent)
        {
            if (current.Name is "symbol" or "marker" or "pattern" or "mask"
                or "linearGradient" or "radialGradient" or "clipPath" or "filter")
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitValues(string values)
    {
        var parts = values.Split(';');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Parses a SMIL clock value in its common forms: a number with an optional
    /// <c>h</c>/<c>min</c>/<c>s</c>/<c>ms</c> metric (seconds by default).
    /// </summary>
    internal static bool TryParseClockValue(string? value, out TimeSpan result)
    {
        result = default;
        if (value == null)
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == "indefinite")
            return false;

        var multiplier = 1.0;
        if (trimmed.EndsWith("ms", StringComparison.Ordinal))
        {
            multiplier = 0.001;
            trimmed = trimmed.Substring(0, trimmed.Length - 2);
        }
        else if (trimmed.EndsWith("min", StringComparison.Ordinal))
        {
            multiplier = 60;
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }
        else if (trimmed.EndsWith("s", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }
        else if (trimmed.EndsWith("h", StringComparison.Ordinal))
        {
            multiplier = 3600;
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return false;
        }

        result = TimeSpan.FromTicks((long)(seconds * multiplier * TimeSpan.TicksPerSecond));
        return true;
    }
}
