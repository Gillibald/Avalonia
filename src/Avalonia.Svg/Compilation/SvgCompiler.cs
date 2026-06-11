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
    public static void CompileDocument(SvgDocument document, DrawingContext context, Size viewportSize) =>
        CompileDocument(document, context, viewportSize, new SvgCompileOptions());

    /// <summary>
    /// Compiles the document and additionally builds the element hit-test tree
    /// alongside the emitted draw calls. Returns the tree's root node.
    /// </summary>
    public static SvgHitNode? CompileDocumentWithHitTree(
        SvgDocument document, DrawingContext context, Size viewportSize)
    {
        var options = new SvgCompileOptions { BuildHitTree = true };
        CompileDocument(document, context, viewportSize, options);
        return options.HitRoot;
    }

    /// <summary>
    /// Compiles the document with the given options; results (hit tree,
    /// animated paint brushes) are written back onto <paramref name="options"/>.
    /// </summary>
    public static void CompileDocument(
        SvgDocument document, DrawingContext context, Size viewportSize, SvgCompileOptions options)
    {
        var root = document.Root;

        // display and opacity on the root element apply like on any container.
        if (root.GetStyleOrAttribute("display") == "none")
            return;

        var rootOpacity = 1.0;
        if (root.GetStyleOrAttribute("opacity") is { } rootOpacityValue
            && SvgStyle.TryParseOpacity(rootOpacityValue, out var parsedRootOpacity))
        {
            rootOpacity = parsedRootOpacity;
        }

        if (rootOpacity <= 0)
            return;

        // The document rasterizes onto a canvas of unknown opacity, and
        // subpixel (LCD) glyph coverage is only well-defined over opaque
        // pixels — blended into transparency it fattens the glyphs. Text
        // therefore uses grayscale antialiasing, like browsers rasterizing
        // SVG layers. Nested recording playback inherits this.
        using var textRendering = context.PushTextOptions(new TextOptions
        {
            TextRenderingMode = TextRenderingMode.Antialias,
        });

        DrawingContext.PushedState? rootOpacityState = rootOpacity < 1
            ? context.PushOpacity(rootOpacity)
            : null;

        using var _ = rootOpacityState;

        var viewBox = document.TryGetViewBox();

        DrawingContext.PushedState? viewBoxState = null;
        var contentViewport = viewportSize;
        var rootMatrix = Matrix.Identity;

        if (viewBox is { } vb)
        {
            var preserveAspectRatio = SvgPreserveAspectRatio.Default;
            if (root.GetAttribute("preserveAspectRatio") is { } par)
                SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);

            rootMatrix = preserveAspectRatio.ComputeTransform(vb, viewportSize);
            if (!rootMatrix.IsIdentity)
                viewBoxState = context.PushTransform(rootMatrix);

            // Percentages inside the document resolve against the viewport the
            // svg element establishes — the viewBox coordinate system when present.
            contentViewport = new Size(vb.Width, vb.Height);
        }

        using (viewBoxState)
        {
            var compileContext = new SvgCompileContext(document, contentViewport)
            {
                PaintAnimationTargets = options.PaintAnimationTargets,
            };

            SvgHitTreeBuilder? hitTree = null;
            if (options.BuildHitTree)
            {
                hitTree = new SvgHitTreeBuilder(rootMatrix);
                hitTree.Root.Element = root;
                compileContext.SetHitTreeBuilder(hitTree);
            }

            var style = SvgStyle.CreateDefault(contentViewport);
            style.Apply(root);

            // The root's computed font size is the reference for rem lengths
            // everywhere in the document, including shared content.
            style.RootFontSize = style.FontSize;
            compileContext.RootFontSize = style.FontSize;

            // clip-path on the root element clips the whole document; its fill
            // box is the viewport the root establishes.
            DrawingContext.PushedState? rootClipState = null;
            if (root.GetStyleOrAttribute("clip-path") is { } rootClipValue)
            {
                switch (SvgClipPaths.Resolve(
                            compileContext, rootClipValue, new Rect(contentViewport), 0, out var rootClip))
                {
                    case SvgClipPathResult.Hidden:
                        options.HitRoot = hitTree?.Root;
                        return;
                    case SvgClipPathResult.Clipped:
                        rootClipState = context.PushGeometryClip(rootClip!);
                        break;
                }
            }

            using (rootClipState)
            {
                foreach (var child in root.Children)
                    CompileElement(child, context, compileContext, style);
            }

            options.HitRoot = hitTree?.Root;
            options.AnimatedBrushes = compileContext.AnimatedBrushes;
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
            // Animation elements are consumed by the animator, not rendered.
            case "animate":
            case "set":
            case "animateTransform":
                return;
        }

        if (element.GetStyleOrAttribute("display") == "none")
            return;

        var style = parentStyle;
        style.Apply(element);

        var localTransform = Matrix.Identity;
        DrawingContext.PushedState? transformState = null;
        if (element.GetAnimatedOrAttribute("transform") is { } transform
            && SvgTransformParser.TryParse(transform.AsSpan(), out var matrix)
            && !matrix.IsIdentity)
        {
            localTransform = matrix;
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

                // mix-blend-mode and isolation are CSS-only properties: they
                // have no presentation attribute form.
                var blendMode = ParseBlendMode(element.GetStyleProperty("mix-blend-mode"));
                var isolate = element.GetStyleProperty("isolation") == "isolate";

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
                Geometry? clipGeometry = null;
                DrawingContext.PushedState? clipState = null;
                if (!compileContext.Measuring
                    && element.GetStyleOrAttribute("clip-path") is { } clipReference)
                {
                    var clipBounds = GetFillBounds(element, compileContext, style);
                    var strokeWidth = style.Stroke.Kind != SvgPaintKind.None ? style.StrokeWidth : 0;
                    switch (SvgClipPaths.Resolve(compileContext, clipReference, clipBounds, strokeWidth, out var clip))
                    {
                        case SvgClipPathResult.Hidden:
                            // The clip resolves to nothing: the element is not rendered.
                            return;
                        case SvgClipPathResult.Clipped:
                            clipGeometry = clip;
                            clipState = context.PushGeometryClip(clip!);
                            break;
                    }
                }

                using (clipState)
                {
                    IDisposable? maskState = null;
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
                            {
                                // The hit node mirrors only what affects hit
                                // testing: the element's transform and clip-path.
                                // Layers (opacity, blending), masks and filters
                                // change pixels, not the hit geometry.
                                var hitTree = compileContext.HitTree;
                                SvgHitNode? hitNode = null;
                                if (hitTree != null)
                                {
                                    if (element.Name is "g" or "a" or "svg" or "use")
                                        hitNode = hitTree.PushNode(element, localTransform, clipGeometry: clipGeometry);
                                    else if (!localTransform.IsIdentity || clipGeometry != null)
                                        hitNode = hitTree.PushNode(null, localTransform, clipGeometry: clipGeometry);
                                }

                                try
                                {
                                    CompileElementContent(element, context, compileContext, style);
                                }
                                finally
                                {
                                    if (hitNode != null)
                                        hitTree!.Pop();
                                }
                            }
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
            var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style);
            var y = GetLength(element, "y", SvgLengthAxis.Vertical, style);
            var recording = compileContext.GetSharedRecording(target, out var ownership, out var hitSubtree);
            if (recording == null)
                return;
            var placement = Matrix.CreateTranslation(x, y);

            if (target.Name is "symbol" or "svg")
            {
                var width = GetUseViewportLength(element, target, "width", SvgLengthAxis.Horizontal, style);
                var height = GetUseViewportLength(element, target, "height", SvgLengthAxis.Vertical, style);
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
                using (context.PushTransform(placement))
                using (context.PushClip(new Rect(0, 0, width, height)))
                {
                    context.DrawRecording(recording, contentMatrix, ownership);
                }

                if (hitSubtree != null)
                {
                    compileContext.HitTree?.AddUseSubtree(
                        placement, new Rect(0, 0, width, height), contentMatrix, hitSubtree);
                }
            }
            else
            {
                context.DrawRecording(recording, placement, ownership);

                if (hitSubtree != null)
                    compileContext.HitTree?.AddUseSubtree(placement, null, Matrix.Identity, hitSubtree);
            }
        }
        finally
        {
            compileContext.ExitUse(target);
        }
    }

    private static double GetUseViewportLength(
        SvgElement use, SvgElement target, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        // width/height resolve from the use site, then the symbol, then 100%.
        var value = use.GetAttribute(name) ?? target.GetAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return style.ResolveLength(length, axis);
        return axis == SvgLengthAxis.Horizontal ? style.Viewport.Width : style.Viewport.Height;
    }

    private static readonly ImmutableSolidColorBrush s_measuringFill = new(Colors.Black);

    private static IImmutableBrush? ResolvePaint(
        in SvgPaint paint, in SvgStyle style, SvgCompileContext compileContext, Rect bounds, double opacity)
    {
        if (paint.Kind == SvgPaintKind.Reference)
        {
            // Fill boxes do not depend on the paint, so measuring recordings
            // substitute a plain brush — resolving a paint server here would
            // compile pattern content under measuring semantics and pollute the
            // document's shared-recording cache with decoration-free content.
            if (compileContext.Measuring)
                return s_measuringFill;

            var brush = paint.Reference is { } id
                ? SvgPaintServers.Resolve(compileContext, id, style, bounds, opacity)
                : null;
            if (brush != null)
                return brush;

            // An unresolved reference uses the declared fallback; without one
            // the paint is invalid and paints nothing.
            return paint.Fallback switch
            {
                SvgPaintFallback.Color => new ImmutableSolidColorBrush(paint.FallbackColor, opacity),
                SvgPaintFallback.CurrentColor => new ImmutableSolidColorBrush(style.Color, opacity),
                _ => null,
            };
        }

        return style.ResolveBrush(paint, opacity);
    }

    /// <summary>
    /// Resolves a shape's fill for drawing, swapping in the registered mutable
    /// brush when the (element, fill) pair runs on the animation paint channel.
    /// </summary>
    private static IBrush? ResolveFillForDrawing(
        SvgElement element, in SvgStyle style, SvgCompileContext compileContext, Rect bounds)
    {
        var brush = ResolvePaint(style.Fill, style, compileContext, bounds, style.FillOpacity);
        return compileContext.TryGetAnimatedBrush(element, "fill", brush) ?? (IBrush?)brush;
    }

    /// <inheritdoc cref="ResolveFillForDrawing"/>
    private static IPen? ResolveStrokeForDrawing(
        SvgElement element, in SvgStyle style, SvgCompileContext compileContext, Rect bounds)
    {
        var brush = ResolvePaint(style.Stroke, style, compileContext, bounds, style.StrokeOpacity);
        if (compileContext.TryGetAnimatedBrush(element, "stroke", brush) is { } animated)
            return style.ResolveMutablePen(animated);
        return style.ResolvePen(brush);
    }

    private static void CompileRect(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var x = GetLength(element, "x", SvgLengthAxis.Horizontal, style);
        var y = GetLength(element, "y", SvgLengthAxis.Vertical, style);
        var width = GetLength(element, "width", SvgLengthAxis.Horizontal, style);
        var height = GetLength(element, "height", SvgLengthAxis.Vertical, style);

        if (width <= 0 || height <= 0)
            return;

        // rx/ry default to 'auto' (SVG 2): an auto value takes the other's value;
        // both auto means square corners. Values clamp to half the rect side.
        var rx = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style);
        var ry = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style);
        var radiusX = rx ?? ry ?? 0;
        var radiusY = ry ?? rx ?? 0;
        radiusX = Math.Min(radiusX, width / 2);
        radiusY = Math.Min(radiusY, height / 2);

        var rect = new Rect(x, y, width, height);
        AddHitShape(element, compileContext, style, new SvgHitShape
        {
            Kind = SvgHitShape.ShapeKind.Rectangle,
            Bounds = rect,
        });

        if (!ShouldPaint(style, compileContext))
            return;

        var brush = ResolveFillForDrawing(element, style, compileContext, rect);
        var pen = ResolveStrokeForDrawing(element, style, compileContext, rect);
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

    /// <summary>
    /// <c>visibility: hidden</c> suppresses painting but, unlike
    /// <c>display: none</c>, keeps layout and the fill box — measuring passes
    /// therefore include hidden geometry, matching <c>getBBox()</c>.
    /// </summary>
    private static bool ShouldPaint(in SvgStyle style, SvgCompileContext compileContext) =>
        style.Visible || compileContext.Measuring;

    private static void AddHitShape(
        SvgElement element, SvgCompileContext compileContext, in SvgStyle style, SvgHitShape shape)
    {
        if (compileContext.HitTree is not { } hitTree)
            return;

        shape.StrokeWidth = style.StrokeWidth;
        if (shape.Kind != SvgHitShape.ShapeKind.Line)
            shape.HasFill = style.Fill.Kind != SvgPaintKind.None;
        shape.HasStroke = style.Stroke.Kind != SvgPaintKind.None && style.StrokeWidth > 0;

        hitTree.AddShape(element, shape, style.PointerEvents, style.Visible);
    }

    private static double? GetCornerRadius(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value == null || value == "auto")
            return null;
        if (SvgLength.TryParse(value.AsSpan(), out var length)
            && style.ResolveLength(length, axis) is var resolved and >= 0)
        {
            return resolved;
        }

        // A negative or unparseable radius behaves as 'auto'.
        return null;
    }

    private static void CompileCircle(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, style);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, style);
        var r = GetLength(element, "r", SvgLengthAxis.Other, style);

        if (r <= 0)
            return;

        DrawEllipseShape(element, context, compileContext, style, new Rect(cx - r, cy - r, 2 * r, 2 * r));
    }

    private static void CompileEllipse(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style)
    {
        var cx = GetLength(element, "cx", SvgLengthAxis.Horizontal, style);
        var cy = GetLength(element, "cy", SvgLengthAxis.Vertical, style);

        // SVG 2: an 'auto' radius takes the other radius' value.
        var rxValue = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style);
        var ryValue = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style);
        var rx = rxValue ?? ryValue ?? 0;
        var ry = ryValue ?? rxValue ?? 0;

        if (rx <= 0 || ry <= 0)
            return;

        DrawEllipseShape(element, context, compileContext, style, new Rect(cx - rx, cy - ry, 2 * rx, 2 * ry));
    }

    private static void DrawEllipseShape(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style, Rect rect)
    {
        AddHitShape(element, compileContext, style, new SvgHitShape
        {
            Kind = SvgHitShape.ShapeKind.Ellipse,
            Bounds = rect,
        });

        if (!ShouldPaint(style, compileContext))
            return;

        var brush = ResolveFillForDrawing(element, style, compileContext, rect);
        var pen = ResolveStrokeForDrawing(element, style, compileContext, rect);
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
        var x1 = GetLength(element, "x1", SvgLengthAxis.Horizontal, style);
        var y1 = GetLength(element, "y1", SvgLengthAxis.Vertical, style);
        var x2 = GetLength(element, "x2", SvgLengthAxis.Horizontal, style);
        var y2 = GetLength(element, "y2", SvgLengthAxis.Vertical, style);

        AddHitShape(element, compileContext, style, new SvgHitShape
        {
            Kind = SvgHitShape.ShapeKind.Line,
            P1 = new Point(x1, y1),
            P2 = new Point(x2, y2),
        });

        if (!ShouldPaint(style, compileContext))
            return;

        var bounds = new Rect(new Point(x1, y1), new Point(x2, y2));
        var pen = ResolveStrokeForDrawing(element, style, compileContext, bounds);
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

        // The geometry also backs hit testing, so it is built even for unpainted
        // shapes when a hit tree is being collected (pointer-events can make
        // unpainted geometry interactive).
        if (style.Fill.Kind != SvgPaintKind.None || style.Stroke.Kind != SvgPaintKind.None
            || compileContext.HitTree != null)
        {
            var geometry = new StreamGeometry();
            bool any;
            using (var geometryContext = geometry.Open())
            {
                geometryContext.SetFillRule(style.FillRule);
                any = SvgPointsParser.Parse(points.AsSpan(), geometryContext, close);
            }

            if (any)
                DrawGeometryShape(element, context, compileContext, style, geometry);
        }

        if (!compileContext.Measuring && style.Visible && HasMarkers(style))
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

        if (style.Fill.Kind != SvgPaintKind.None || style.Stroke.Kind != SvgPaintKind.None
            || compileContext.HitTree != null)
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

            DrawGeometryShape(element, context, compileContext, style, geometry);
        }

        if (!compileContext.Measuring && style.Visible && HasMarkers(style))
        {
            var sampler = SvgPathSampler.Parse(data.AsSpan());
            SvgMarkers.Emit(context, compileContext, style, sampler.Vertices);
        }
    }

    private static void DrawGeometryShape(
        SvgElement element, DrawingContext context, SvgCompileContext compileContext, in SvgStyle style,
        StreamGeometry geometry)
    {
        var bounds = geometry.Bounds;
        AddHitShape(element, compileContext, style, new SvgHitShape
        {
            Kind = SvgHitShape.ShapeKind.Geometry,
            Bounds = bounds,
            Geometry = geometry,
        });

        if (!ShouldPaint(style, compileContext))
            return;

        var brush = ResolveFillForDrawing(element, style, compileContext, bounds);
        var pen = ResolveStrokeForDrawing(element, style, compileContext, bounds);
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
                var width = GetLength(element, "width", SvgLengthAxis.Horizontal, style);
                var height = GetLength(element, "height", SvgLengthAxis.Vertical, style);
                if (width <= 0 || height <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "x", SvgLengthAxis.Horizontal, style),
                    GetLength(element, "y", SvgLengthAxis.Vertical, style),
                    width, height);
            }
            case "circle":
            {
                var r = GetLength(element, "r", SvgLengthAxis.Other, style);
                if (r <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, style) - r,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, style) - r,
                    2 * r, 2 * r);
            }
            case "ellipse":
            {
                var rx = GetCornerRadius(element, "rx", SvgLengthAxis.Horizontal, style);
                var ry = GetCornerRadius(element, "ry", SvgLengthAxis.Vertical, style);
                var radiusX = rx ?? ry ?? 0;
                var radiusY = ry ?? rx ?? 0;
                if (radiusX <= 0 || radiusY <= 0)
                    return default;
                return new Rect(
                    GetLength(element, "cx", SvgLengthAxis.Horizontal, style) - radiusX,
                    GetLength(element, "cy", SvgLengthAxis.Vertical, style) - radiusY,
                    2 * radiusX, 2 * radiusY);
            }
            case "line":
            {
                return new Rect(
                    new Point(
                        GetLength(element, "x1", SvgLengthAxis.Horizontal, style),
                        GetLength(element, "y1", SvgLengthAxis.Vertical, style)),
                    new Point(
                        GetLength(element, "x2", SvgLengthAxis.Horizontal, style),
                        GetLength(element, "y2", SvgLengthAxis.Vertical, style)));
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
                        CompileElementContent(element, ctx, compileContext, capturedStyle));

                    return recording.Bounds;
                }
                finally
                {
                    compileContext.Measuring = previous;
                }
            }
        }
    }

    private static double GetLength(SvgElement element, string name, SvgLengthAxis axis, in SvgStyle style)
    {
        var value = element.GetStyleOrAttribute(name);
        if (value != null && SvgLength.TryParse(value.AsSpan(), out var length))
            return style.ResolveLength(length, axis);
        return 0;
    }
}
