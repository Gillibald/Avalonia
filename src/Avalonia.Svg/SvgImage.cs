using System;
using System.Collections.Generic;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Animation;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Rendering.Composition;

namespace Avalonia.Media;

/// <summary>
/// An <see cref="IImage"/> that renders an SVG document. The document is compiled
/// once into an immutable <see cref="DrawingRecording"/> at construction time and
/// replayed on every draw.
/// </summary>
public sealed class SvgImage : IImage, IDisposable, ICompositionImage
{
    private readonly SvgDocument _document;
    private readonly bool _ownsDocument;
    private readonly DrawingRecording _recording;
    private readonly SvgHitNode? _hitRoot;
    private readonly bool _hitTestNeedsFullWalk;

    /// <summary>
    /// Compiles <paramref name="document"/> into a recording sized to the
    /// document's intrinsic size.
    /// </summary>
    public SvgImage(SvgDocument document)
        : this(document, ownsDocument: false, compositor: null, paintAnimationTargets: null)
    {
    }

    /// <summary>
    /// Compiles the document, optionally compositor-bound with mutable brushes
    /// for the animation paint channel; see <see cref="SvgCompileOptions"/>.
    /// </summary>
    internal SvgImage(
        SvgDocument document,
        Compositor? compositor,
        IReadOnlyCollection<(SvgElement Element, string Attribute)>? paintAnimationTargets)
        : this(document, ownsDocument: false, compositor, paintAnimationTargets)
    {
    }

    private SvgImage(
        SvgDocument document,
        bool ownsDocument,
        Compositor? compositor,
        IReadOnlyCollection<(SvgElement Element, string Attribute)>? paintAnimationTargets)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _ownsDocument = ownsDocument;

        Size = document.GetIntrinsicSize();
        var options = new SvgCompileOptions
        {
            BuildHitTree = true,
            PaintAnimationTargets = paintAnimationTargets,
        };

        void Compile(DrawingContext ctx) => SvgCompiler.CompileDocument(document, ctx, Size, options);

        _recording = compositor == null
            ? DrawingRecording.Create(Compile)
            : DrawingRecording.Create(compositor, Compile);

        // Without any size hints (width, height or viewBox) the canvas takes
        // the content's extent; the CSS 300×150 default only feeds the
        // compilation viewport.
        if (!document.HasIntrinsicSizeHints && _recording.Bounds is { Width: > 0, Height: > 0 } contentBounds)
            Size = new Size(Math.Max(0, contentBounds.Right), Math.Max(0, contentBounds.Bottom));

        _hitRoot = options.HitRoot;
        AnimatedBrushes = options.AnimatedBrushes;

        // The recording only contains painted content, so it can pre-filter hit
        // tests — unless pointer-events made unpainted or hidden geometry
        // interactive somewhere in the tree.
        _hitTestNeedsFullWalk = _hitRoot != null && RequiresFullWalk(_hitRoot);
    }

    /// <summary>
    /// Loads an SVG from <paramref name="uri"/> — an <c>avares://</c> resource or
    /// a local file — and wraps it in an image that owns the document, so
    /// disposing the image disposes the document too. A relative
    /// <paramref name="uri"/> is resolved against <paramref name="baseUri"/>.
    /// </summary>
    public static SvgImage Load(Uri uri, Uri? baseUri = null)
        => new(
            SvgDocument.Load(SvgDocument.ResolveUri(uri, baseUri)),
            ownsDocument: true, compositor: null, paintAnimationTargets: null);

    /// <summary>
    /// The mutable brushes registered for the animation paint channel, keyed by
    /// (element, fill/stroke); null when the compile had no paint targets.
    /// </summary>
    internal Dictionary<(SvgElement Element, string Attribute), SolidColorBrush>? AnimatedBrushes { get; }

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

    /// <summary>
    /// Creates a render-thread animating visual subtree for this document, or
    /// null when it has no animations the composition channel hosts (the caller
    /// renders statically via <see cref="Draw"/>). Each call builds an
    /// independent instance — see <see cref="ICompositionImage"/>.
    /// </summary>
    public ICompositionImageInstance? CreateInstance(Compositor compositor)
    {
        var animator = SvgAnimator.TryCreate(_document);
        if (animator is null)
            return null;

        if (SvgCompositionPartitioner.TryBuild(_document, animator) is not { } rootGroup)
            return null;

        var state = new SvgAnimationState();
        var host = new SvgCompositionHost(
            _document, compositor, animator, rootGroup, _document.GetIntrinsicSize(), state);
        return new SvgCompositionInstance(host, animator, state);
    }

    private sealed class SvgCompositionInstance : ICompositionImageInstance
    {
        private readonly SvgCompositionHost _host;
        private readonly SvgAnimator _animator;
        private readonly SvgAnimationState _state;

        public SvgCompositionInstance(SvgCompositionHost host, SvgAnimator animator, SvgAnimationState state)
        {
            _host = host;
            _animator = animator;
            _state = state;
        }

        public CompositionVisual Visual => _host.RootVisual;

        public void SetStretchTransform(Matrix transform) => _host.UpdateStretch(transform);

        public bool NeedsClock => _animator.HasUnclaimedWork;

        public void OnClock(TimeSpan elapsed)
        {
            // Paint entries mutate their compositor brushes inside Apply; a
            // structural change re-compiles the affected slices into their
            // visuals, which the compositor repaints on its own.
            if (_animator.Apply(elapsed, _state))
                _host.RecompileStructural();
        }

        public void Dispose() => _host.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _recording.Dispose();
        if (_ownsDocument)
            _document.Dispose();
    }
}
