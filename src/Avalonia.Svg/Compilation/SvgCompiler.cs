using System;
using Avalonia.Media;
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
            var style = SvgStyle.CreateDefault(contentViewport);
            style.Apply(root);

            foreach (var child in root.Children)
                CompileElement(child, context, style);
        }
    }

    private static void CompileElement(SvgElement element, DrawingContext context, in SvgStyle parentStyle)
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
            switch (element.Name)
            {
                case "g":
                case "a":
                case "svg":
                {
                    foreach (var child in element.Children)
                        CompileElement(child, context, style);
                    break;
                }
                case "rect":
                    CompileRect(element, context, style);
                    break;
                case "circle":
                    CompileCircle(element, context, style);
                    break;
                case "ellipse":
                    CompileEllipse(element, context, style);
                    break;
                case "line":
                    CompileLine(element, context, style);
                    break;
                case "polyline":
                    CompilePoly(element, context, style, close: false);
                    break;
                case "polygon":
                    CompilePoly(element, context, style, close: true);
                    break;
                case "path":
                    CompilePath(element, context, style);
                    break;
            }
        }
    }

    private static void CompileRect(SvgElement element, DrawingContext context, in SvgStyle style)
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

        var brush = style.ResolveFillBrush();
        var pen = style.ResolvePen();
        if (brush == null && pen == null)
            return;

        context.DrawRectangle(brush, pen, new Rect(x, y, width, height), radiusX, radiusY);
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

    private static void CompileCircle(SvgElement element, DrawingContext context, in SvgStyle style)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, style.Viewport);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, style.Viewport);
        var r = GetLength(element, "r", SvgLengthAxis.Other, style.Viewport);

        if (r <= 0)
            return;

        var brush = style.ResolveFillBrush();
        var pen = style.ResolvePen();
        if (brush == null && pen == null)
            return;

        context.DrawEllipse(brush, pen, new Rect(cx - r, cy - r, 2 * r, 2 * r));
    }

    private static void CompileEllipse(SvgElement element, DrawingContext context, in SvgStyle style)
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

        var brush = style.ResolveFillBrush();
        var pen = style.ResolvePen();
        if (brush == null && pen == null)
            return;

        context.DrawEllipse(brush, pen, new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry));
    }

    private static void CompileLine(SvgElement element, DrawingContext context, in SvgStyle style)
    {
        var pen = style.ResolvePen();
        if (pen == null)
            return;

        var x1 = GetLength(element, "x1", SvgLengthAxis.Horizontal, style.Viewport);
        var y1 = GetLength(element, "y1", SvgLengthAxis.Vertical, style.Viewport);
        var x2 = GetLength(element, "x2", SvgLengthAxis.Horizontal, style.Viewport);
        var y2 = GetLength(element, "y2", SvgLengthAxis.Vertical, style.Viewport);

        context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
    }

    private static void CompilePoly(SvgElement element, DrawingContext context, in SvgStyle style, bool close)
    {
        var points = element.GetAttribute("points");
        if (string.IsNullOrEmpty(points))
            return;

        var brush = style.ResolveFillBrush();
        var pen = style.ResolvePen();
        if (brush == null && pen == null)
            return;

        var geometry = new StreamGeometry();
        bool any;
        using (var geometryContext = geometry.Open())
        {
            geometryContext.SetFillRule(style.FillRule);
            any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
        }

        if (any)
            context.DrawGeometry(brush, pen, geometry);
    }

    private static void CompilePath(SvgElement element, DrawingContext context, in SvgStyle style)
    {
        var data = element.GetStyleOrAttribute("d");
        if (string.IsNullOrEmpty(data))
            return;

        var brush = style.ResolveFillBrush();
        var pen = style.ResolvePen();
        if (brush == null && pen == null)
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

        context.DrawGeometry(brush, pen, geometry);
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, viewport);
        return 0;
    }
}
