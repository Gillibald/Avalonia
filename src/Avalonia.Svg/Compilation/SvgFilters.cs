using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Compiles SVG <c>&lt;filter&gt;</c> references into
/// <c>PushLayer(new LayerOptions {{ Bounds = region, Effect = … }})</c>.
/// Supported primitives: <c>feGaussianBlur</c>, <c>feOffset</c>,
/// <c>feColorMatrix</c>, <c>feDropShadow</c> and the classic
/// <c>feMerge(blur/offset of SourceAlpha, SourceGraphic)</c> drop-shadow
/// pattern. Linear <c>in</c>/<c>result</c> chains collapse into an
/// <see cref="ImmutableCompositeEffect"/>; anything else renders unfiltered
/// with a one-shot warning.
/// </summary>
internal static class SvgFilters
{
    private static bool s_warnedAboutUnsupported;

    /// <summary>
    /// The push states for an applied filter: a hard region clip (SVG filter
    /// regions clip; <c>LayerOptions.Bounds</c> only sizes the buffer) and the
    /// effect layer. <see cref="Hidden"/> signals that the filter produces no
    /// output and the element's content must not be compiled at all.
    /// </summary>
    internal struct SvgFilterScope : IDisposable
    {
        public DrawingContext.PushedState? ClipState;
        public DrawingContext.PushedState? LayerState;
        public bool Hidden;

        public void Dispose()
        {
            LayerState?.Dispose();
            ClipState?.Dispose();
        }
    }

    public static SvgFilterScope Push(
        DrawingContext context, SvgCompileContext compileContext, string id, Rect bounds)
    {
        var scope = default(SvgFilterScope);

        if (!TryResolve(compileContext, id, bounds, out var region, out var effect))
            return scope;

        if (effect == null)
        {
            // A valid filter with no output (empty, or an empty region) hides
            // the element, per spec — prune at compile time.
            scope.Hidden = true;
            return scope;
        }

        scope.ClipState = context.PushClip(region);
        scope.LayerState = context.PushLayer(new LayerOptions { Bounds = region, Effect = effect });
        return scope;
    }

    /// <summary>
    /// Resolves a filter reference. Returns false when the filter is missing or
    /// uses unsupported primitives/graphs (the element renders unfiltered);
    /// returns true with a null <paramref name="effect"/> when the filter is
    /// valid but produces nothing (the element is hidden).
    /// </summary>
    public static bool TryResolve(
        SvgCompileContext compileContext, string id, Rect bounds,
        out Rect region, out IImmutableEffect? effect)
    {
        region = default;
        effect = null;

        if (compileContext.Document.GetElementById(id) is not { Name: "filter" } filter)
            return false;

        // Filter region; filterUnits default to objectBoundingBox with the
        // spec's -10% / 120% defaults.
        var boxUnits = filter.GetAttribute("filterUnits") != "userSpaceOnUse";
        if (boxUnits && (bounds.Width <= 0 || bounds.Height <= 0))
            return true; // hides the element

        var x = GetCoordinate(filter, "x", -0.1, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var y = GetCoordinate(filter, "y", -0.1, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);
        var width = GetCoordinate(filter, "width", 1.2, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var height = GetCoordinate(filter, "height", 1.2, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);

        region = boxUnits
            ? new Rect(
                bounds.X + x * bounds.Width,
                bounds.Y + y * bounds.Height,
                width * bounds.Width,
                height * bounds.Height)
            : new Rect(x, y, width, height);

        if (region.Width <= 0 || region.Height <= 0)
            return true; // hides the element

        return TryBuildEffectChain(filter, out effect);
    }

    private static bool TryBuildEffectChain(SvgElement filter, out IImmutableEffect? effect)
    {
        effect = null;

        var stages = new List<IImmutableEffect>();
        string? lastResult = null;
        var firstInput = "SourceGraphic";
        var any = false;

        foreach (var primitive in filter.Children)
        {
            if (primitive.Name is "title" or "desc" or "metadata")
                continue;

            any = true;
            var input = primitive.GetAttribute("in");

            if (primitive.Name == "feMerge")
            {
                // Only the classic drop-shadow pattern is supported:
                // blur (of SourceAlpha) [+ offset], merged under SourceGraphic.
                if (TryCollapseMergeToDropShadow(primitive, stages, lastResult, firstInput, out var dropShadow))
                {
                    stages.Clear();
                    stages.Add(dropShadow);
                    lastResult = primitive.GetAttribute("result");
                    continue;
                }

                return Unsupported("feMerge (beyond the SourceAlpha drop-shadow pattern)");
            }

            // Linear chains only: each primitive consumes the previous result.
            if (stages.Count == 0)
            {
                if (input is not (null or "SourceGraphic" or "SourceAlpha"))
                    return Unsupported($"a first input of '{input}'");
                firstInput = input ?? "SourceGraphic";
            }
            else if (input != null && input != lastResult)
            {
                return Unsupported($"a non-linear input of '{input}'");
            }

            switch (primitive.Name)
            {
                case "feGaussianBlur":
                {
                    var sigma = GetNumber(primitive, "stdDeviation", 0);
                    if (sigma > 0)
                        stages.Add(new ImmutableBlurEffect(SigmaToBlurRadius(sigma)));
                    break;
                }
                case "feOffset":
                {
                    stages.Add(new ImmutableOffsetEffect(
                        GetNumber(primitive, "dx", 0),
                        GetNumber(primitive, "dy", 0)));
                    break;
                }
                case "feColorMatrix":
                {
                    if (TryCreateColorMatrix(primitive, out var colorMatrix))
                        stages.Add(colorMatrix);
                    else
                        return Unsupported("an invalid feColorMatrix");
                    break;
                }
                case "feDropShadow":
                {
                    var color = Colors.Black;
                    if (primitive.GetStyleOrAttribute("flood-color") is { } floodColor
                        && floodColor != "currentColor"
                        && SvgColor.TryParse(floodColor, out var parsed))
                    {
                        color = parsed;
                    }

                    var opacity = 1.0;
                    if (primitive.GetStyleOrAttribute("flood-opacity") is { } floodOpacity
                        && SvgStyle.TryParseOpacity(floodOpacity, out var parsedOpacity))
                    {
                        opacity = parsedOpacity;
                    }

                    var sigma = GetNumber(primitive, "stdDeviation", 2);
                    stages.Add(new ImmutableDropShadowEffect(
                        GetNumber(primitive, "dx", 2),
                        GetNumber(primitive, "dy", 2),
                        sigma > 0 ? SigmaToBlurRadius(sigma) : 0,
                        color,
                        opacity));
                    break;
                }
                default:
                    return Unsupported($"the '{primitive.Name}' primitive");
            }

            lastResult = primitive.GetAttribute("result");
        }

        if (!any)
            return true; // an empty filter hides the element

        if (stages.Count == 0)
        {
            // Only identity stages (e.g. a zero blur): render unchanged through
            // the layer.
            effect = new ImmutableOffsetEffect(0, 0);
            return true;
        }

        effect = stages.Count == 1 ? stages[0] : new ImmutableCompositeEffect(stages.ToArray());
        return true;
    }

    private static bool TryCollapseMergeToDropShadow(
        SvgElement merge, List<IImmutableEffect> stages, string? lastResult, string firstInput,
        out IImmutableEffect dropShadow)
    {
        dropShadow = null!;

        // Expect exactly two merge nodes: the current chain result below the
        // source graphic.
        if (merge.Children.Count != 2
            || merge.Children[0].Name != "feMergeNode"
            || merge.Children[1].Name != "feMergeNode")
        {
            return false;
        }

        var first = merge.Children[0].GetAttribute("in");
        var second = merge.Children[1].GetAttribute("in");
        if (second != "SourceGraphic" || (first != null && first != lastResult))
            return false;

        // The chain must be blur [+ offset] over SourceAlpha.
        if (firstInput != "SourceAlpha" || stages.Count is < 1 or > 2)
            return false;

        double blurRadius;
        double offsetX = 0, offsetY = 0;

        if (stages[0] is IBlurEffect blur)
        {
            blurRadius = blur.Radius;
            if (stages.Count == 2)
            {
                if (stages[1] is IOffsetEffect offset)
                {
                    offsetX = offset.OffsetX;
                    offsetY = offset.OffsetY;
                }
                else
                {
                    return false;
                }
            }
        }
        else
        {
            return false;
        }

        dropShadow = new ImmutableDropShadowEffect(offsetX, offsetY, blurRadius, Colors.Black, 1);
        return true;
    }

    private static bool TryCreateColorMatrix(SvgElement primitive, out ImmutableColorMatrixEffect effect)
    {
        effect = null!;
        var type = primitive.GetAttribute("type") ?? "matrix";
        var values = primitive.GetAttribute("values");

        switch (type)
        {
            case "matrix":
            {
                if (values == null)
                    return false;
                var matrix = new double[ImmutableColorMatrixEffect.MatrixLength];
                var tokenizer = new SvgTokenizer(values.AsSpan());
                for (var i = 0; i < matrix.Length; i++)
                {
                    if (!tokenizer.TryReadNumber(out matrix[i]))
                        return false;
                }

                effect = new ImmutableColorMatrixEffect(matrix);
                return true;
            }
            case "saturate":
            {
                var s = 1.0;
                if (values != null && !TryParseNumber(values, out s))
                    return false;
                effect = new ImmutableColorMatrixEffect(new[]
                {
                    0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s, 0, 0,
                    0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s, 0, 0,
                    0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s, 0, 0,
                    0, 0, 0, 1, 0,
                });
                return true;
            }
            case "hueRotate":
            {
                var degrees = 0.0;
                if (values != null && !TryParseNumber(values, out degrees))
                    return false;
                var cos = Math.Cos(Matrix.ToRadians(degrees));
                var sin = Math.Sin(Matrix.ToRadians(degrees));
                effect = new ImmutableColorMatrixEffect(new[]
                {
                    0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928, 0, 0,
                    0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283, 0, 0,
                    0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072, 0, 0,
                    0, 0, 0, 1, 0,
                });
                return true;
            }
            case "luminanceToAlpha":
            {
                effect = new ImmutableColorMatrixEffect(new[]
                {
                    0d, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0.2125, 0.7154, 0.0721, 0, 0,
                });
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// SVG <c>stdDeviation</c> is the Gaussian sigma; Avalonia blur radii lower
    /// to Skia via <c>sigma = 0.288675 · radius + 0.5</c>, so invert that.
    /// </summary>
    private static double SigmaToBlurRadius(double sigma) =>
        Math.Max(0, (sigma - 0.5) / 0.288675);

    private static bool Unsupported(string what)
    {
        if (!s_warnedAboutUnsupported)
        {
            s_warnedAboutUnsupported = true;
            Logger.TryGet(LogEventLevel.Warning, "SVG")?.Log(
                typeof(SvgFilters),
                "An SVG filter uses " + what + ", which is not supported; the element renders unfiltered.");
        }

        return false;
    }

    private static double GetNumber(SvgElement element, string name, double fallback)
    {
        var value = element.GetAttribute(name);
        return value != null && TryParseNumber(value, out var parsed) ? parsed : fallback;
    }

    private static bool TryParseNumber(string value, out double result)
    {
        // The first number of a possibly two-value list (e.g. stdDeviation
        // "2 3"); Avalonia effects are symmetric, so the first value wins.
        var tokenizer = new SvgTokenizer(value.AsSpan());
        return tokenizer.TryReadNumber(out result);
    }

    private static double GetCoordinate(
        SvgElement element, string attribute, double fallback,
        bool boxUnits, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetAttribute(attribute);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
            return fallback;

        if (boxUnits)
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;

        return length.Resolve(axis, viewport);
    }
}
