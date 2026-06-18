using System.Collections.Generic;

namespace Avalonia.Media.Svg;

/// <summary>
/// Implemented by a hosted SVG composition instance so the host control can hit
/// test against the current animation frame. The host downcasts its
/// <see cref="Avalonia.Media.ICompositionImageInstance"/> to this when routing
/// element hit tests, so structural geometry follows the animation instead of
/// staying at the document's base state.
/// </summary>
/// <remarks>
/// Structural (geometry) animation is reflected through the per-instance
/// <c>SvgAnimationState</c> the instance maintains; transform/opacity timelines
/// run as server-side composition animations, so their current transform is read
/// back from the compositor and folded into the hit tree. Both follow what is
/// actually drawn, within a frame.
/// </remarks>
internal interface ISvgHitTestSource
{
    /// <summary>Hit tests at a point in the document's viewport coordinates.</summary>
    IReadOnlyList<SvgElement> HitTest(Point point);
}
