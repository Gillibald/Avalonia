using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;

namespace Avalonia.Svg;

/// <summary>
/// Displays an SVG document and routes pointer events to the SVG elements under
/// the pointer. The document compiles once into a retained
/// <see cref="Rendering.Composition.DrawingRecording"/> (via <see cref="SvgImage"/>);
/// rendering replays the recording under the control's stretch transform.
/// </summary>
public class Svg : Control
{
    /// <summary>
    /// Defines the <see cref="Source"/> property.
    /// </summary>
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<Svg, Uri?>(nameof(Source));

    /// <summary>
    /// Defines the <see cref="Document"/> property.
    /// </summary>
    public static readonly StyledProperty<SvgDocument?> DocumentProperty =
        AvaloniaProperty.Register<Svg, SvgDocument?>(nameof(Document));

    /// <summary>
    /// Defines the <see cref="Stretch"/> property.
    /// </summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<Svg, Stretch>(nameof(Stretch), Stretch.Uniform);

    /// <summary>
    /// Defines the <see cref="StretchDirection"/> property.
    /// </summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<Svg, StretchDirection>(nameof(StretchDirection), StretchDirection.Both);

    private SvgImage? _image;
    private SvgDocument? _ownedDocument;
    private bool _loadFailed;

    static Svg()
    {
        AffectsRender<Svg>(SourceProperty, DocumentProperty, StretchProperty, StretchDirectionProperty);
        AffectsMeasure<Svg>(SourceProperty, DocumentProperty, StretchProperty, StretchDirectionProperty);
    }

    /// <summary>
    /// Gets or sets the URI the document is loaded from — an absolute
    /// <c>avares://</c> resource or file URI. Ignored while <see cref="Document"/>
    /// is set.
    /// </summary>
    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// Gets or sets the document to display. Takes precedence over
    /// <see cref="Source"/>; the caller keeps ownership of the document.
    /// </summary>
    public SvgDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
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

        ComputeStretchRects(image, out var sourceRect, out var destRect, out _);
        if (destRect.Width <= 0 || destRect.Height <= 0)
            return;

        context.DrawImage(image, sourceRect, destRect);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        EnsureImage() is { } image
            ? Stretch.CalculateSize(availableSize, image.Size, StretchDirection)
            : default;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) =>
        EnsureImage() is { } image
            ? Stretch.CalculateSize(finalSize, image.Size)
            : default;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == DocumentProperty)
            ReleaseImage();
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

        var document = Document;
        if (document == null && !_loadFailed && Source is { } source)
        {
            try
            {
                _ownedDocument = SvgDocument.Load(source);
                document = _ownedDocument;
            }
            catch (Exception exception)
            {
                // Remember the failure so a broken source is not re-loaded on
                // every measure/render pass.
                _loadFailed = true;
                Logger.TryGet(LogEventLevel.Error, LogArea.Control)?.Log(
                    this, "Failed to load SVG document from {Source}: {Error}", source, exception);
            }
        }

        if (document == null)
            return null;

        _image = new SvgImage(document);

        // Compositor-bound (animated) recordings report bounds changes; static
        // immutable recordings never do (and throw on subscription).
        if (_image.Recording.Compositor != null)
            _image.Recording.BoundsChanged += OnRecordingBoundsChanged;

        return _image;
    }

    private void OnRecordingBoundsChanged(object? sender, Rect bounds) => InvalidateMeasure();

    private void ReleaseImage()
    {
        if (_image != null)
        {
            if (_image.Recording.Compositor != null)
                _image.Recording.BoundsChanged -= OnRecordingBoundsChanged;

            _image.Dispose();
            _image = null;
        }

        _ownedDocument?.Dispose();
        _ownedDocument = null;
        _loadFailed = false;
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
