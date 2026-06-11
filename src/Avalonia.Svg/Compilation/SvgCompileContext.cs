using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Per-compilation state: the document being compiled, the viewport, and the
/// cycle guard for <c>&lt;use&gt;</c> recursion. Shared sub-recordings are cached
/// on the <see cref="SvgDocument"/> so they are reused across compilations and
/// released by <see cref="SvgDocument.Dispose"/>.
/// </summary>
internal sealed class SvgCompileContext
{
    private HashSet<SvgElement>? _useStack;
    private HashSet<SvgElement>? _sharedStack;
    private HashSet<SvgElement>? _clipPathStack;
    private SvgHitTreeBuilder? _hitTree;

    public SvgCompileContext(SvgDocument document, Size viewport)
    {
        Document = document;
        Viewport = viewport;
    }

    public SvgDocument Document { get; }

    public Size Viewport { get; }

    /// <summary>
    /// True while compiling a throwaway recording to measure an element's fill
    /// box: masks, clips, markers and compositing layers are skipped, since the
    /// objectBoundingBox is the undecorated geometry box.
    /// </summary>
    public bool Measuring { get; set; }

    /// <summary>The document root's computed font size; the reference for <c>rem</c>.</summary>
    public double RootFontSize { get; set; } = 16;

    /// <summary>
    /// The hit-test tree collector, when the compilation builds one. Suspended
    /// while measuring (throwaway recordings must not contribute hit nodes) and
    /// while compiling non-interactive shared content (markers, patterns, masks).
    /// </summary>
    public SvgHitTreeBuilder? HitTree => Measuring ? null : _hitTree;

    public void SetHitTreeBuilder(SvgHitTreeBuilder? builder) => _hitTree = builder;

    /// <summary>
    /// (element, fill/stroke) pairs compiled as mutable brushes for the
    /// animation paint channel; see <see cref="SvgCompileOptions"/>.
    /// </summary>
    public IReadOnlyCollection<(SvgElement Element, string Attribute)>? PaintAnimationTargets { get; set; }

    /// <summary>The mutable brushes registered during compilation, keyed like the targets.</summary>
    public Dictionary<(SvgElement Element, string Attribute), SolidColorBrush>? AnimatedBrushes { get; private set; }

    /// <summary>
    /// Returns (creating and registering on first use) the mutable brush for an
    /// animated fill/stroke, seeded from the statically resolved brush. Returns
    /// null when the pair is not an animation target. Measuring passes and
    /// shared content keep their immutable paints — the same element compiles
    /// into multiple recordings, but only the main tree is animated.
    /// </summary>
    public SolidColorBrush? TryGetAnimatedBrush(SvgElement element, string attribute, IBrush? resolved)
    {
        if (Measuring
            || PaintAnimationTargets == null
            || !PaintAnimationTargets.Contains((element, attribute)))
        {
            return null;
        }

        AnimatedBrushes ??= new Dictionary<(SvgElement, string), SolidColorBrush>();
        if (!AnimatedBrushes.TryGetValue((element, attribute), out var brush))
        {
            brush = resolved is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color) { Opacity = solid.Opacity }
                : new SolidColorBrush(Colors.Transparent);
            AnimatedBrushes.Add((element, attribute), brush);
        }

        return brush;
    }

    /// <summary>
    /// Guards against reference cycles while expanding <c>&lt;use&gt;</c>.
    /// Returns false when <paramref name="target"/> is already being expanded.
    /// </summary>
    public bool EnterUse(SvgElement target)
    {
        _useStack ??= new HashSet<SvgElement>();
        return _useStack.Add(target);
    }

    public void ExitUse(SvgElement target) => _useStack?.Remove(target);

    /// <summary>
    /// Tracks the <c>&lt;clipPath&gt;</c> elements currently being built so a
    /// recursive <c>clip-path</c> reference can be ignored, breaking the cycle
    /// one level in.
    /// </summary>
    public bool EnterClipPath(SvgElement clipPath)
    {
        _clipPathStack ??= new HashSet<SvgElement>();
        return _clipPathStack.Add(clipPath);
    }

    public void ExitClipPath(SvgElement clipPath) => _clipPathStack?.Remove(clipPath);

    public bool IsBuildingClipPath(SvgElement clipPath) => _clipPathStack?.Contains(clipPath) == true;

    /// <summary>
    /// Gets (or compiles and caches) the shared recording for non-interactive
    /// referenced content (markers, pattern tiles, mask content). The content is
    /// compiled once with the default style context — use-site style inheritance
    /// into unstyled referenced content is not propagated (the recording is
    /// shared between all reference sites).
    /// Returns null for circular references (e.g. a marker whose content is
    /// marked with itself): the cache only fills after the compile returns, so
    /// re-entrant requests would otherwise recurse without bound. Callers treat
    /// null as an invalid reference and ignore it, per the error-handling rules.
    /// </summary>
    public DrawingRecording? GetSharedRecording(SvgElement target, out DrawingRecordingOwnership ownership)
    {
        ownership = DrawingRecordingOwnership.Shared;
        if (!EnterShared(target))
            return null;

        try
        {
            // Measuring-time compilation skips decorations and compositing; such a
            // recording must never enter the document cache. Hand the caller an
            // owned throwaway instead — the enclosing measuring recording disposes it.
            if (Measuring)
            {
                ownership = DrawingRecordingOwnership.Owned;
                return DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
            }

            if (Document.TryGetSharedRecording(target, Viewport, out var recording))
                return recording;

            recording = DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
            Document.AddSharedRecording(target, Viewport, recording);
            return recording;
        }
        finally
        {
            ExitShared(target);
        }
    }

    /// <summary>
    /// Gets the shared recording for a <c>&lt;use&gt;</c> target, together with
    /// the target's hit subtree when this compilation builds a hit-test tree.
    /// The subtree is cached next to the recording and reused (as a shared node)
    /// by every use site. Returns null for circular references.
    /// </summary>
    public DrawingRecording? GetSharedRecording(
        SvgElement target, out DrawingRecordingOwnership ownership, out SvgHitNode? hitSubtree)
    {
        hitSubtree = null;
        ownership = DrawingRecordingOwnership.Shared;
        if (!EnterShared(target))
            return null;

        try
        {
            if (Measuring)
            {
                ownership = DrawingRecordingOwnership.Owned;
                return DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
            }

            var needHits = _hitTree != null;

            if (!Document.TryGetSharedRecording(target, Viewport, out var recording))
            {
                var builder = needHits ? new SvgHitTreeBuilder(Matrix.Identity) : null;
                recording = DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, builder));
                Document.AddSharedRecording(target, Viewport, recording);

                if (builder != null)
                    Document.AddSharedHitSubtree(target, Viewport, builder.Root);

                hitSubtree = builder?.Root;
                return recording;
            }

            SvgHitNode? subtree = null;
            if (needHits && !Document.TryGetSharedHitSubtree(target, Viewport, out subtree!))
            {
                // The recording was cached by a compilation that did not build hit
                // information (e.g. nested inside pattern or mask content). Rebuild
                // just the hit subtree through a throwaway recording.
                var builder = new SvgHitTreeBuilder(Matrix.Identity);
                DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, builder)).Dispose();
                subtree = builder.Root;
                Document.AddSharedHitSubtree(target, Viewport, subtree);
            }

            hitSubtree = subtree;
            return recording;
        }
        finally
        {
            ExitShared(target);
        }
    }

    /// <summary>
    /// True when the target's subtree consumes <c>context-fill</c> or
    /// <c>context-stroke</c> — directly or through nested <c>use</c>
    /// references. Such content cannot share a recording between reference
    /// sites: the context paints differ per site.
    /// </summary>
    public bool UsesContextPaint(SvgElement target)
    {
        return Walk(target, null);

        bool Walk(SvgElement element, HashSet<SvgElement>? visited)
        {
            if (element.GetStyleOrAttribute("fill") is { } fill && fill.Contains("context-"))
                return true;
            if (element.GetStyleOrAttribute("stroke") is { } stroke && stroke.Contains("context-"))
                return true;

            if (element.Name == "use"
                && element.Href is { Length: > 1 } href && href[0] == '#'
                && Document.GetElementById(href.Substring(1)) is { } useTarget)
            {
                visited ??= new HashSet<SvgElement>();
                if (visited.Add(element) && Walk(useTarget, visited))
                    return true;
            }

            foreach (var child in element.Children)
            {
                if (Walk(child, visited))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Compiles referenced content with site-specific context paints. The
    /// recording is always owned (never cached): every site resolves the
    /// context paints differently. Returns null for circular references.
    /// </summary>
    public DrawingRecording? GetContextRecording(
        SvgElement target, in SvgPaint contextFill, in SvgPaint contextStroke, Rect contextBounds,
        out DrawingRecordingOwnership ownership, out SvgHitNode? hitSubtree)
    {
        hitSubtree = null;
        ownership = DrawingRecordingOwnership.Owned;
        if (!EnterShared(target))
            return null;

        try
        {
            var builder = _hitTree != null && !Measuring ? new SvgHitTreeBuilder(Matrix.Identity) : null;
            var fill = contextFill;
            var stroke = contextStroke;
            var recording = DrawingRecording.Create(
                ctx => CompileSharedContent(target, ctx, builder, fill, stroke, contextBounds));
            hitSubtree = builder?.Root;
            return recording;
        }
        finally
        {
            ExitShared(target);
        }
    }

    /// <summary>
    /// Guards shared-content compilation against reference cycles across all
    /// shared kinds (markers, patterns, masks, use targets) — including cycles
    /// that cross kinds, e.g. a pattern whose tile is marked with a marker that
    /// fills with that pattern.
    /// </summary>
    private bool EnterShared(SvgElement target)
    {
        _sharedStack ??= new HashSet<SvgElement>();
        return _sharedStack.Add(target);
    }

    private void ExitShared(SvgElement target) => _sharedStack?.Remove(target);

    private void CompileSharedContent(
        SvgElement target, DrawingContext ctx, SvgHitTreeBuilder? hitTree,
        in SvgPaint contextFill = default, in SvgPaint contextStroke = default,
        Rect contextBounds = default)
    {
        var previousHitTree = _hitTree;
        _hitTree = hitTree;
        try
        {
            var style = SvgStyle.CreateDefault(Viewport);
            style.RootFontSize = RootFontSize;
            style.ContextFill = contextFill;
            style.ContextStroke = contextStroke;
            style.ContextBounds = contextBounds;

            if (target.Name is "symbol" or "svg" or "marker" or "pattern" or "mask")
            {
                // Viewport-establishing containers compile their children only —
                // the viewport mapping (viewBox, width/height, ref points, tile
                // rects, mask regions) is applied at each reference site so the
                // recording itself stays shareable.
                style.Apply(target);
                foreach (var child in target.Children)
                    SvgCompiler.CompileElement(child, ctx, this, style);
            }
            else
            {
                SvgCompiler.CompileElement(target, ctx, this, style);
            }
        }
        finally
        {
            _hitTree = previousHitTree;
        }
    }
}
