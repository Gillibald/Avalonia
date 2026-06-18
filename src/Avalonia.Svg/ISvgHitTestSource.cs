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
/// Only structural (geometry) animation is reflected: the per-instance
/// <c>SvgAnimationState</c> the instance maintains already holds the current
/// structural overrides. Transform/opacity timelines run as server-side
/// composition animations with no UI-thread clock, so their current value is not
/// available here and those elements still hit test at their base transform.
/// </remarks>
internal interface ISvgHitTestSource
{
    /// <summary>Hit tests at a point in the document's viewport coordinates.</summary>
    IReadOnlyList<SvgElement> HitTest(Point point);
}
