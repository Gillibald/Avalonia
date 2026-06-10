using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Resolves <c>url(#id)</c> paint-server references — linear and radial
/// gradients — to immutable brushes. Patterns follow in a later phase.
/// </summary>
internal static class SvgPaintServers
{
    private const int MaxReferenceChain = 8;

    public static IImmutableBrush? Resolve(SvgCompileContext context, string id, in SvgStyle style, Rect bounds)
        => Resolve(context, id, style, bounds, 1);

    public static IImmutableBrush? Resolve(
        SvgCompileContext context, string id, in SvgStyle style, Rect bounds, double opacity)
    {
        var element = context.Document.GetElementById(id);
        return element?.Name switch
        {
            "linearGradient" => ResolveGradient(context, element, style, bounds, opacity, radial: false),
            "radialGradient" => ResolveGradient(context, element, style, bounds, opacity, radial: true),
            "pattern" => SvgPatterns.Resolve(context, element, bounds, opacity),
            _ => null,
        };
    }

    private static IImmutableBrush? ResolveGradient(
        SvgCompileContext context, SvgElement element, in SvgStyle style, Rect bounds, double opacity, bool radial)
    {
        // href chains: unset attributes and missing stops inherit from the
        // referenced gradient (linear and radial may reference each other).
        var chain = BuildReferenceChain(context, element);

        var stops = ParseStops(chain, style);
        if (stops == null || stops.Count == 0)
            return null;
        if (stops.Count == 1)
            return new ImmutableSolidColorBrush(stops[0].Color, opacity);

        var objectBoundingBox = GetChained(chain, "gradientUnits") != "userSpaceOnUse";

        // Per spec an objectBoundingBox gradient on a zero-area box disables
        // rendering of the element part using it.
        if (objectBoundingBox && (bounds.Width <= 0 || bounds.Height <= 0))
            return null;

        var spreadMethod = GetChained(chain, "spreadMethod") switch
        {
            "reflect" => GradientSpreadMethod.Reflect,
            "repeat" => GradientSpreadMethod.Repeat,
            _ => GradientSpreadMethod.Pad,
        };

        var transform = ParseGradientTransform(chain, objectBoundingBox, bounds);

        if (radial)
        {
            var cx = GetCoordinate(chain, "cx", 0.5, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var cy = GetCoordinate(chain, "cy", 0.5, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);
            var r = GetCoordinate(chain, "r", 0.5, objectBoundingBox, SvgLengthAxis.Other, context.Viewport);
            var fx = GetCoordinate(chain, "fx", cx, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var fy = GetCoordinate(chain, "fy", cy, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);

            if (r <= 0)
                return new ImmutableSolidColorBrush(stops[stops.Count - 1].Color, opacity);

            var unit = objectBoundingBox ? RelativeUnit.Relative : RelativeUnit.Absolute;
            return new ImmutableRadialGradientBrush(
                stops,
                opacity,
                transform: transform,
                spreadMethod: spreadMethod,
                center: new RelativePoint(cx, cy, unit),
                gradientOrigin: new RelativePoint(fx, fy, unit),
                radiusX: new RelativeScalar(r, unit),
                radiusY: new RelativeScalar(r, unit));
        }
        else
        {
            var x1 = GetCoordinate(chain, "x1", 0, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var y1 = GetCoordinate(chain, "y1", 0, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);
            var x2 = GetCoordinate(chain, "x2", objectBoundingBox ? 1 : 0, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var y2 = GetCoordinate(chain, "y2", 0, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);

            if (!objectBoundingBox && GetChained(chain, "x2") == null)
            {
                // The userSpaceOnUse default for x2 is 100% of the viewport.
                x2 = context.Viewport.Width;
            }

            var unit = objectBoundingBox ? RelativeUnit.Relative : RelativeUnit.Absolute;
            return new ImmutableLinearGradientBrush(
                stops,
                opacity,
                transform: transform,
                spreadMethod: spreadMethod,
                startPoint: new RelativePoint(x1, y1, unit),
                endPoint: new RelativePoint(x2, y2, unit));
        }
    }

    private static List<SvgElement> BuildReferenceChain(SvgCompileContext context, SvgElement element)
    {
        var chain = new List<SvgElement> { element };
        var current = element;

        for (var depth = 0; depth < MaxReferenceChain; depth++)
        {
            var href = current.Href;
            if (href is not { Length: > 1 } || href[0] != '#')
                break;

            var target = context.Document.GetElementById(href.Substring(1));
            if (target == null
                || target.Name is not ("linearGradient" or "radialGradient")
                || chain.Contains(target))
            {
                break;
            }

            chain.Add(target);
            current = target;
        }

        return chain;
    }

    private static string? GetChained(List<SvgElement> chain, string attribute)
    {
        foreach (var element in chain)
        {
            if (element.GetAttribute(attribute) is { } value)
                return value;
        }

        return null;
    }

    private static double GetCoordinate(
        List<SvgElement> chain, string attribute, double fallback,
        bool objectBoundingBox, SvgLengthAxis axis, Size viewport)
    {
        var value = GetChained(chain, attribute);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
            return fallback;

        if (objectBoundingBox)
        {
            // In bounding-box units values are fractions; percentages divide by 100.
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;
        }

        return length.Resolve(axis, viewport);
    }

    private static ImmutableTransform? ParseGradientTransform(List<SvgElement> chain, bool objectBoundingBox, Rect bounds)
    {
        var value = GetChained(chain, "gradientTransform");
        if (value == null
            || !SvgTransformParser.TryParse(value.AsSpan(), out var matrix)
            || matrix.IsIdentity)
        {
            return null;
        }

        if (objectBoundingBox)
        {
            // SVG applies gradientTransform in the unit bounding-box space, while
            // Avalonia applies the brush transform in target space (origin at the
            // bounds top-left). Conjugating by the bounding-box scale converts
            // between the two exactly.
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            matrix = Matrix.CreateScale(1 / bounds.Width, 1 / bounds.Height)
                     * matrix
                     * Matrix.CreateScale(bounds.Width, bounds.Height);
        }

        return new ImmutableTransform(matrix);
    }

    private static List<ImmutableGradientStop>? ParseStops(List<SvgElement> chain, in SvgStyle style)
    {
        // Stops come from the first gradient in the chain that defines any.
        SvgElement? source = null;
        foreach (var element in chain)
        {
            foreach (var child in element.Children)
            {
                if (child.Name == "stop")
                {
                    source = element;
                    break;
                }
            }

            if (source != null)
                break;
        }

        if (source == null)
            return null;

        var stops = new List<ImmutableGradientStop>();
        double previousOffset = 0;

        foreach (var child in source.Children)
        {
            if (child.Name != "stop")
                continue;

            var offset = 0.0;
            if (child.GetStyleOrAttribute("offset") is { } offsetValue
                && SvgLength.TryParse(offsetValue.AsSpan(), out var offsetLength))
            {
                offset = offsetLength.Unit == SvgLengthUnit.Percent
                    ? offsetLength.Value / 100.0
                    : offsetLength.Value;
            }

            // Offsets clamp to [0, 1] and must be non-decreasing.
            offset = Math.Max(previousOffset, Math.Min(1, Math.Max(0, offset)));
            previousOffset = offset;

            var color = Colors.Black;
            if (child.GetStyleOrAttribute("stop-color") is { } colorValue)
            {
                if (colorValue == "currentColor")
                    color = style.Color;
                else if (Color.TryParse(colorValue, out var parsed))
                    color = parsed;
            }

            if (child.GetStyleOrAttribute("stop-opacity") is { } opacityValue
                && double.TryParse(opacityValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var stopOpacity))
            {
                stopOpacity = Math.Min(1, Math.Max(0, stopOpacity));
                color = new Color((byte)Math.Round(color.A * stopOpacity), color.R, color.G, color.B);
            }

            stops.Add(new ImmutableGradientStop(offset, color));
        }

        return stops;
    }
}
