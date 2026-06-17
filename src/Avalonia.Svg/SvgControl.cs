using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Animation;

namespace Avalonia.Controls;

/// <summary>
/// Displays an SVG document and routes pointer events to the SVG elements under
/// the pointer. The document compiles once into a retained
/// <see cref="Rendering.Composition.DrawingRecording"/> (via <see cref="SvgImage"/>);
/// rendering replays the recording under the control's stretch transform.
/// </summary>
public class SvgControl : Control
{
    /// <summary>
    /// Defines the <see cref="Source"/> property.
    /// </summary>
    public static readonly StyledProperty<SvgDocument?> SourceProperty =
        AvaloniaProperty.Register<SvgControl, SvgDocument?>(nameof(Source));

    /// <summary>
    /// Defines the <see cref="Stretch"/> property.
    /// </summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<SvgControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>
    /// Defines the <see cref="StretchDirection"/> property.
    /// </summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<SvgControl, StretchDirection>(nameof(StretchDirection), StretchDirection.Both);

    private SvgImage? _image;
    private SvgAnimator? _animator;
    private bool _animatorInitialized;
    private bool _animationRunning;
    private Action<TimeSpan>? _animationFrame;
    private TimeSpan? _animationStart;
    private SvgCompositionHost? _compositionHost;
    private bool _childVisualAttached;

    /// <summary>
    /// Opt-in for the experimental animation composition channel: SMIL
    /// transform/opacity timelines run as server-side composition key-frame
    /// animations on sliced child visuals instead of UI-thread ticks, so they
    /// keep playing while the UI thread is busy. Paint and structural
    /// timelines still tick on the UI thread. Opt-in because composition
    /// animations follow the compositor clock rather than the control's SMIL
    /// clock, which changes timing semantics for code that drives or observes
    /// document time.
    /// </summary>
    public static bool EnableExperimentalCompositionAnimations { get; set; }

    static SvgControl()
    {
        AffectsRender<SvgControl>(SourceProperty, StretchProperty, StretchDirectionProperty);
        AffectsMeasure<SvgControl>(SourceProperty, StretchProperty, StretchDirectionProperty);
    }

    /// <summary>
    /// Gets or sets the document to display. This is the control's content
    /// property, and XAML converts strings to documents: SVG markup — pasted
    /// directly as a nested <c>&lt;svg&gt;</c> element (no CDATA wrapper needed)
    /// or as CDATA content of the control — is validated, minified and turned
    /// into a document factory at compile time, while URI strings
    /// (<c>avares://</c> resources or files, relative against the XAML base
    /// URI) load through <see cref="SvgDocumentTypeConverter"/>. Documents
    /// created by XAML are disposed by the control when replaced; documents
    /// assigned from code stay caller-owned.
    /// </summary>
    [Content]
    public SvgDocument? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// Gets or sets a value controlling how the document is stretched to the
    /// control's bounds.
    /// </summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>
    /// Gets or sets a value controlling in what direction the document is stretched.
    /// </summary>
    public StretchDirection StretchDirection
    {
        get => GetValue(StretchDirectionProperty);
        set => SetValue(StretchDirectionProperty, value);
    }

    /// <summary>
    /// The compiled image currently displayed; null until a document or source
    /// is set (or when loading failed). Compiles lazily on first access.
    /// </summary>
    public SvgImage? CompiledImage => EnsureImage();

    /// <summary>
    /// Raised when the pointer is pressed over an interactive SVG element. The
    /// hit test honors <c>pointer-events</c> and <c>visibility</c>; the event is
    /// only raised when at least one element is hit.
    /// </summary>
    public event EventHandler<SvgElementPointerEventArgs>? ElementPointerPressed;

    /// <summary>
    /// Raised when the pointer is released over an interactive SVG element.
    /// </summary>
    public event EventHandler<SvgElementPointerEventArgs>? ElementPointerReleased;

    /// <summary>
    /// Raised when the pointer moves over an interactive SVG element.
    /// </summary>
    public event EventHandler<SvgElementPointerEventArgs>? ElementPointerMoved;

    /// <summary>
    /// Hit tests the document at a point in control coordinates and returns the
    /// SVG event-target chain, innermost element first — empty when nothing
    /// interactive is under the point.
    /// </summary>
    public IReadOnlyList<SvgElement> HitTestElements(Point point)
    {
        if (EnsureImage() is not { } image || !TryMapToViewport(image, point, out var viewportPoint))
            return Array.Empty<SvgElement>();

        return image.HitTestElements(viewportPoint);
    }

    /// <inheritdoc/>
    public sealed override void Render(DrawingContext context)
    {
        if (EnsureImage() is not { } image || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        ComputeStretchRects(image, out var sourceRect, out var destRect, out var scale);
        if (destRect.Width <= 0 || destRect.Height <= 0)
            return;

        if (_compositionHost != null)
        {
            // The document renders through the child composition visual; the
            // control itself only contributes a hit-testable surface and the
            // stretch mapping. Attaching the child visual invalidates, so it
            // never happens inside the render pass.
            _compositionHost.UpdateStretch(
                Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y)
                * Matrix.CreateScale(scale.X, scale.Y)
                * Matrix.CreateTranslation(destRect.X, destRect.Y));

            if (!_childVisualAttached)
                Threading.Dispatcher.UIThread.Post(AttachChildVisual, Threading.DispatcherPriority.Render);

            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            return;
        }

        context.DrawImage(image, sourceRect, destRect);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (EnsureImage() is not { } image)
            return default;

        // The layout pass is the safe place to attach the child visual; the
        // render pass must not invalidate.
        AttachChildVisual();
        return Stretch.CalculateSize(availableSize, image.Size, StretchDirection);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) =>
        EnsureImage() is { } image
            ? Stretch.CalculateSize(finalSize, image.Size)
            : default;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            ReleaseImage();

            // Documents created by XAML (inline markup or source strings)
            // belong to the control; user-assigned documents stay theirs.
            if (change.OldValue is SvgDocument { HostOwned: true } replaced)
                replaced.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        RaiseElementEvent(ElementPointerPressed, e);
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        RaiseElementEvent(ElementPointerReleased, e);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        RaiseElementEvent(ElementPointerMoved, e);
    }

    private void RaiseElementEvent(EventHandler<SvgElementPointerEventArgs>? handler, PointerEventArgs e)
    {
        if (handler == null)
            return;

        var elements = HitTestElements(e.GetPosition(this));
        if (elements.Count > 0)
            handler(this, new SvgElementPointerEventArgs(elements, e));
    }

    private SvgImage? EnsureImage()
    {
        if (_image != null)
            return _image;

        var document = Source;
        if (document == null || document.IsDisposed)
            return null;

        if (!_animatorInitialized)
        {
            _animatorInitialized = true;
            _animator = SvgAnimator.TryCreate(document);
        }

        // The composition channel hosts the document as a sliced visual tree:
        // transform/opacity timelines run as server key-frame animations and
        // only the structural remainder (if any) keeps ticking. The control's
        // own image then serves measurement and hit testing.
        if (EnableExperimentalCompositionAnimations
            && _animator != null && _compositionHost == null && GetCompositor() is { } hostCompositor
            && SvgCompositionPartitioner.TryBuild(document, _animator) is { } rootGroup)
        {
            _compositionHost = new SvgCompositionHost(
                document, hostCompositor, _animator, rootGroup, document.GetIntrinsicSize());
        }

        // Paint-only animations compile once into a compositor-bound recording
        // with mutable brushes; the ticks then propagate through the
        // compositor's change tracking without ever re-compiling. Everything
        // else (or no compositor yet) compiles immutable; structural ticks
        // re-compile against the document's cached shared sub-recordings.
        if (_compositionHost != null)
        {
            _image = new SvgImage(document);
        }
        else if (_animator is { HasStructural: false } paintAnimator && GetCompositor() is { } compositor)
        {
            _image = new SvgImage(document, compositor, paintAnimator.GetPaintTargets());
            paintAnimator.BindPaintBrushes(_image.AnimatedBrushes);
        }
        else
        {
            _image = new SvgImage(document);
        }

        // Compositor-bound (animated) recordings report bounds changes; static
        // immutable recordings never do (and throw on subscription).
        if (_image.Recording.Compositor != null)
            _image.Recording.BoundsChanged += OnRecordingBoundsChanged;

        TryStartAnimation();
        return _image;
    }

    private Compositor? GetCompositor() =>
        VisualRoot is { } root ? ElementComposition.GetElementVisual(root)?.Compositor : null;

    private void OnRecordingBoundsChanged(object? sender, Rect bounds) => InvalidateMeasure();

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // An animated image compiled before a compositor was available could
        // not use the composition or paint channels; recompile now.
        if (_animator != null && _compositionHost == null && _image != null && GetCompositor() != null
            && (_animator.HasStructural || _image.Recording.Compositor == null))
        {
            DisposeImage();
        }

        AttachChildVisual();
        TryStartAnimation();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachChildVisual();
        StopAnimation();
    }

    private void AttachChildVisual()
    {
        if (_compositionHost == null || _childVisualAttached || VisualRoot == null)
            return;

        ElementComposition.SetElementChildVisual(this, _compositionHost.RootVisual);
        _childVisualAttached = true;
    }

    private void DetachChildVisual()
    {
        if (!_childVisualAttached)
            return;

        ElementComposition.SetElementChildVisual(this, null);
        _childVisualAttached = false;
    }

    private void TryStartAnimation()
    {
        if (_animator == null || !_animator.HasUnclaimedWork || _animationRunning
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _animationRunning = true;
        _animationFrame ??= OnAnimationTick;
        topLevel.RequestAnimationFrame(_animationFrame);
    }

    private void StopAnimation()
    {
        // The pending animation-frame callback checks the flag and stops
        // re-requesting itself.
        _animationRunning = false;
        _animationStart = null;
    }

    private void OnAnimationTick(TimeSpan time)
    {
        if (!_animationRunning || _animator == null)
            return;

        // The first tick anchors the document timeline.
        _animationStart ??= time;

        if (_animator.Apply(time - _animationStart.Value))
        {
            if (_compositionHost != null)
            {
                // Only the structural slices re-compile; static and
                // composition slices replay untouched.
                _compositionHost.RecompileStructural();
            }
            else
            {
                // A structural value changed: re-compile the root recording. Shared
                // sub-recordings (symbols, markers, patterns) stay cached on the
                // document and are replayed, not rebuilt; the previous root keeps
                // its shared children alive until the rendered frame releases it.
                DisposeImage();
                InvalidateVisual();
            }
        }

        if (TopLevel.GetTopLevel(this) is { } topLevel)
            topLevel.RequestAnimationFrame(_animationFrame!);
        else
            _animationRunning = false;
    }

    private void DisposeImage()
    {
        if (_image == null)
            return;

        if (_image.Recording.Compositor != null)
            _image.Recording.BoundsChanged -= OnRecordingBoundsChanged;

        _image.Dispose();
        _image = null;
    }

    private void ReleaseImage()
    {
        StopAnimation();
        DetachChildVisual();
        _compositionHost?.Dispose();
        _compositionHost = null;
        DisposeImage();

        _animator = null;
        _animatorInitialized = false;
    }

    private void ComputeStretchRects(SvgImage image, out Rect sourceRect, out Rect destRect, out Vector scale)
    {
        var viewPort = new Rect(Bounds.Size);
        var sourceSize = image.Size;

        scale = Stretch.CalculateScaling(Bounds.Size, sourceSize, StretchDirection);
        var scaledSize = sourceSize * scale;

        destRect = viewPort
            .CenterRect(new Rect(scaledSize))
            .Intersect(viewPort);
        sourceRect = scale.X > 0 && scale.Y > 0
            ? new Rect(sourceSize).CenterRect(new Rect(destRect.Size / scale))
            : default;
    }

    private bool TryMapToViewport(SvgImage image, Point point, out Point viewportPoint)
    {
        viewportPoint = default;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return false;

        ComputeStretchRects(image, out var sourceRect, out var destRect, out var scale);
        if (scale.X <= 0 || scale.Y <= 0 || !destRect.Contains(point))
            return false;

        viewportPoint = new Point(
            sourceRect.X + (point.X - destRect.X) / scale.X,
            sourceRect.Y + (point.Y - destRect.Y) / scale.Y);
        return true;
    }
}
