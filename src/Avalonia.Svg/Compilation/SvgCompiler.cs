using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
            // Group compositing: element opacity (group semantics, distinct from
            // per-primitive opacity), mix-blend-mode and isolation all fold into
            // a single recorded layer. Skipped while measuring fill boxes.
            DrawingContext.PushedState? layerState = null;
            if (!compileContext.Measuring)
            {
                var opacity = 1.0;
                if (element.GetStyleOrAttribute("opacity") is { } opacityValue
                    && SvgStyle.TryParseOpacity(opacityValue, out var parsedOpacity))
                {
                    opacity = parsedOpacity;
                }

                if (opacity <= 0)
                    return;

                var blendMode = ParseBlendMode(element.GetStyleOrAttribute("mix-blend-mode"));
                var isolate = element.GetStyleOrAttribute("isolation") == "isolate";

                if (opacity < 1 || blendMode != BitmapBlendingMode.Unspecified || isolate)
                {
                    layerState = context.PushLayer(new LayerOptions
                    {
                        Opacity = opacity < 1 ? opacity : null,
                        BlendMode = blendMode,
                        Isolate = isolate,
                    });
                }
            }

            using (layerState)
            {
                DrawingContext.PushedState? clipState = null;
                if (!compileContext.Measuring
                    && element.GetStyleOrAttribute("clip-path") is { } clipReference)
                {
                    var clipBounds = GetFillBounds(element, compileContext, style);
                    if (SvgClipPaths.TryBuild(compileContext, clipReference, clipBounds) is { } clip)
                        clipState = context.PushGeometryClip(clip);
                }

                using (clipState)
                {
                    DrawingContext.PushedState? maskState = null;
                    if (!compileContext.Measuring
                        && element.GetStyleOrAttribute("mask") is { } maskValue
                        && SvgClipPaths.TryParseUrlReference(maskValue, out var maskId))
                    {
                        var maskBounds = GetFillBounds(element, compileContext, style);
                        maskState = SvgMasks.TryPush(context, compileContext, maskId, maskBounds);
                    }

                    using (maskState)
                    {
                        var filterScope = default(SvgFilters.SvgFilterScope);
                        if (!compileContext.Measuring
                            && element.GetStyleOrAttribute("filter") is { } filterValue
                            && SvgClipPaths.TryParseUrlReference(filterValue, out var filterId))
                        {
                            var filterBounds = GetFillBounds(element, compileContext, style);
                            filterScope = SvgFilters.Push(context, compileContext, filterId, filterBounds);
                        }

                        using (filterScope)
                        {
                            if (!filterScope.Hidden)
                                CompileElementContent(element, context, compileContext, style);
                        }
                    }
                }
            }
        }
    }

    private static void CompileElementContent(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
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
            case "text":
                SvgText.Compile(element, context, compileContext, style);
                break;
        }
    }

    private static BitmapBlendingMode ParseBlendMode(string? value) => value switch
    {
        "multiply" => BitmapBlendingMode.Multiply,
        "screen" => BitmapBlendingMode.Screen,
        "overlay" => BitmapBlendingMode.Overlay,
        "darken" => BitmapBlendingMode.Darken,
        "lighten" => BitmapBlendingMode.Lighten,
        "color-dodge" => BitmapBlendingMode.ColorDodge,
        "color-burn" => BitmapBlendingMode.ColorBurn,
        "hard-light" => BitmapBlendingMode.HardLight,
        "soft-light" => BitmapBlendingMode.SoftLight,
        "difference" => BitmapBlendingMode.Difference,
        "exclusion" => BitmapBlendingMode.Exclusion,
        "hue" => BitmapBlendingMode.Hue,
        "saturation" => BitmapBlendingMode.Saturation,
        "color" => BitmapBlendingMode.Color,
        "luminosity" => BitmapBlendingMode.Luminosity,
        _ => BitmapBlendingMode.Unspecified,
    };

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
            case "mask":
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
        in SvgPaint paint, in SvgStyle style, SvgCompileContext compileContext, Rect bounds, double opacity)
    {
        if (paint.Kind == SvgPaintKind.Reference)
            return paint.Reference is { } id ? SvgPaintServers.Resolve(compileContext, id, style, bounds, opacity) : null;
        return style.ResolveBrush(paint, opacity);
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
        var brush = ResolvePaint(style.Fill, style, compileContext, rect, style.FillOpacity);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, rect, style.StrokeOpacity));
        if (brush == null && pen == null)
            return;

        if (style.StrokeBeforeFill && brush != null && pen != null)
        {
            context.DrawRectangle(null, pen, rect, radiusX, radiusY);
            context.DrawRectangle(brush, null, rect, radiusX, radiusY);
        }
        else
        {
            context.DrawRectangle(brush, pen, rect, radiusX, radiusY);
        }
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

        DrawEllipseShape(context, compileContext, style, new Rect(cx - r, cy - r, 2 * r, 2 * r));
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

        DrawEllipseShape(context, compileContext, style, new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry));
    }

    private static void DrawEllipseShape(
        DrawingContext context, SvgCompileContext compileContext, in SvgStyle style, Rect rect)
    {
        var brush = ResolvePaint(style.Fill, style, compileContext, rect, style.FillOpacity);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, rect, style.StrokeOpacity));
        if (brush == null && pen == null)
            return;

        if (style.StrokeBeforeFill && brush != null && pen != null)
        {
            context.DrawEllipse(null, pen, rect);
            context.DrawEllipse(brush, null, rect);
        }
        else
        {
            context.DrawEllipse(brush, pen, rect);
        }
    }

    private static void CompileLine(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var x1 = GetLength(element, "x1", SvgLengthAxis.Horizontal, style.Viewport);
        var y1 = GetLength(element, "y1", SvgLengthAxis.Vertical, style.Viewport);
        var x2 = GetLength(element, "x2", SvgLengthAxis.Horizontal, style.Viewport);
        var y2 = GetLength(element, "y2", SvgLengthAxis.Vertical, style.Viewport);

        var bounds = new Rect(new Point(x1, y1), new Point(x2, y2));
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, bounds, style.StrokeOpacity));
        if (pen != null)
            context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

        if (!compileContext.Measuring && HasMarkers(style))
        {
            var direction = NormalizeDirection(new Point(x1, y1), new Point(x2, y2));
            var vertices = new[]
            {
                new SvgPathVertex(new Point(x1, y1), null, direction),
                new SvgPathVertex(new Point(x2, y2), direction, null),
            };
            SvgMarkers.Emit(context, compileContext, style, vertices);
        }
    }

    private static Vector? NormalizeDirection(Point from, Point to)
    {
        var v = new Vector(to.X - from.X, to.Y - from.Y);
        var length = v.Length;
        return length > 1e-9 ? v / length : null;
    }

    private static bool HasMarkers(in SvgStyle style) =>
        style.MarkerStart != null || style.MarkerMid != null || style.MarkerEnd != null;

    private static void CompilePoly(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style, bool close)
    {
        var points = element.GetAttribute("points");
        if (string.IsNullOrEmpty(points))
            return;

        if (style.Fill.Kind != SvgPaintKind.None || style.Stroke.Kind != SvgPaintKind.None)
        {
            var geometry = new StreamGeometry();
            bool any;
            using (var geometryContext = geometry.Open())
            {
                geometryContext.SetFillRule(style.FillRule);
                any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
            }

            if (any)
                DrawGeometryShape(context, compileContext, style, geometry);
        }

        if (!compileContext.Measuring && HasMarkers(style))
        {
            var list = SvgPointsParser.ParseList(points.AsSpan());
            if (list.Count > 0)
                SvgMarkers.Emit(context, compileContext, style, BuildPolyVertices(list, close));
        }
    }

    private static IReadOnlyList<SvgPathVertex> BuildPolyVertices(List<Point> points, bool close)
    {
        var vertices = new List<SvgPathVertex>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var incoming = i > 0
                ? NormalizeDirection(points[i - 1], points[i])
                : close && points.Count > 1 ? NormalizeDirection(points[points.Count - 1], points[0]) : null;
            var outgoing = i + 1 < points.Count
                ? NormalizeDirection(points[i], points[i + 1])
                : close && points.Count > 1 ? NormalizeDirection(points[points.Count - 1], points[0]) : null;
            vertices.Add(new SvgPathVertex(points[i], incoming, outgoing));
        }

        return vertices;
    }

    private static void CompilePath(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var data = element.GetStyleOrAttribute("d");
        if (string.IsNullOrEmpty(data))
            return;

        if (style.Fill.Kind != SvgPaintKind.None || style.Stroke.Kind != SvgPaintKind.None)
        {
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
                    // Per the SVG error-handling rules the path's valid prefix
                    // still renders; the parser emitted it before throwing.
                }
            }

            DrawGeometryShape(context, compileContext, style, geometry);
        }

        if (!compileContext.Measuring && HasMarkers(style))
        {
            var sampler = SvgPathSampler.Parse(data.AsSpan());
            SvgMarkers.Emit(context, compileContext, style, sampler.Vertices);
        }
    }

    private static void DrawGeometryShape(
        DrawingContext context, SvgCompileContext compileContext, in SvgStyle style, StreamGeometry geometry)
    {
        var bounds = geometry.Bounds;
        var brush = ResolvePaint(style.Fill, style, compileContext, bounds, style.FillOpacity);
        var pen = style.ResolvePen(ResolvePaint(style.Stroke, style, compileContext, bounds, style.StrokeOpacity));
        if (brush == null && pen == null)
            return;

        if (style.StrokeBeforeFill && brush != null && pen != null)
        {
            context.DrawGeometry(null, pen, geometry);
            context.DrawGeometry(brush, null, geometry);
        }
        else
        {
            context.DrawGeometry(brush, pen, geometry);
        }
    }

    /// <summary>
    /// Computes the element's fill box (the SVG objectBoundingBox: geometry
    /// without stroke, markers or decorations). Cheap attribute math where
    /// possible; containers and geometry-backed shapes measure through a
    /// throwaway recording.
    /// </summary>
    internal static Rect GetFillBounds(SvgElement element, SvgCompileContext compileContext, in SvgStyle style)
    {
        switch (element.Name)
        {
            case "rect":
            {
                var width = GetLength(element, "width", SvgLengthAxis.Horizontal, style.Viewport);
                var height = GetLength(element, "height", SvgLengthAxis.Vertical, style.Viewport);
                if (width <= 0 || height <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "x", SvgLengthAxis.Horizontal, style.Viewport),
                    GetLength(element, "y", SvgLengthAxis.Vertical, style.Viewport),
                    width, height);
            }
            case "circle":
            {
                var r = GetLength(element, "r", SvgLengthAxis.Other, style.Viewport);
                if (r <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, style.Viewport) - r,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, style.Viewport) - r,
                    2 * r, 2 * r);
            }
            case "ellipse":
            {
                var rx = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style.Viewport);
                var ry = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style.Viewport);
                var radiusX = rx ?? ry ?? 0;
                var radiusY = ry ?? rx ?? 0;
                if (radiusX <= 0 || radiusY <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, style.Viewport) - radiusX,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, style.Viewport) - radiusY,
                    2 * radiusX, 2 * radiusY);
            }
            case "line":
            {
                return new Rect(
                    new Point(
                        GetLength(element, "x1", SvgLengthAxis.Horizontal, style.Viewport),
                        GetLength(element, "y1", SvgLengthAxis.Vertical, style.Viewport)),
                    new Point(
                        GetLength(element, "x2", SvgLengthAxis.Horizontal, style.Viewport),
                        GetLength(element, "y2", SvgLengthAxis.Vertical, style.Viewport)));
            }
            case "path":
            {
                if (element.GetStyleOrAttribute("d") is not { Length: > 0 } data)
                    return default;
                var geometry = new StreamGeometry();
                using (var geometryContext = geometry.Open())
                {
                    try
                    {
                        SvgPathParser.Parse(data.AsSpan(), geometryContext);
                    }
                    catch (FormatException)
                    {
                    }
                }

                return geometry.Bounds;
            }
            case "polygon":
            case "polyline":
            {
                if (element.GetAttribute("points") is not { Length: > 0 } points)
                    return default;
                var geometry = new StreamGeometry();
                using (var geometryContext = geometry.Open())
                {
                    SvgPointsParser.Parse(points.AsSpan(), geometryContext, close: true);
                }

                return geometry.Bounds;
            }
            default:
            {
                // Containers (and use) measure through a throwaway strokeless
                // recording of their content; decorations are skipped via the
                // measuring flag.
                var measureStyle = style;
                measureStyle.Stroke = SvgPaint.None;
                measureStyle.MarkerStart = measureStyle.MarkerMid = measureStyle.MarkerEnd = null;

                var previous = compileContext.Measuring;
                compileContext.Measuring = true;
                try
                {
                    var capturedStyle = measureStyle;
                    using var recording = DrawingRecording.Create(ctx =>
                    {
                        if (element.Name is "g" or "a" or "svg")
                        {
                            foreach (var child in element.Children)
                                CompileElement(child, ctx, compileContext, capturedStyle);
                        }
                        else if (element.Name == "use")
                        {
                            CompileUse(element, ctx, compileContext, capturedStyle);
                        }
                    });

                    return recording.Bounds;
                }
                finally
                {
                    compileContext.Measuring = previous;
                }
            }
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
