using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avalonia.Svg.Compilation;

/// <summary>The SVG <c>pointer-events</c> property values.</summary>
internal enum SvgPointerEvents
{
    /// <summary>The default: visible elements hit on their painted parts.</summary>
    VisiblePainted,
    None,
    All,
    Fill,
    Stroke,
    Painted,
    Visible,
    VisibleFill,
    VisibleStroke,
}

/// <summary>The leaf geometry of a hit-test node.</summary>
internal sealed class SvgHitShape
{
    /// <summary>Axis-aligned shapes test analytically.</summary>
    public enum ShapeKind
    {
        Rectangle,
        Ellipse,
        Line,
        /// <summary>Geometry-backed shapes (path, polygon, polyline, clip-accurate).</summary>
        Geometry,
    }

    public ShapeKind Kind;
    public Rect Bounds;
    public Geometry? Geometry;
    /// <summary>Line endpoints, in given (unnormalized) order.</summary>
    public Point P1, P2;
    public double StrokeWidth;
    public bool HasFill;
    public bool HasStroke;

    public bool HitTest(Point point, SvgPointerEvents pointerEvents, bool visible)
    {
        bool wantsFill, wantsStroke, requiresVisible, requiresPainted;
        switch (pointerEvents)
        {
            case SvgPointerEvents.None:
                return false;
            case SvgPointerEvents.All:
                wantsFill = wantsStroke = true;
                requiresVisible = requiresPainted = false;
                break;
            case SvgPointerEvents.Fill:
                wantsFill = true;
                wantsStroke = false;
                requiresVisible = requiresPainted = false;
                break;
            case SvgPointerEvents.Stroke:
                wantsFill = false;
                wantsStroke = true;
                requiresVisible = requiresPainted = false;
                break;
            case SvgPointerEvents.Painted:
                wantsFill = wantsStroke = true;
                requiresVisible = false;
                requiresPainted = true;
                break;
            case SvgPointerEvents.Visible:
                wantsFill = wantsStroke = true;
                requiresVisible = true;
                requiresPainted = false;
                break;
            case SvgPointerEvents.VisibleFill:
                wantsFill = true;
                wantsStroke = false;
                requiresVisible = true;
                requiresPainted = false;
                break;
            case SvgPointerEvents.VisibleStroke:
                wantsFill = false;
                wantsStroke = true;
                requiresVisible = true;
                requiresPainted = false;
                break;
            default: // VisiblePainted
                wantsFill = wantsStroke = true;
                requiresVisible = true;
                requiresPainted = true;
                break;
        }

        if (requiresVisible && !visible)
            return false;

        var fillCounts = wantsFill && (!requiresPainted || HasFill);
        var strokeCounts = wantsStroke && (!requiresPainted || HasStroke);

        if (fillCounts && FillContains(point))
            return true;
        if (strokeCounts && StrokeContains(point))
            return true;
        return false;
    }

    private bool FillContains(Point point)
    {
        switch (Kind)
        {
            case ShapeKind.Rectangle:
                return Bounds.Contains(point);
            case ShapeKind.Ellipse:
                return EllipseContains(Bounds, point);
            case ShapeKind.Line:
                return false;
            default:
                return Geometry?.FillContains(point) ?? false;
        }
    }

    private bool StrokeContains(Point point)
    {
        if (StrokeWidth <= 0)
            return false;

        var half = StrokeWidth / 2;
        switch (Kind)
        {
            case ShapeKind.Rectangle:
                return Bounds.Inflate(half).Contains(point) && !Bounds.Deflate(half).Contains(point);
            case ShapeKind.Ellipse:
                return EllipseContains(Bounds.Inflate(half), point) && !EllipseContains(Bounds.Deflate(half), point);
            case ShapeKind.Line:
                return SegmentDistanceSquared(point, P1, P2) <= half * half;
            default:
                return Geometry?.StrokeContains(new ImmutablePen(Brushes.Black, StrokeWidth), point) ?? false;
        }
    }

    private static double SegmentDistanceSquared(Point point, Point a, Point b)
    {
        var ab = b - a;
        var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        var t = lengthSquared > 0
            ? ((point.X - a.X) * ab.X + (point.Y - a.Y) * ab.Y) / lengthSquared
            : 0;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        var closest = a + ab * t;
        var d = point - closest;
        return d.X * d.X + d.Y * d.Y;
    }

    private static bool EllipseContains(Rect rect, Point point)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return false;
        var dx = (point.X - rect.Center.X) / (rect.Width / 2);
        var dy = (point.Y - rect.Center.Y) / (rect.Height / 2);
        return dx * dx + dy * dy <= 1;
    }
}

/// <summary>
/// A node of the SVG-side hit-test tree, built alongside the recording at
/// compile time. The recording stays free of element identity — per the plan,
/// element hit testing is entirely an SVG-layer concern.
/// </summary>
internal sealed class SvgHitNode
{
    /// <summary>The element this node represents; null for synthetic wrappers (use viewports).</summary>
    public SvgElement? Element;

    /// <summary>The node's local transform (the element's <c>transform</c>, viewBox or use placement).</summary>
    public Matrix Transform = Matrix.Identity;

    /// <summary>An axis-aligned local clip (symbol/use viewports).</summary>
    public Rect? ClipRect;

    /// <summary>A geometric local clip (<c>clip-path</c>).</summary>
    public Geometry? ClipGeometry;

    public List<SvgHitNode>? Children;

    public SvgHitShape? Shape;
    public SvgPointerEvents PointerEvents;
    public bool Visible = true;

    public void Add(SvgHitNode child) => (Children ??= new List<SvgHitNode>()).Add(child);

    /// <summary>
    /// Hit tests this subtree. <paramref name="chain"/> receives the SVG
    /// event-target chain, innermost first; later siblings (painted on top)
    /// win over earlier ones.
    /// </summary>
    public bool HitTest(Point point, List<SvgElement> chain)
    {
        if (!Transform.IsIdentity)
        {
            if (!Transform.TryInvert(out var inverse))
                return false;
            point = point.Transform(inverse);
        }

        if (ClipRect is { } clipRect && !clipRect.Contains(point))
            return false;
        if (ClipGeometry is { } clipGeometry && !clipGeometry.FillContains(point))
            return false;

        if (Children != null)
        {
            for (var i = Children.Count - 1; i >= 0; i--)
            {
                if (Children[i].HitTest(point, chain))
                {
                    if (Element != null)
                        chain.Add(Element);
                    return true;
                }
            }
        }

        if (Shape != null && Element != null && Shape.HitTest(point, PointerEvents, Visible))
        {
            chain.Add(Element);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Collects the hit-test tree during compilation; mirrors the compiler's
/// container pushes. Shared subtrees (use/symbol targets) are cached on the
/// document next to their recordings and wrapped per use site.
/// </summary>
internal sealed class SvgHitTreeBuilder
{
    private readonly Stack<SvgHitNode> _stack = new();

    public SvgHitTreeBuilder(Matrix rootTransform)
    {
        Root = new SvgHitNode { Transform = rootTransform };
        _stack.Push(Root);
    }

    public SvgHitNode Root { get; }

    private SvgHitNode Current => _stack.Peek();

    public SvgHitNode PushNode(SvgElement? element, Matrix transform, Rect? clipRect = null, Geometry? clipGeometry = null)
    {
        var node = new SvgHitNode
        {
            Element = element,
            Transform = transform,
            ClipRect = clipRect,
            ClipGeometry = clipGeometry,
        };
        Current.Add(node);
        _stack.Push(node);
        return node;
    }

    public void Pop() => _stack.Pop();

    public void AddShape(SvgElement element, SvgHitShape shape, SvgPointerEvents pointerEvents, bool visible)
    {
        // Prune leaves that can never hit, so the tree stays small and the
        // recording-based early-out stays valid for default content.
        if (pointerEvents == SvgPointerEvents.None)
            return;

        var requiresVisible = pointerEvents is SvgPointerEvents.VisiblePainted or SvgPointerEvents.Visible
            or SvgPointerEvents.VisibleFill or SvgPointerEvents.VisibleStroke;
        if (requiresVisible && !visible)
            return;

        var requiresPainted = pointerEvents is SvgPointerEvents.VisiblePainted or SvgPointerEvents.Painted;
        if (requiresPainted && !shape.HasFill && !shape.HasStroke)
            return;

        Current.Add(new SvgHitNode
        {
            Element = element,
            Shape = shape,
            PointerEvents = pointerEvents,
            Visible = visible,
        });
    }

    /// <summary>
    /// Attaches the cached shared hit subtree of a use/symbol target. The
    /// enclosing element node (pushed by the compiler for the <c>use</c> element)
    /// provides the element identity; the wrappers added here only mirror the
    /// placement, viewport clip and viewBox mapping the drawing side applies.
    /// </summary>
    public void AddUseSubtree(Matrix placement, Rect? clipRect, Matrix contentMatrix, SvgHitNode subtree)
    {
        var wrapper = new SvgHitNode
        {
            Transform = placement,
            ClipRect = clipRect,
        };

        if (!contentMatrix.IsIdentity)
        {
            var inner = new SvgHitNode { Transform = contentMatrix };
            inner.Add(subtree);
            wrapper.Add(inner);
        }
        else
        {
            wrapper.Add(subtree);
        }

        Current.Add(wrapper);
    }
}
