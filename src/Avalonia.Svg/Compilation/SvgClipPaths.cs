using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>How a <c>clip-path</c> value resolved.</summary>
internal enum SvgClipPathResult
{
    /// <summary>No clipping applies (none, unknown shape, lenient invalid reference).</summary>
    NotClipped,

    /// <summary>A clip geometry was produced.</summary>
    Clipped,

    /// <summary>The clip resolves to nothing: the element must not render.</summary>
    Hidden,
}

/// <summary>
/// Builds clip geometries for <c>clip-path</c> values — <c>&lt;clipPath&gt;</c>
/// references and CSS basic shapes — for
/// <see cref="DrawingContext.PushGeometryClip"/> emission.
/// </summary>
internal static class SvgClipPaths
{
    private const int MaxUseDepth = 8;

    /// <summary>
    /// Resolves a <c>clip-path</c> value on a rendered element. An unresolvable
    /// <c>url()</c> reference is ignored at this level, per CSS Masking; an
    /// empty or in-error <c>clipPath</c> hides the element.
    /// </summary>
    public static SvgClipPathResult Resolve(
        SvgCompileContext context, string value, Rect bounds, double strokeWidth, out Geometry? geometry)
    {
        geometry = null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == "none")
            return SvgClipPathResult.NotClipped;

        if (TryParseUrlReference(trimmed, out var id))
        {
            if (context.Document.GetElementById(id) is not { Name: "clipPath" } clipPath)
                return SvgClipPathResult.NotClipped;

            geometry = BuildClipPathGeometry(context, clipPath, bounds);
            return geometry == null ? SvgClipPathResult.Hidden : SvgClipPathResult.Clipped;
        }

        if (TryParseBasicShape(context, trimmed, bounds, strokeWidth, out geometry))
            return SvgClipPathResult.Clipped;

        return SvgClipPathResult.NotClipped;
    }

    internal static bool TryParseUrlReference(string value, out string id)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("url(", StringComparison.Ordinal))
        {
            var close = trimmed.IndexOf(')');
            if (close > 4)
            {
                // SVG 2 allows the reference to be quoted.
                var target = trimmed.Substring(4, close - 4).Trim().Trim('\'', '"');
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

    /// <summary>
    /// Builds the geometry of a <c>&lt;clipPath&gt;</c> element, or null when
    /// the clip is in error or empty — which clips everything away. Invalid
    /// children are excluded; a clipPath left with no valid children, an
    /// unresolvable <c>clip-path</c> of its own, or a non-invertible
    /// <c>transform</c> are all in error.
    /// </summary>
    private static Geometry? BuildClipPathGeometry(SvgCompileContext context, SvgElement clipPath, Rect bounds)
    {
        if (!context.EnterClipPath(clipPath))
            return null;

        try
        {
            // A non-invertible or unparsable transform puts the clipPath in error.
            var clipTransform = Matrix.Identity;
            if (clipPath.GetAnimatedOrAttribute("transform") is { } transformValue)
            {
                if (!SvgTransformParser.TryParse(transformValue.AsSpan(), out clipTransform)
                    || !clipTransform.HasInverse)
                {
                    return null;
                }
            }

            // The clipPath's own clip-path intersects the result; in-error
            // references are strict here, unlike on rendered elements.
            Geometry? selfClip = null;
            if (clipPath.GetStyleOrAttribute("clip-path") is { } selfValue)
            {
                switch (ResolveInner(context, selfValue, bounds, out selfClip))
                {
                    case InnerClip.Invalid:
                        return null;
                }
            }

            Geometry? result = null;
            foreach (var child in clipPath.Children)
            {
                if (BuildChildGeometry(context, child, bounds, useDepth: 0) is not { } childGeometry)
                    continue;

                result = result == null
                    ? childGeometry
                    : new CombinedGeometry(GeometryCombineMode.Union, result, childGeometry);
            }

            if (result == null)
                return null;

            // clipPathUnits default to userSpaceOnUse; objectBoundingBox maps the
            // unit square onto the clipped element's bounding box. The transform
            // attribute applies on top, in the target user space.
            var wrap = clipTransform;
            if (clipPath.GetAttribute("clipPathUnits") == "objectBoundingBox")
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return null;

                wrap = Matrix.CreateScale(bounds.Width, bounds.Height)
                       * Matrix.CreateTranslation(bounds.X, bounds.Y)
                       * clipTransform;
            }

            result = ApplyTransform(result, wrap);

            if (selfClip != null)
                result = new CombinedGeometry(GeometryCombineMode.Intersect, result, selfClip);

            return result;
        }
        finally
        {
            context.ExitClipPath(clipPath);
        }
    }

    private enum InnerClip
    {
        /// <summary>No clip applies (absent value or a recursion broken by ignoring it).</summary>
        None,
        Clip,
        Invalid,
    }

    /// <summary>
    /// Resolves a <c>clip-path</c> attribute inside clipPath content. A
    /// reference that cycles back to a clipPath currently being built is
    /// ignored; anything else that fails to resolve is in error.
    /// </summary>
    private static InnerClip ResolveInner(SvgCompileContext context, string value, Rect bounds, out Geometry? geometry)
    {
        geometry = null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == "none")
            return InnerClip.None;

        if (!TryParseUrlReference(trimmed, out var id))
            return InnerClip.None;

        if (context.Document.GetElementById(id) is not { Name: "clipPath" } clipPath)
            return InnerClip.Invalid;

        if (context.IsBuildingClipPath(clipPath))
            return InnerClip.None;

        geometry = BuildClipPathGeometry(context, clipPath, bounds);
        return geometry == null ? InnerClip.Invalid : InnerClip.Clip;
    }

    /// <summary>
    /// Builds one clipPath child's contribution: the shape's fill geometry with
    /// its inherited clip-rule, intersected with its own clip-path in local
    /// coordinates, then transformed. Invalid kinds and invisible children
    /// contribute nothing.
    /// </summary>
    private static Geometry? BuildChildGeometry(SvgCompileContext context, SvgElement element, Rect bounds, int useDepth)
    {
        if (element.GetStyleOrAttribute("display") == "none")
            return null;
        if (GetInheritedProperty(element, "visibility") is "hidden" or "collapse")
            return null;

        Geometry? geometry;
        switch (element.Name)
        {
            case "use":
            {
                if (useDepth >= MaxUseDepth)
                    return null;

                var href = element.Href;
                if (href is not { Length: > 1 } || href[0] != '#'
                    || context.Document.GetElementById(href.Substring(1)) is not { } target)
                {
                    return null;
                }

                geometry = BuildChildGeometry(context, target, bounds, useDepth + 1);
                if (geometry == null)
                    return null;

                var x = GetLength(element, "x", SvgLengthAxis.Horizontal, context.Viewport);
                var y = GetLength(element, "y", SvgLengthAxis.Vertical, context.Viewport);
                if (x != 0 || y != 0)
                    geometry = ApplyTransform(geometry, Matrix.CreateTranslation(x, y));
                break;
            }

            case "text":
            {
                var style = BuildInheritedStyle(context, element);
                var rule = GetInheritedProperty(element, "clip-rule") == "evenodd"
                    ? FillRule.EvenOdd
                    : FillRule.NonZero;
                geometry = SvgText.BuildClipGeometry(element, context, style, rule);
                if (geometry == null)
                    return null;
                break;
            }

            default:
                geometry = TryCreateShapeGeometry(element, context);
                if (geometry == null)
                    return null;
                break;
        }

        // The child's own clip-path intersects in the child's local space,
        // before its transform applies to both.
        if (element.GetStyleOrAttribute("clip-path") is { } clipValue)
        {
            switch (ResolveInner(context, clipValue, bounds, out var childClip))
            {
                case InnerClip.Invalid:
                    return null;
                case InnerClip.Clip:
                    geometry = new CombinedGeometry(GeometryCombineMode.Intersect, geometry, childClip!);
                    break;
            }
        }

        if (element.GetAnimatedOrAttribute("transform") is { } transform
            && SvgTransformParser.TryParse(transform.AsSpan(), out var matrix)
            && !matrix.IsIdentity)
        {
            geometry = ApplyTransform(geometry, matrix);
        }

        return geometry;
    }

    /// <summary>Composes a matrix onto a geometry's existing matrix transform.</summary>
    private static Geometry ApplyTransform(Geometry geometry, Matrix matrix)
    {
        if (matrix.IsIdentity)
            return geometry;

        geometry.Transform = geometry.Transform is MatrixTransform existing
            ? new MatrixTransform(existing.Matrix * matrix)
            : new MatrixTransform(matrix);
        return geometry;
    }

    /// <summary>
    /// Looks up an inherited presentation property through the element's
    /// ancestor chain (clip-rule and visibility inherit into clipPath content).
    /// </summary>
    private static string? GetInheritedProperty(SvgElement element, string name)
    {
        for (var current = element; current != null; current = current.Parent)
        {
            if (current.GetStyleOrAttribute(name) is { } value && value != "inherit")
                return value;
        }

        return null;
    }

    /// <summary>Builds the cascaded style at an element's tree position.</summary>
    private static SvgStyle BuildInheritedStyle(SvgCompileContext context, SvgElement element)
    {
        var chain = new List<SvgElement>();
        for (var current = element; current != null; current = current.Parent)
            chain.Add(current);

        var style = SvgStyle.CreateDefault(context.Viewport);
        style.RootFontSize = context.RootFontSize;
        for (var i = chain.Count - 1; i >= 0; i--)
            style.Apply(chain[i]);

        return style;
    }

    private static Geometry? TryCreateShapeGeometry(SvgElement element, SvgCompileContext context)
    {
        var viewport = context.Viewport;
        return element.Name switch
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
    }

    private static Geometry? CreateRect(SvgElement element, Size viewport)
    {
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, viewport);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, viewport);
        var width = GetLength(element, "width", SvgLengthAxis.Horizontal, viewport);
        var height = GetLength(element, "height", SvgLengthAxis.Vertical, viewport);
        if (width <= 0 || height <= 0)
            return null;

        // rx/ry default to each other and clamp to half the side.
        var rx = GetOptionalLength(element, "rx", SvgLengthAxis.Horizontal, viewport);
        var ry = GetOptionalLength(element, "ry", SvgLengthAxis.Vertical, viewport);
        var radiusX = Math.Min(Math.Max(rx ?? ry ?? 0, 0), width / 2);
        var radiusY = Math.Min(Math.Max(ry ?? rx ?? 0, 0), height / 2);

        return new RectangleGeometry(new Rect(x, y, width, height), radiusX, radiusY);
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
                GetInheritedProperty(element, "clip-rule") == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero);
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
            geometryContext.SetFillRule(
                GetInheritedProperty(element, "clip-rule") == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero);
            any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
        }

        return any ? geometry : null;
    }

    /// <summary>
    /// Parses the CSS basic-shape form <c>circle(&lt;radius&gt;? [at x y]?) &lt;box&gt;?</c>
    /// — the shape SVG 2 content uses in practice. The reference box defaults
    /// to the fill box; <c>stroke-box</c> inflates it by half the stroke width
    /// and <c>view-box</c> uses the nearest viewport.
    /// </summary>
    private static bool TryParseBasicShape(
        SvgCompileContext context, string value, Rect bounds, double strokeWidth, out Geometry? geometry)
    {
        geometry = null;

        if (!value.StartsWith("circle(", StringComparison.Ordinal))
            return false;

        var close = value.IndexOf(')');
        if (close < 0)
            return false;

        var arguments = value.Substring(7, close - 7).Trim();
        var boxKeyword = value.Substring(close + 1).Trim();

        Rect box;
        switch (boxKeyword)
        {
            case "" or "fill-box" or "content-box" or "padding-box" or "border-box":
                box = bounds;
                break;
            case "stroke-box" or "margin-box":
                box = bounds.Inflate(strokeWidth / 2);
                break;
            case "view-box":
                box = new Rect(context.Viewport);
                break;
            default:
                return false;
        }

        if (box.Width <= 0 || box.Height <= 0)
            return false;

        var radiusPart = arguments;
        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;

        var atIndex = arguments.IndexOf("at", StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            radiusPart = arguments.Substring(0, atIndex).Trim();
            var position = arguments.Substring(atIndex + 2).Trim();
            if (!TryParseCoordinate(position, box.X, box.Width, out cx)
                || !TryParseSecondCoordinate(position, box.Y, box.Height, out cy))
            {
                return false;
            }
        }

        double radius;
        if (radiusPart.Length == 0 || radiusPart == "closest-side")
        {
            radius = Math.Min(
                Math.Min(Math.Abs(cx - box.X), Math.Abs(box.Right - cx)),
                Math.Min(Math.Abs(cy - box.Y), Math.Abs(box.Bottom - cy)));
        }
        else if (radiusPart == "farthest-side")
        {
            radius = Math.Max(
                Math.Max(Math.Abs(cx - box.X), Math.Abs(box.Right - cx)),
                Math.Max(Math.Abs(cy - box.Y), Math.Abs(box.Bottom - cy)));
        }
        else if (SvgLength.TryParse(radiusPart.AsSpan(), out var radiusLength))
        {
            // Percentages refer to the box diagonal normalized by sqrt(2).
            radius = radiusLength.Unit == SvgLengthUnit.Percent
                ? radiusLength.Value / 100 * Math.Sqrt(box.Width * box.Width + box.Height * box.Height) / Math.Sqrt(2)
                : radiusLength.Resolve(SvgLengthAxis.Other, context.Viewport);
        }
        else
        {
            return false;
        }

        if (radius <= 0)
            return false;

        geometry = new EllipseGeometry(new Rect(cx - radius, cy - radius, 2 * radius, 2 * radius));
        return true;
    }

    private static bool TryParseCoordinate(string position, double origin, double size, out double result)
    {
        result = 0;
        var space = position.IndexOf(' ');
        var token = (space < 0 ? position : position.Substring(0, space)).Trim();
        if (!SvgLength.TryParse(token.AsSpan(), out var length))
            return false;

        result = origin + (length.Unit == SvgLengthUnit.Percent ? length.Value / 100 * size : length.Value);
        return true;
    }

    private static bool TryParseSecondCoordinate(string position, double origin, double size, out double result)
    {
        result = 0;
        var space = position.IndexOf(' ');
        if (space < 0)
        {
            // A single position value centers the other axis.
            result = origin + size / 2;
            return true;
        }

        var token = position.Substring(space + 1).Trim();
        if (!SvgLength.TryParse(token.AsSpan(), out var length))
            return false;

        result = origin + (length.Unit == SvgLengthUnit.Percent ? length.Value / 100 * size : length.Value);
        return true;
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
        => GetOptionalLength(element, name, axis, viewport) ?? 0;

    private static double? GetOptionalLength(SvgElement element, string name, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && value != "auto" && SvgLength.TryParse(value.AsSpan(), out var length))
            return length.Resolve(axis, viewport);
        return null;
    }
}
