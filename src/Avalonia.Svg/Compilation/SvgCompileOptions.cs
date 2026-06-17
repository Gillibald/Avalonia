using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Avalonia.Media.Svg.Compilation;

/// <summary>
/// Optional inputs and outputs of a document compilation. Inputs are set by the
/// caller; the compiler fills the result properties.
/// </summary>
internal sealed class SvgCompileOptions
{
    /// <summary>Build the element hit-test tree alongside the recording.</summary>
    public bool BuildHitTree { get; init; }

    /// <summary>
    /// (element, fill/stroke) pairs whose paints compile as mutable
    /// <see cref="SolidColorBrush"/> instances for the animation paint channel.
    /// Only meaningful when the target recording is compositor-bound — immutable
    /// recordings snapshot mutable brushes.
    /// </summary>
    public IReadOnlyCollection<(SvgElement Element, string Attribute)>? PaintAnimationTargets { get; init; }

    /// <summary>
    /// Restricts the render-tree walk to elements the predicate accepts; null
    /// compiles everything. Reference-resolved content (defs, use targets,
    /// paint servers) is unaffected. The animation composition channel uses
    /// this to compile document slices that render as separate composition
    /// visuals.
    /// </summary>
    public Func<SvgElement, bool>? ElementFilter { get; init; }

    /// <summary>The hit-test tree root, when <see cref="BuildHitTree"/> was set.</summary>
    public SvgHitNode? HitRoot { get; internal set; }

    /// <summary>The mutable brushes registered for <see cref="PaintAnimationTargets"/>.</summary>
    public Dictionary<(SvgElement Element, string Attribute), SolidColorBrush>? AnimatedBrushes { get; internal set; }
}
