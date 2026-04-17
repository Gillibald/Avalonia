using Avalonia.Media.Imaging;

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

    /// <summary>Opacity with the default (<c>1.0</c>) substituted for an unset value.</summary>
    internal double EffectiveOpacity => Opacity ?? 1.0;

    /// <summary>Blend mode with <see cref="BitmapBlendingMode.Unspecified"/> mapped to <see cref="BitmapBlendingMode.SourceOver"/>.</summary>
    internal BitmapBlendingMode EffectiveBlendMode =>
        BlendMode == BitmapBlendingMode.Unspecified ? BitmapBlendingMode.SourceOver : BlendMode;

    /// <summary>True when the options would produce a pass-through layer (no visible effect).</summary>
    internal bool IsPassthrough =>
        EffectiveOpacity == 1.0
        && EffectiveBlendMode == BitmapBlendingMode.SourceOver
        && Effect == null;
}
