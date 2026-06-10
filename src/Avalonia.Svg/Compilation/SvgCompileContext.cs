using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

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
    /// Gets (or compiles and caches) the shared recording for non-interactive
    /// referenced content (markers, pattern tiles, mask content). The content is
    /// compiled once with the default style context — use-site style inheritance
    /// into unstyled referenced content is not propagated (the recording is
    /// shared between all reference sites).
    /// </summary>
    public DrawingRecording GetSharedRecording(SvgElement target, out DrawingRecordingOwnership ownership)
    {
        // Measuring-time compilation skips decorations and compositing; such a
        // recording must never enter the document cache. Hand the caller an
        // owned throwaway instead — the enclosing measuring recording disposes it.
        if (Measuring)
        {
            ownership = DrawingRecordingOwnership.Owned;
            return DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
        }

        ownership = DrawingRecordingOwnership.Shared;
        if (Document.TryGetSharedRecording(target, Viewport, out var recording))
            return recording;

        recording = DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
        Document.AddSharedRecording(target, Viewport, recording);
        return recording;
    }

    /// <summary>
    /// Gets the shared recording for a <c>&lt;use&gt;</c> target, together with
    /// the target's hit subtree when this compilation builds a hit-test tree.
    /// The subtree is cached next to the recording and reused (as a shared node)
    /// by every use site.
    /// </summary>
    public DrawingRecording GetSharedRecording(
        SvgElement target, out DrawingRecordingOwnership ownership, out SvgHitNode? hitSubtree)
    {
        if (Measuring)
        {
            hitSubtree = null;
            ownership = DrawingRecordingOwnership.Owned;
            return DrawingRecording.Create(ctx => CompileSharedContent(target, ctx, hitTree: null));
        }

        ownership = DrawingRecordingOwnership.Shared;
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

    private void CompileSharedContent(SvgElement target, DrawingContext ctx, SvgHitTreeBuilder? hitTree)
    {
        var previousHitTree = _hitTree;
        _hitTree = hitTree;
        try
        {
            var style = SvgStyle.CreateDefault(Viewport);
            style.RootFontSize = RootFontSize;

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
