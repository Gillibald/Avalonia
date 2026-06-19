using System;
using System.Collections.Generic;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Animation;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Metadata;
using Avalonia.Rendering.Composition;

namespace Avalonia.Media;

/// <summary>
/// An <see cref="IImage"/> that renders an SVG document. The document is compiled
/// into an immutable <see cref="DrawingRecording"/> when it is assigned — through a
/// constructor or the <see cref="Document"/> content property — and replayed on
/// every draw. Reassigning <see cref="Document"/> recompiles and raises
/// <see cref="ICompositionImage.Invalidated"/> so any hosting control refreshes.
/// </summary>
public sealed class SvgImage : IImage, IDisposable, ICompositionImage
{
    private SvgDocument? _document;
    private bool _ownsDocument;
    private DrawingRecording? _recording;
    private SvgHitNode? _hitRoot;
    private bool _hitTestNeedsFullWalk;

    /// <summary>
    /// Creates an empty image. Assign <see cref="Document"/> — the content
    /// property — to compile a document into it; intended for XAML, where the
    /// document is supplied as inline content or via a <c>Document</c> binding.
    /// </summary>
    public SvgImage()
    {
    }

    /// <summary>
    /// Compiles <paramref name="document"/> into a recording sized to the
    /// document's intrinsic size.
    /// </summary>
    public SvgImage(SvgDocument document)
    {
        Initialize(document ?? throw new ArgumentNullException(nameof(document)),
            ownsDocument: false, compositor: null, paintAnimationTargets: null);
    }

    /// <summary>
    /// Compiles the document, optionally compositor-bound with mutable brushes
    /// for the animation paint channel; see <see cref="SvgCompileOptions"/>.
    /// </summary>
    internal SvgImage(
        SvgDocument document,
        Compositor? compositor,
        IReadOnlyCollection<(SvgElement Element, string Attribute)>? paintAnimationTargets)
    {
        Initialize(document ?? throw new ArgumentNullException(nameof(document)),
            ownsDocument: false, compositor, paintAnimationTargets);
    }

    /// <summary>
    /// The SVG document this image renders. Assigning it compiles the document
    /// into the replayed recording; this is the image's XAML content property, so
    /// both <c>&lt;SvgImage&gt;&lt;svg .../&gt;&lt;/SvgImage&gt;</c> and
    /// <c>&lt;SvgImage Document="{StaticResource Doc}"/&gt;</c> work. Reassigning it
    /// recompiles and raises <see cref="ICompositionImage.Invalidated"/> so hosts
    /// refresh, and assigning <c>null</c> clears the image. A document assigned here is not owned
    /// by the image (not disposed when replaced or when the image is disposed); a
    /// document loaded via <see cref="Load"/> is.
    /// </summary>
    [Content]
    public SvgDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
                return;

            // Hold the superseded compile (and the previous document, if this image
            // owned it) until the replacement is in place, then release it. The
            // static recording is immutable, so disposing it frees no in-flight
            // server state — the last committed frame holds its own copy.
            var previousRecording = _recording;
            var previousOwnedDocument = _ownsDocument ? _document : null;

            if (value is null)
                Clear();
            else
                Initialize(value, ownsDocument: false, compositor: null, paintAnimationTargets: null);

            previousRecording?.Dispose();
            previousOwnedDocument?.Dispose();

            _invalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    private EventHandler? _invalidated;

    /// <inheritdoc />
    event EventHandler? ICompositionImage.Invalidated
    {
        add => _invalidated += value;
        remove => _invalidated -= value;
    }

    /// <summary>
    /// Loads an SVG from <paramref name="uri"/> — an <c>avares://</c> resource or
    /// a local file — and wraps it in an image that owns the document, so
    /// disposing the image disposes the document too. A relative
    /// <paramref name="uri"/> is resolved against <paramref name="baseUri"/>.
    /// </summary>
    public static SvgImage Load(Uri uri, Uri? baseUri = null)
    {
        var image = new SvgImage();
        image.Initialize(
            SvgDocument.Load(SvgDocument.ResolveUri(uri, baseUri)),
            ownsDocument: true, compositor: null, paintAnimationTargets: null);
        return image;
    }

    private void Initialize(
        SvgDocument document,
        bool ownsDocument,
        Compositor? compositor,
        IReadOnlyCollection<(SvgElement Element, string Attribute)>? paintAnimationTargets)
    {
        _document = document;
        _ownsDocument = ownsDocument;

        Size = document.GetIntrinsicSize();
        var options = new SvgCompileOptions
        {
            BuildHitTree = true,
            PaintAnimationTargets = paintAnimationTargets,
        };

        void Compile(DrawingContext ctx) => SvgCompiler.CompileDocument(document, ctx, Size, options);

        var recording = compositor == null
            ? DrawingRecording.Create(Compile)
            : DrawingRecording.Create(compositor, Compile);
        _recording = recording;

        // Without any size hints (width, height or viewBox) the canvas takes
        // the content's extent; the CSS 300×150 default only feeds the
        // compilation viewport.
        if (!document.HasIntrinsicSizeHints && recording.Bounds is { Width: > 0, Height: > 0 } contentBounds)
            Size = new Size(Math.Max(0, contentBounds.Right), Math.Max(0, contentBounds.Bottom));

        _hitRoot = options.HitRoot;
        AnimatedBrushes = options.AnimatedBrushes;

        // The recording only contains painted content, so it can pre-filter hit
        // tests — unless pointer-events made unpainted or hidden geometry
        // interactive somewhere in the tree.
        _hitTestNeedsFullWalk = _hitRoot != null && RequiresFullWalk(_hitRoot);
    }

    // Resets to the empty (no-document) state; the caller disposes the superseded
    // recording and any owned document.
    private void Clear()
    {
        _document = null;
        _ownsDocument = false;
        _recording = null;
        _hitRoot = null;
        _hitTestNeedsFullWalk = false;
        AnimatedBrushes = null;
        Size = default;
    }

    /// <summary>
    /// The mutable brushes registered for the animation paint channel, keyed by
    /// (element, fill/stroke); null when the compile had no paint targets.
    /// </summary>
    internal Dictionary<(SvgElement Element, string Attribute), SolidColorBrush>? AnimatedBrushes { get; private set; }

    /// <inheritdoc/>
    public Size Size { get; private set; }

    /// <summary>
    /// The compiled recording; replayable directly via
    /// <see cref="DrawingContext.DrawRecording(DrawingRecording)"/>. Throws if no
    /// <see cref="Document"/> has been assigned yet.
    /// </summary>
    public DrawingRecording Recording
        => _recording ?? throw new InvalidOperationException("SvgImage has no Document.");

    /// <summary>
    /// The precise bounds of the drawn content in viewport space (the viewBox
    /// mapping is baked into the recording), backed by the recording's eager
    /// bounds; empty until a <see cref="Document"/> is assigned. Useful for tight
    /// layout measurement; <see cref="GetContentBounds"/> gives per-item-tight
    /// bounds under an additional transform.
    /// </summary>
    public Rect ContentBounds => _recording?.Bounds ?? default;

    /// <inheritdoc cref="ContentBounds"/>
    public Rect GetContentBounds(Matrix transform) => _recording?.GetBounds(transform) ?? default;

    /// <summary>
    /// Hit tests the document at a point in viewport coordinates (the same space
    /// as <see cref="Size"/>) and returns the SVG event-target chain, innermost
    /// element first — empty when nothing is hit. Respects each element's
    /// <c>pointer-events</c>, <c>visibility</c>, transforms and clips; between
    /// overlapping siblings the one painted on top wins.
    /// </summary>
    public IReadOnlyList<SvgElement> HitTestElements(Point point)
    {
        if (_hitRoot == null || _recording is not { IsDisposed: false })
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

    /// <summary>
    /// Overrides the local transform of hit-test nodes whose element the lookup
    /// supplies a matrix for, leaving the rest untouched. Used to fold a
    /// composition-animated element's current server transform into the hit tree
    /// so it hit tests where it is drawn. The change is in place on this image's
    /// hit tree, so callers re-apply it as the value changes.
    /// </summary>
    internal void ApplyHitTransformOverrides(Func<SvgElement, Matrix?> lookup)
    {
        if (_hitRoot != null)
            ApplyTransformOverride(_hitRoot, lookup);
    }

    private static void ApplyTransformOverride(SvgHitNode node, Func<SvgElement, Matrix?> lookup)
    {
        if (node.Element is { } element && lookup(element) is { } matrix)
            node.Transform = matrix;

        if (node.Children is { } children)
        {
            foreach (var child in children)
                ApplyTransformOverride(child, lookup);
        }
    }

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
        if (_recording is not { IsDisposed: false }
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

    /// <inheritdoc />
    ICompositionImageInstance? ICompositionImage.CreateInstance(Compositor compositor)
    {
        if (_document is not { } document)
            return null;

        var animator = SvgAnimator.TryCreate(document);
        if (animator is null)
            return null;

        if (SvgCompositionPartitioner.TryBuild(document, animator) is not { } rootGroup)
            return null;

        var state = new SvgAnimationState();
        var host = new SvgCompositionHost(
            document, compositor, animator, rootGroup, document.GetIntrinsicSize(), state);
        return new SvgCompositionInstance(document, host, animator, state);
    }

    private sealed class SvgCompositionInstance : ICompositionImageInstance, ISvgHitTestSource
    {
        private readonly SvgDocument _document;
        private readonly SvgCompositionHost _host;
        private readonly SvgAnimator _animator;
        private readonly SvgAnimationState _state;

        // A hit-test image compiled from the current structural overrides,
        // rebuilt only after a structural tick changes geometry.
        private SvgImage? _hitImage;
        private int _structuralRevision;
        private int _hitImageRevision = -1;

        public SvgCompositionInstance(
            SvgDocument document, SvgCompositionHost host, SvgAnimator animator, SvgAnimationState state)
        {
            _document = document;
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
            {
                _host.RecompileStructural();
                _structuralRevision++; // the cached hit image is now stale
            }
        }

        public IReadOnlyList<SvgElement> HitTest(Point point)
        {
            // Reuse the cached hit image until a structural tick changes
            // geometry; the per-instance state already holds the current
            // structural overrides, materialized for the synchronous compile.
            // (Composition transforms run server-side and are not in the state,
            // so those elements hit test at their base transform — see
            // ISvgHitTestSource.)
            if (_hitImage == null || _hitImageRevision != _structuralRevision)
            {
                _hitImage?.Dispose();
                using (_state.Materialize())
                    _hitImage = new SvgImage(_document);
                _hitImageRevision = _structuralRevision;
            }

            // Transform/opacity timelines run server-side, so their current value
            // is not in the state; read each animated visual's transform back from
            // the compositor and fold it into the hit tree. Re-applied every call
            // since the server animates continuously.
            if (_host.HasServerAnimations)
                _hitImage.ApplyHitTransformOverrides(
                    element => _host.TryGetServerTransform(element, out var transform) ? transform : null);

            return _hitImage.HitTestElements(point);
        }

        public void Dispose()
        {
            _hitImage?.Dispose();
            _host.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _recording?.Dispose();
        if (_ownsDocument)
            _document?.Dispose();
    }
}
