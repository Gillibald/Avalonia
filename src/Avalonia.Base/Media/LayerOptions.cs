using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Media;

/// <summary>
/// Parameters that control how a layer pushed by
/// <see cref="DrawingContext.PushLayer(LayerOptions)"/> is composited back
/// onto the surface below it when the layer is popped.
///
/// A layer saves subsequent draw operations onto an offscreen buffer and
/// composites them as a unit. This differs from
/// <see cref="DrawingContext.PushOpacity(double)"/>, which blends each child
/// with the existing backdrop independently — layers are needed for SVG's
/// <c>&lt;g opacity&gt;</c> (group opacity), <c>mix-blend-mode</c>, and
/// <c>&lt;filter&gt;</c> semantics, where overlapping semi-transparent
/// children must first be combined before the group as a whole is composited.
/// </summary>
public readonly record struct LayerOptions
{
    /// <summary>
    /// Optional explicit layer bounds in local coordinates. When <c>null</c>,
    /// the backend sizes the layer implicitly from the drawn content.
    /// The bounds are a sizing hint for the offscreen buffer, not a clip —
    /// content drawn outside them is not necessarily clipped. Callers that
    /// need hard clipping (e.g. an SVG filter region) must push an explicit
    /// clip around the layer.
    /// </summary>
    public Rect? Bounds { get; init; }

    /// <summary>
    /// Opacity applied when the layer is composited back. <c>null</c> means
    /// the default value of <c>1.0</c> (opaque). Differs from
    /// <see cref="DrawingContext.PushOpacity(double)"/> in that overlapping
    /// semi-transparent children are first blended inside the layer before
    /// this opacity is applied.
    /// </summary>
    public double? Opacity { get; init; }

    /// <summary>
    /// Blend mode used when compositing the layer onto the surface below.
    /// <see cref="BitmapBlendingMode.Unspecified"/> (the default) uses
    /// <see cref="BitmapBlendingMode.SourceOver"/> semantics. Drives SVG
    /// <c>mix-blend-mode</c>.
    /// </summary>
    public BitmapBlendingMode BlendMode { get; init; }

    /// <summary>
    /// Optional image-filter effect applied at layer composite time. Drives
    /// SVG <c>&lt;filter&gt;</c>. When <c>null</c>, no filter is applied.
    /// </summary>
    public IEffect? Effect { get; init; }

    /// <summary>
    /// Optional image-filter effect applied to the content already on the
    /// surface when the layer is opened, rather than to the layer's own
    /// content. This is the CSS <c>backdrop-filter</c> semantic: the layer is
    /// initialized with a filtered copy of what is behind it, so drawing a
    /// translucent shape over it produces the frosted-glass look.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Effect"/>, a backdrop cannot be expressed by the
    /// composite-time paint, so it needs a backend that implements
    /// <c>IDrawingContextImplWithLayers</c>. Backends without one ignore it and
    /// emit a one-shot warning. The filtered region is
    /// <see cref="BackdropBounds"/> shaped by <see cref="BackdropClip"/>; the
    /// rest of the layer starts out transparent. Like everything else in the
    /// layer, the backdrop is subject to <see cref="Effect"/>,
    /// <see cref="OpacityMask"/> and <see cref="Opacity"/> when the layer
    /// composites.
    /// </remarks>
    public IEffect? BackdropEffect { get; init; }

    /// <summary>
    /// The rectangle the <see cref="BackdropEffect"/> fills, in local
    /// coordinates. When <c>null</c>, the backdrop fills <see cref="Bounds"/>,
    /// or the current clip when no bounds are given either. Set it to keep a
    /// backdrop to its own area in a layer that is larger than that area, such
    /// as a panel whose content overflows it.
    /// </summary>
    public Rect? BackdropBounds { get; init; }

    /// <summary>
    /// Optional geometry that shapes the <see cref="BackdropEffect"/> inside
    /// <see cref="BackdropBounds"/>, in local coordinates - a rounded rectangle
    /// for a rounded panel, say. Unlike a clip pushed around the layer, it
    /// does not clip anything drawn into the layer.
    /// </summary>
    public Geometry? BackdropClip { get; init; }

    /// <summary>
    /// The resolved form of <see cref="BackdropClip"/> that implementations
    /// read. Drawing contexts resolve it from <see cref="BackdropClip"/>, and
    /// render-thread callers that hold only a platform geometry set it
    /// directly.
    /// </summary>
    internal IGeometryImpl? BackdropClipImpl { get; init; }

    /// <summary>
    /// Optional brush that masks the layer as a whole when it composites: its
    /// coverage, read according to <see cref="OpacityMaskType"/>, scales the
    /// layer's content and its backdrop alike. The brush is mapped onto
    /// <see cref="Bounds"/>, which must be set when a mask is, the same way
    /// <see cref="DrawingContext.PushOpacityMask(IBrush, Rect)"/> maps its brush
    /// onto its bounds.
    /// </summary>
    public IBrush? OpacityMask { get; init; }

    /// <summary>
    /// How <see cref="OpacityMask"/> turns into coverage: the brush's alpha
    /// (the default) or its luminance.
    /// </summary>
    public MaskType OpacityMaskType { get; init; }

    /// <summary>
    /// Optional cache slot for the filtered backdrop, stamped by the
    /// compositor's render pass. When present, an implementation that supports
    /// caching draws the retained image instead of re-sampling the surface, and
    /// refreshes it only on frames the update pass has marked safe. See
    /// <see cref="BackdropLayerCache"/> for the handshake contract.
    /// </summary>
    internal BackdropLayerCache? BackdropCache { get; init; }

    /// <summary>
    /// Forces an isolated offscreen layer even when no other compositing
    /// parameter is set. Children with non-default blend modes then composite
    /// against the layer's contents instead of the page backdrop — the CSS
    /// <c>isolation: isolate</c> semantics.
    /// </summary>
    public bool Isolate { get; init; }

    /// <summary>Opacity with the default (<c>1.0</c>) substituted for an unset value.</summary>
    internal double EffectiveOpacity => Opacity ?? 1.0;

    /// <summary>Blend mode with <see cref="BitmapBlendingMode.Unspecified"/> mapped to <see cref="BitmapBlendingMode.SourceOver"/>.</summary>
    internal BitmapBlendingMode EffectiveBlendMode =>
        BlendMode == BitmapBlendingMode.Unspecified ? BitmapBlendingMode.SourceOver : BlendMode;

    /// <summary>True when the options would produce a pass-through layer (no visible effect).</summary>
    internal bool IsPassthrough =>
        !Isolate
        && EffectiveOpacity == 1.0
        && EffectiveBlendMode == BitmapBlendingMode.SourceOver
        && Effect == null
        && BackdropEffect == null
        && OpacityMask == null;
}
