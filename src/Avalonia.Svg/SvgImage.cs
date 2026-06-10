using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Svg.Compilation;

namespace Avalonia.Svg;

/// <summary>
/// An <see cref="IImage"/> that renders an SVG document. The document is compiled
/// once into an immutable <see cref="DrawingRecording"/> at construction time and
/// replayed on every draw.
/// </summary>
public sealed class SvgImage : IImage, IDisposable
{
    private readonly DrawingRecording _recording;
    private readonly SvgHitNode? _hitRoot;
    private readonly bool _hitTestNeedsFullWalk;

    /// <summary>
    /// Compiles <paramref name="document"/> into a recording sized to the
    /// document's intrinsic size.
    /// </summary>
    public SvgImage(SvgDocument document)
    {
        _ = document ?? throw new ArgumentNullException(nameof(document));

        Size = document.GetIntrinsicSize();
        SvgHitNode? hitRoot = null;
        _recording = DrawingRecording.Create(
            ctx => hitRoot = SvgCompiler.CompileDocumentWithHitTree(document, ctx, Size));
        _hitRoot = hitRoot;

        // The recording only contains painted content, so it can pre-filter hit
        // tests — unless pointer-events made unpainted or hidden geometry
        // interactive somewhere in the tree.
        _hitTestNeedsFullWalk = hitRoot != null && RequiresFullWalk(hitRoot);
    }

    /// <inheritdoc/>
    public Size Size { get; }

    /// <summary>The compiled recording; replayable directly via <see cref="DrawingContext.DrawRecording(DrawingRecording)"/>.</summary>
    public DrawingRecording Recording => _recording;

    /// <summary>
    /// The precise bounds of the drawn content in viewport space (the viewBox
    /// mapping is baked into the recording), backed by the recording's eager
    /// bounds. Useful for tight layout measurement; <see cref="GetContentBounds"/>
    /// gives per-item-tight bounds under an additional transform.
    /// </summary>
    public Rect ContentBounds => _recording.Bounds;

    /// <inheritdoc cref="ContentBounds"/>
    public Rect GetContentBounds(Matrix transform) => _recording.GetBounds(transform);

    /// <summary>
    /// Hit tests the document at a point in viewport coordinates (the same space
    /// as <see cref="Size"/>) and returns the SVG event-target chain, innermost
    /// element first — empty when nothing is hit. Respects each element's
    /// <c>pointer-events</c>, <c>visibility</c>, transforms and clips; between
    /// overlapping siblings the one painted on top wins.
    /// </summary>
    public IReadOnlyList<SvgElement> HitTestElements(Point point)
    {
        if (_hitRoot == null || _recording.IsDisposed)
            return Array.Empty<SvgElement>();

        // Painted-content pre-filter: cheap recording-side rejection for
        // documents whose interactivity matches what is painted.
        if (!_hitTestNeedsFullWalk && !_recording.HitTest(point))
            return Array.Empty<SvgElement>();

        var chain = new List<SvgElement>();
        _hitRoot.HitTest(point, chain);
        return chain;
    }

    /// <summary>
    /// Returns true when any interactive element is hit at the given viewport
    /// point — equivalent to <see cref="HitTestElements"/> returning a non-empty
    /// chain.
    /// </summary>
    public bool HitTest(Point point) => HitTestElements(point).Count > 0;

    private static bool RequiresFullWalk(SvgHitNode node)
    {
        if (node.Shape != null && node.PointerEvents != SvgPointerEvents.VisiblePainted)
            return true;

        if (node.Children is { } children)
        {
            foreach (var child in children)
            {
                if (RequiresFullWalk(child))
                    return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (_recording.IsDisposed
            || sourceRect.Width <= 0 || sourceRect.Height <= 0
            || destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        var transform =
            Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y)
            * Matrix.CreateScale(destRect.Width / sourceRect.Width, destRect.Height / sourceRect.Height)
            * Matrix.CreateTranslation(destRect.X, destRect.Y);

        using (context.PushClip(destRect))
            context.DrawRecording(_recording, transform);
    }

    /// <inheritdoc/>
    public void Dispose() => _recording.Dispose();
}
