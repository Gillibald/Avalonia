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

        var transform = ParseGradientTransform(chain, objectBoundingBox, bounds, context.Viewport);

        if (radial)
        {
            // Geometry attributes only inherit from gradients of the same kind;
            // defaults are percentages, resolved against the units mode.
            var cx = GetCoordinate(chain, "cx", "radialGradient", 50, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var cy = GetCoordinate(chain, "cy", "radialGradient", 50, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);
            var r = GetCoordinate(chain, "r", "radialGradient", 50, objectBoundingBox, SvgLengthAxis.Other, context.Viewport);
            var fx = GetChained(chain, "fx", "radialGradient") != null
                ? GetCoordinate(chain, "fx", "radialGradient", 50, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport)
                : cx;
            var fy = GetChained(chain, "fy", "radialGradient") != null
                ? GetCoordinate(chain, "fy", "radialGradient", 50, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport)
                : cy;

            var fr = GetCoordinate(chain, "fr", "radialGradient", 0, objectBoundingBox, SvgLengthAxis.Other, context.Viewport);

            // A negative radius is an error: the paint is invalid. A zero
            // end radius paints the last stop's color.
            if (r < 0 || fr < 0)
                return null;
            if (r == 0)
                return new ImmutableSolidColorBrush(stops[stops.Count - 1].Color, opacity);

            // SVG 2 renders a focal point outside the end circle as a true
            // two-point conical gradient, leaving the area outside the cone
            // unpainted. The brush only takes its conical path with a focal
            // radius, so give such a focal point a negligible one.
            var dx = fx - cx;
            var dy = fy - cy;
            if (fr == 0 && dx * dx + dy * dy > r * r)
                fr = r * 1e-6;

            var unit = objectBoundingBox ? RelativeUnit.Relative : RelativeUnit.Absolute;
            return new ImmutableRadialGradientBrush(
                stops,
                opacity,
                transform: transform,
                transformOrigin: null,
                spreadMethod: spreadMethod,
                center: new RelativePoint(cx, cy, unit),
                gradientOrigin: new RelativePoint(fx, fy, unit),
                radiusX: new RelativeScalar(r, unit),
                radiusY: new RelativeScalar(r, unit),
                focalRadius: new RelativeScalar(fr, unit));
        }
        else
        {
            var x1 = GetCoordinate(chain, "x1", "linearGradient", 0, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var y1 = GetCoordinate(chain, "y1", "linearGradient", 0, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);
            var x2 = GetCoordinate(chain, "x2", "linearGradient", 100, objectBoundingBox, SvgLengthAxis.Horizontal, context.Viewport);
            var y2 = GetCoordinate(chain, "y2", "linearGradient", 0, objectBoundingBox, SvgLengthAxis.Vertical, context.Viewport);

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

    private static string? GetChained(List<SvgElement> chain, string attribute, string? validOn = null)
    {
        foreach (var element in chain)
        {
            // Geometry attributes are only valid on their own gradient kind; a
            // chain may mix kinds, in which case foreign attributes are skipped.
            if (validOn != null && element.Name != validOn)
                continue;

            if (element.GetAttribute(attribute) is { } value)
                return value;
        }

        return null;
    }

    private static double GetCoordinate(
        List<SvgElement> chain, string attribute, string? validOn, double percentFallback,
        bool objectBoundingBox, SvgLengthAxis axis, Size viewport)
    {
        var value = GetChained(chain, attribute, validOn);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
        {
            // The spec defaults are percentages, sensitive to the units mode.
            length = new SvgLength(percentFallback, SvgLengthUnit.Percent);
        }

        if (objectBoundingBox)
        {
            // In bounding-box units values are fractions; percentages divide by 100.
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;
        }

        return length.Resolve(axis, viewport);
    }

    private static ImmutableTransform? ParseGradientTransform(
        List<SvgElement> chain, bool objectBoundingBox, Rect bounds, Size viewport)
    {
        var value = GetChained(chain, "gradientTransform");
        if (value == null
            || !SvgTransformParser.TryParse(value.AsSpan(), out var matrix)
            || matrix.IsIdentity)
        {
            return null;
        }

        // transform-origin conjugates the gradient transform in the gradient's
        // own coordinate space: the unit box in objectBoundingBox mode, user
        // space otherwise.
        SvgElement? originElement = null;
        foreach (var element in chain)
        {
            if (element.GetStyleOrAttribute("transform-origin") != null)
            {
                originElement = element;
                break;
            }
        }

        if (objectBoundingBox)
        {
            // SVG applies gradientTransform in the unit bounding-box space, while
            // Avalonia applies the brush transform in target space (origin at the
            // bounds top-left). Conjugating by the bounding-box scale converts
            // between the two exactly.
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            if (originElement != null)
            {
                matrix = SvgCompiler.ApplyTransformOrigin(
                    originElement, matrix, new Rect(0, 0, 1, 1), new Rect(0, 0, 1, 1));
            }

            matrix = Matrix.CreateScale(1 / bounds.Width, 1 / bounds.Height)
                     * matrix
                     * Matrix.CreateScale(bounds.Width, bounds.Height);
        }
        else if (originElement != null)
        {
            matrix = SvgCompiler.ApplyTransformOrigin(
                originElement, matrix, new Rect(viewport), bounds);

            // The brush conjugates its transform about the target bounds
            // position; compensate so the matrix acts in user space.
            if (bounds.X != 0 || bounds.Y != 0)
            {
                matrix = Matrix.CreateTranslation(bounds.X, bounds.Y)
                         * matrix
                         * Matrix.CreateTranslation(-bounds.X, -bounds.Y);
            }
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

            // Offsets accept only numbers and percentages; anything else is an
            // error and behaves as the initial 0.
            var offset = 0.0;
            if (child.GetStyleOrAttribute("offset") is { } offsetValue
                && SvgLength.TryParse(offsetValue.AsSpan(), out var offsetLength)
                && offsetLength.Unit is SvgLengthUnit.User or SvgLengthUnit.Percent)
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
                {
                    // currentColor takes the 'color' value inherited through the
                    // stop's own tree position (typically set on the gradient),
                    // falling back to the referencing element's color.
                    color = GetInheritedColor(child) ?? style.Color;
                }
                else if (colorValue == "inherit")
                {
                    // stop-color does not inherit by default; the keyword pulls
                    // the nearest ancestor declaration.
                    color = GetInheritedStopColor(child, style) ?? Colors.Black;
                }
                else if (SvgColor.TryParse(colorValue, out var parsed))
                {
                    color = parsed;
                }
            }

            if (child.GetStyleOrAttribute("stop-opacity") is { } opacityValue
                && SvgStyle.TryParseOpacity(opacityValue, out var stopOpacity))
            {
                color = new Color((byte)Math.Round(color.A * stopOpacity), color.R, color.G, color.B);
            }

            stops.Add(new ImmutableGradientStop(offset, color));
        }

        return stops;
    }

    private static Color? GetInheritedColor(SvgElement element)
    {
        for (var current = element; current != null; current = current.Parent)
        {
            if (current.GetStyleOrAttribute("color") is { } value
                && value != "inherit"
                && SvgColor.TryParse(value, out var color))
            {
                return color;
            }
        }

        return null;
    }

    private static Color? GetInheritedStopColor(SvgElement element, in SvgStyle style)
    {
        // stop-color does not inherit: the 'inherit' keyword takes the
        // immediate parent's computed value, which is the initial black unless
        // declared on the parent itself.
        if (element.Parent?.GetStyleOrAttribute("stop-color") is { } value && value != "inherit")
        {
            if (value == "currentColor")
                return GetInheritedColor(element.Parent) ?? style.Color;
            if (SvgColor.TryParse(value, out var color))
                return color;
        }

        return null;
    }
}
