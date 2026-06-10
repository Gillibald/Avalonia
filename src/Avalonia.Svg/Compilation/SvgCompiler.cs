using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Walks a parsed <see cref="SvgDocument"/> and emits draw calls into a
/// <see cref="DrawingContext"/> — typically one that is building a
/// <c>DrawingRecording</c>.
/// </summary>
internal static class SvgCompiler
{
    /// <summary>
    /// Compiles the document into <paramref name="context"/>, mapping the
    /// document's <c>viewBox</c> onto a viewport of <paramref name="viewportSize"/>.
    /// </summary>
    public static void CompileDocument(SvgDocument document, DrawingContext context, Size viewportSize)
    {
        var root = document.Root;
        var viewBox = document.TryGetViewBox();

        DrawingContext.PushedState? viewBoxState = null;
        var contentViewport = viewportSize;

        if (viewBox is { } vb)
        {
            var preserveAspectRatio = SvgPreserveAspectRatio.Default;
            if (root.GetAttribute("preserveAspectRatio") is { } par)
                SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);

            var matrix = preserveAspectRatio.ComputeTransform(vb, viewportSize);
            if (!matrix.IsIdentity)
                viewBoxState = context.PushTransform(matrix);

            // Percentages inside the document resolve against the viewport the
            // svg element establishes — the viewBox coordinate system when present.
            contentViewport = new Size(vb.Width, vb.Height);
        }

        using (viewBoxState)
        {
            var compileContext = new SvgCompileContext(document, contentViewport);
            var style = SvgStyle.CreateDefault(contentViewport);
            style.Apply(root);

            foreach (var child in root.Children)
                CompileElement(child, context, compileContext, style);
        }
    }

    internal static void CompileElement(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle parentStyle)
    {
        switch (element.Name)
        {
            // Definition and metadata containers never render directly.
            case "defs":
            case "symbol":
            case "marker":
            case "clipPath":
            case "mask":
            case "pattern":
            case "linearGradient":
            case "radialGradient":
            case "filter":
            case "style":
            case "title":
            case "desc":
            case "metadata":
            case "script":
                return;
        }

        if (element.GetStyleOrAttribute("display") == "none")
            return;

        var style = parentStyle;
        style.Apply(element);

        DrawingContext.PushedState? transformState = null;
        if (element.GetAttribute("transform") is { } transform
            && SvgTransformParser.TryParse(transform.AsSpan(), out var matrix)
            && !matrix.IsIdentity)
        {
            transformState = context.PushTransform(matrix);
        }

        using (transformState)
        {
            DrawingContext.PushedState? clipState = null;
            if (element.GetStyleOrAttribute("clip-path") is { } clipReference)
            {
                // objectBoundingBox clip units require the element's bounding box;
                // it is available for the basic shapes (geometry-backed shapes and
                // groups support userSpaceOnUse clips only for now).
                var clipBounds = TryGetCheapShapeBounds(element, style.Viewport) ?? default;
                if (SvgClipPaths.TryBuild(compileContext, clipReference, clipBounds) is { } clip)
                    clipState = context.PushGeometryClip(clip);
            }

            using (clipState)
            {
                switch (element.Name)
                {
                    case "g":
                    case "a":
                    case "svg":
                    {
                        foreach (var child in element.Children)
                            CompileElement(child, context, compileContext, style);
                        break;
                    }
                    case "use":
                        CompileUse(element, context, compileContext, style);
                        break;
                    case "rect":
                        CompileRect(element, context, compileContext, style);
                        break;
                    case "circle":
                        CompileCircle(element, context, compileContext, style);
                        break;
                    case "ellipse":
                        CompileEllipse(element, context, compileContext, style);
                        break;
                    case "line":
                        CompileLine(element, context, compileContext, style);
                        break;
                    case "polyline":
                        CompilePoly(element, context, compileContext, style, close: false);
                        break;
                    case "polygon":
                        CompilePoly(element, context, compileContext, style, close: true);
                        break;
                    case "path":
                        CompilePath(element, context, compileContext, style);
                        break;
                }
            }
        }
    }

    private static void CompileUse(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var href = element.Href;
        if (href is not { Length: > 1 } || href[0] != '#')
            return;

        var target = compileContext.Document.GetElementById(href.Substring(1));
        if (target == null)
            return;

        switch (target.Name)
        {
            // Only graphics and container elements are valid use targets.
            case "clipPath":
            case "linearGradient":
            case "radialGradient":
            case "pattern":
            case "filter":
            case "marker":
            case "style":
            case "script":
            case "title":
            case "desc":
            case "metadata":
            case "defs":
                return;
        }

        if (!compileContext.EnterUse(target))
            return;

        try
        {
            var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style.Viewport);
            var y = GetLength(element, "y", SvgLengthAxis.Vertical, style.Viewport);

            DrawingContext.PushedState? opacityState = null;
            if (element.GetStyleOrAttribute("opacity") is { } opacityValue
                && double.TryParse(opacityValue, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var opacity)
                && opacity < 1)
            {
                opacityState = context.PushOpacity(Math.Max(0, opacity));
            }

            using (opacityState)
            {
                var recording = compileContext.GetSharedRecording(target);

                if (target.Name is "symbol" or "svg")
                {
                    var width = GetUseViewportLength(element, target, "width", SvgLengthAxis.Horizontal, style.Viewport);
                    var height = GetUseViewportLength(element, target, "height", SvgLengthAxis.Vertical, style.Viewport);
                    if (width <= 0 || height <= 0)
                        return;

                    var contentMatrix = Matrix.Identity;
                    if (target.GetAttribute("viewBox") is { } viewBoxValue
                        && SvgViewBox.TryParse(viewBoxValue.AsSpan(), out var viewBox))
                    {
                        var preserveAspectRatio = SvgPreserveAspectRatio.Default;
                        if (target.GetAttribute("preserveAspectRatio") is { } par)
                            SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);
                        contentMatrix = preserveAspectRatio.ComputeTransform(viewBox, new Size(width, height));
                    }

                    // A symbol establishes a viewport: position it, clip to it
                    // (overflow defaults to hidden), and replay the shared content
                    // recording under the viewBox mapping.
                    using (context.PushTransform(Matrix.CreateTranslation(x, y)))
                    using (context.PushClip(new Rect(0, 0, width, height)))
                    {
                        context.DrawRecording(recording, contentMatrix, DrawingRecordingOwnership.Shared);
                    }
                }
                else
                {
                    context.DrawRecording(recording, Matrix.CreateTranslation(x, y), DrawingRecordingOwnership.Shared);
                }
            }
        }
        finally
        {
            compileContext.ExitUse(target);
        }
    }

    private static double GetUseViewportLength(
        SvgElement use, SvgElement target, string name, SvgLengthAxis axis, Size viewport)
    {
        // width/height resolve from the use site, then the symbol, then 100%.
        var value = use.GetAttribute(name) ?? target.GetAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, viewport);
        return axis == SvgLengthAxis.Horizontal ? viewport.Width : viewport.Height;
    }

    private static IImmutableBrush? ResolvePaint(
        in SvgPaint paint, in SvgStyle style, SvgCompileContext compileContext, Rect bounds)
    {
        if (paint.Kind == SvgPaintKind.Reference)
            return paint.Reference is { } id ? SvgPaintServers.Resolve(compileContext, id, style, bounds) : null;
        return style.ResolveBrush(paint);
    }

    private static void CompileRect(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style.Viewport);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, style.Viewport);
        var width = GetLength(element, "width", SvgLengthAxis.Horizontal, style.Viewport);
        var height = GetLength(element, "height", SvgLengthAxis.Vertical, style.Viewport);

        if (width <= 0 || height <= 0)
            return;

        // rx/ry default to 'auto' (SVG 2): an auto value takes the other's value;
        // both auto means square corners. Values clamp to half the rect side.
        var rx = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style.Viewport);
        var ry = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style.Viewport);
        var radiusX = rx ?? ry ?? 0;
        var radiusY = ry ?? rx ?? 0;
        radiusX = Math.Min(radiusX, width / 2);
        radiusY = Math.Min(radiusY, height / 2);

        var rect = new Rect(x, y, width, height);
        var brush = ResolvePaint(style.Fill, style, compileContext, rect);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, rect));
        if (brush == null && pen == null)
            return;

        context.DrawRectangle(brush, pen, rect, radiusX, radiusY);
    }

    private static double? GetCornerRadius(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value == null || value == "auto")
            return null;
        if (SvgLength.TryParse(value.AsSpan(), out var length)
            && length.Resolve(axis, viewport) is var resolved and >= 0)
        {
            return resolved;
        }

        // A negative or unparseable radius behaves as 'auto'.
        return null;
    }

    private static void CompileCircle(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, style.Viewport);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, style.Viewport);
        var r = GetLength(element, "r", SvgLengthAxis.Other, style.Viewport);

        if (r <= 0)
            return;

        var rect = new Rect(cx - r, cy - r, 2 * r, 2 * r);
        var brush = ResolvePaint(style.Fill, style, compileContext, rect);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, rect));
        if (brush == null && pen == null)
            return;

        context.DrawEllipse(brush, pen, rect);
    }

    private static void CompileEllipse(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, style.Viewport);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, style.Viewport);

        // SVG 2: an 'auto' radius takes the other radius' value.
        var rxValue = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style.Viewport);
        var ryValue = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style.Viewport);
        var rx = rxValue ?? ryValue ?? 0;
        var ry = ryValue ?? rxValue ?? 0;

        if (rx <= 0 || ry <= 0)
            return;

        var rect = new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry);
        var brush = ResolvePaint(style.Fill, style, compileContext, rect);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, rect));
        if (brush == null && pen == null)
            return;

        context.DrawEllipse(brush, pen, rect);
    }

    private static void CompileLine(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var x1 = GetLength(element, "x1", SvgLengthAxis.Horizontal, style.Viewport);
        var y1 = GetLength(element, "y1", SvgLengthAxis.Vertical, style.Viewport);
        var x2 = GetLength(element, "x2", SvgLengthAxis.Horizontal, style.Viewport);
        var y2 = GetLength(element, "y2", SvgLengthAxis.Vertical, style.Viewport);

        var bounds = new Rect(new Point(x1, y1), new Point(x2, y2));
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, bounds));
        if (pen == null)
            return;

        context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
    }

    private static void CompilePoly(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style, bool close)
    {
        var points = element.GetAttribute("points");
        if (string.IsNullOrEmpty(points))
            return;

        if (style.Fill.Kind == SvgPaintKind.None && style.Stroke.Kind == SvgPaintKind.None)
            return;

        var geometry = new StreamGeometry();
        bool any;
        using (var geometryContext = geometry.Open())
        {
            geometryContext.SetFillRule(style.FillRule);
            any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
        }

        if (!any)
            return;

        var bounds = geometry.Bounds;
        var brush = ResolvePaint(style.Fill, style, compileContext, bounds);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, bounds));
        if (brush == null && pen == null)
            return;

        context.DrawGeometry(brush, pen, geometry);
    }

    private static void CompilePath(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var data = element.GetStyleOrAttribute("d");
        if (string.IsNullOrEmpty(data))
            return;

        if (style.Fill.Kind == SvgPaintKind.None && style.Stroke.Kind == SvgPaintKind.None)
            return;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.SetFillRule(style.FillRule);
            try
            {
                SvgPathParser.Parse(data.AsSpan(), geometryContext);
            }
            catch (FormatException)
            {
                // Per the SVG error-handling rules the path's valid prefix still
                // renders; the parser emitted it before throwing.
            }
        }

        var bounds = geometry.Bounds;
        var brush = ResolvePaint(style.Fill, style, compileContext, bounds);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, bounds));
        if (brush == null && pen == null)
            return;

        context.DrawGeometry(brush, pen, geometry);
    }

    /// <summary>
    /// Computes the bounding box for shapes whose box is cheap to derive from
    /// attributes (no geometry construction). Used for objectBoundingBox clip
    /// resolution; returns null for geometry-backed shapes and containers.
    /// </summary>
    private static Rect? TryGetCheapShapeBounds(SvgElement element, Size viewport)
    {
        switch (element.Name)
        {
            case "rect":
            {
                var width = GetLength(element, "width", SvgLengthAxis.Horizontal, viewport);
                var height = GetLength(element, "height", SvgLengthAxis.Vertical, viewport);
                if (width <= 0 || height <= 0)
                    return null;
                return new Rect(
                    GetLength(element, "x", SvgLengthAxis.Horizontal, viewport),
                    GetLength(element, "y", SvgLengthAxis.Vertical, viewport),
                    width, height);
            }
            case "circle":
            {
                var r = GetLength(element, "r", SvgLengthAxis.Other, viewport);
                if (r <= 0)
                    return null;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, viewport) - r,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, viewport) - r,
                    2 * r, 2 * r);
            }
            case "ellipse":
            {
                var rx = GetLength(element, "rx", SvgLengthAxis.Horizontal, viewport);
                var ry = GetLength(element, "ry", SvgLengthAxis.Vertical, viewport);
                if (rx <= 0 || ry <= 0)
                    return null;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, viewport) - rx,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, viewport) - ry,
                    2 * rx, 2 * ry);
            }
            default:
                return null;
        }
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, viewport);
        return 0;
    }
}
