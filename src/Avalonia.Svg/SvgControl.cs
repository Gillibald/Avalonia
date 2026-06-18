using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Media.Svg;

namespace Avalonia.Controls;

/// <summary>
/// Displays an SVG document and routes pointer events to the SVG elements under
/// the pointer. The document compiles into an <see cref="SvgImage"/>; an animated
/// document renders through a render-thread composition visual (via the shared
/// <see cref="CompositionImageHost"/>), a static one through a plain draw. The
/// control adds the typed <see cref="SvgDocument"/> source, the stretch mapping
/// and SVG hit testing on top of that shared hosting.
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
    private CompositionImageHost? _host;

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
    /// URI) load through the XAML <c>SvgDocumentTypeConverter</c>. Documents
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

        // While hosting an animated document, hit test against the instance's
        // current frame so structural geometry follows the animation; a static
        // document (no instance) tests against the compiled image directly.
        if (_host?.Instance is ISvgHitTestSource hitSource)
            return hitSource.HitTest(viewportPoint);

        return image.HitTestElements(viewportPoint);
    }

    /// <inheritdoc/>
    public sealed override void Render(DrawingContext context)
    {
        if (EnsureImage() is not { } image || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        // An animated document renders through the child composition visual; the
        // control then only contributes the stretch mapping (set inside TryHost).
        // The child visual is not a draw-list visual, so it never registers in
        // the compositor hit test — the control must still paint a transparent
        // surface over its bounds to stay hit-testable for pointer routing to the
        // SVG elements. A static document falls through to the draw below.
        if (_host is { } host && host.TryHost(Bounds, image.Size, Stretch, StretchDirection))
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            return;
        }

        ComputeStretchRects(image, out var sourceRect, out var destRect, out _);
        if (destRect.Width <= 0 || destRect.Height <= 0)
            return;

        context.DrawImage(image, sourceRect, destRect);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (EnsureImage() is not { } image)
            return default;

        // The layout pass is the safe place to attach the child visual; the
        // render pass must not invalidate.
        _host?.EnsureAttached();
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
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host?.EnsureAttached();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host?.Detach();
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

        // One static compile feeds measurement, hit testing and the static draw;
        // the host builds its own animating instance from the same document when
        // the document is animated.
        _image = new SvgImage(document);
        _host = new CompositionImageHost(this);
        _host.SetSource(_image);
        return _image;
    }

    private void ReleaseImage()
    {
        _host?.Dispose();
        _host = null;
        _image?.Dispose();
        _image = null;
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
