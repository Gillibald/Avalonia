using System;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Builds clip geometries from <c>&lt;clipPath&gt;</c> elements for
/// <see cref="DrawingContext.PushGeometryClip"/> emission.
/// </summary>
internal static class SvgClipPaths
{
    /// <summary>
    /// Resolves a <c>clip-path: url(#id)</c> reference to a geometry, or null
    /// when the reference is invalid or produces no shapes.
    /// </summary>
    public static Geometry? TryBuild(SvgCompileContext context, string reference, Rect bounds)
    {
        if (!TryParseUrlReference(reference, out var id))
            return null;

        var element = context.Document.GetElementById(id);
        if (element is not { Name: "clipPath" })
            return null;

        var group = new GeometryGroup { FillRule = FillRule.NonZero };

        foreach (var child in element.Children)
        {
            if (TryCreateShapeGeometry(child, context) is { } geometry)
                group.Children.Add(geometry);
        }

        if (group.Children.Count == 0)
            return null;

        // clipPathUnits default to userSpaceOnUse; objectBoundingBox maps the
        // unit square onto the clipped element's bounding box.
        if (element.GetAttribute("clipPathUnits") == "objectBoundingBox")
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            group.Transform = new MatrixTransform(
                Matrix.CreateScale(bounds.Width, bounds.Height)
                * Matrix.CreateTranslation(bounds.X, bounds.Y));
        }

        return group;
    }

    internal static bool TryParseUrlReference(string value, out string id)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("url(", StringComparison.Ordinal))
        {
            var close = trimmed.IndexOf(')');
            if (close > 4)
            {
                var target = trimmed.Substring(4, close - 4).Trim();
                if (target.Length > 1 && target[0] == '#')
                {
                    id = target.Substring(1);
                    return true;
                }
            }
        }

        id = string.Empty;
        return false;
    }

    private static Geometry? TryCreateShapeGeometry(SvgElement element, SvgCompileContext context)
    {
        var viewport = context.Viewport;
        Geometry? geometry = element.Name switch
        {
            "rect" => CreateRect(element, viewport),
            "circle" => CreateCircle(element, viewport),
            "ellipse" => CreateEllipse(element, viewport),
            "path" => CreatePath(element),
            "polygon" => CreatePoly(element, close: true),
            // An open polyline clips as if closed (clipping uses fill geometry).
            "polyline" => CreatePoly(element, close: true),
            _ => null,
        };

        if (geometry != null
            && element.GetAttribute("transform") is { } transform
            && SvgTransformParser.TryParse(transform.AsSpan(), out var matrix)
            && !matrix.IsIdentity)
        {
            geometry.Transform = new MatrixTransform(matrix);
        }

        return geometry;
    }

    private static Geometry? CreateRect(SvgElement element, Size viewport)
    {
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, viewport);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, viewport);
        var width = GetLength(element, "width", SvgLengthAxis.Horizontal, viewport);
        var height = GetLength(element, "height", SvgLengthAxis.Vertical, viewport);
        return width > 0 && height > 0 ? new RectangleGeometry(new Rect(x, y, width, height)) : null;
    }

    private static Geometry? CreateCircle(SvgElement element, Size viewport)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, viewport);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, viewport);
        var r = GetLength(element, "r", SvgLengthAxis.Other, viewport);
        return r > 0 ? new EllipseGeometry(new Rect(cx - r, cy - r, 2 * r, 2 * r)) : null;
    }

    private static Geometry? CreateEllipse(SvgElement element, Size viewport)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, viewport);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, viewport);
        var rx = GetLength(element, "rx", SvgLengthAxis.Horizontal, viewport);
        var ry = GetLength(element, "ry", SvgLengthAxis.Vertical, viewport);
        return rx > 0 && ry > 0 ? new EllipseGeometry(new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry)) : null;
    }

    private static Geometry? CreatePath(SvgElement element)
    {
        var data = element.GetStyleOrAttribute("d");
        if (string.IsNullOrEmpty(data))
            return null;

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.SetFillRule(
                element.GetStyleOrAttribute("clip-rule") == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero);
            try
            {
                SvgPathParser.Parse(data.AsSpan(), geometryContext);
            }
            catch (FormatException)
            {
                // The valid prefix clips, per the SVG error-handling rules.
            }
        }

        return geometry;
    }

    private static Geometry? CreatePoly(SvgElement element, bool close)
    {
        var points = element.GetAttribute("points");
        if (string.IsNullOrEmpty(points))
            return null;

        var geometry = new StreamGeometry();
        bool any;
        using (var geometryContext = geometry.Open())
        {
            geometryContext.SetFillRule(FillRule.NonZero);
            any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
        }

        return any ? geometry : null;
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, viewport);
        return 0;
    }
}
