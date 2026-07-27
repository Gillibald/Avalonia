using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

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

    /// <summary>
    /// Composition brushes pre-created for paint targets lifted to the
    /// composition channel: the compiler paints with these instead of
    /// registering mutable brushes, and their color animations run on the
    /// render thread. Each brush is seeded from the statically resolved paint
    /// exactly once (tracked in <see cref="SeededLiftedTargets"/>) - a later
    /// client write would detach the running server animation.
    /// </summary>
    public IReadOnlyDictionary<(SvgElement Element, string Attribute), CompositionSolidColorBrush>? LiftedPaintBrushes { get; init; }

    /// <summary>
    /// Targets whose lifted brush has been seeded; owned by the host so the
    /// guard survives structural re-compiles.
    /// </summary>
    public HashSet<(SvgElement Element, string Attribute)>? SeededLiftedTargets { get; init; }

    /// <summary>
    /// Gradient definitions lifted to composition gradient brushes, keyed by
    /// the definition element: plain consumers paint with the shared brush and
    /// its timelines run on the render thread. Measuring and shared compiles,
    /// context paints and consumers with their own paint opacity keep the
    /// static per-use resolution.
    /// </summary>
    public IReadOnlyDictionary<SvgElement, IBrush>? LiftedGradients { get; init; }

    /// <summary>The hit-test tree root, when <see cref="BuildHitTree"/> was set.</summary>
    public SvgHitNode? HitRoot { get; internal set; }

    /// <summary>The mutable brushes registered for <see cref="PaintAnimationTargets"/>.</summary>
    public Dictionary<(SvgElement Element, string Attribute), SolidColorBrush>? AnimatedBrushes { get; internal set; }
}
