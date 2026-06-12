using System;
using System.Collections.Generic;
using System.Globalization;

namespace Avalonia.Svg.Animation;

/// <summary>The visual property a composition-channel animation drives.</summary>
internal enum SvgCompositionAnimationKind
{
    /// <summary>animateTransform rotate → RotationAngle with a fixed CenterPoint.</summary>
    Rotate,
    /// <summary>animateTransform translate → Offset.</summary>
    Translate,
    /// <summary>animateTransform scale → Scale (about the user-space origin).</summary>
    Scale,
    /// <summary>animate opacity → Opacity.</summary>
    Opacity,
}

/// <summary>
/// A SMIL animation lowered to a composition key-frame animation: the sampled
/// values become server-side key frames and the UI thread does no per-frame
/// work at all.
/// </summary>
internal sealed class SvgCompositionAnimation
{
    public SvgCompositionAnimation(SvgAnimationEntry entry, SvgCompositionAnimationKind kind, float[][] frames)
    {
        Entry = entry;
        Kind = kind;
        Frames = frames;
    }

    public SvgAnimationEntry Entry { get; }

    public SvgCompositionAnimationKind Kind { get; }

    /// <summary>
    /// Parsed numeric components per key frame: rotate <c>[angle°]</c> (the
    /// shared center lives in <see cref="CenterX"/>/<see cref="CenterY"/>),
    /// translate <c>[tx, ty]</c>, scale <c>[sx, sy]</c>, opacity <c>[o]</c>.
    /// </summary>
    public float[][] Frames { get; }

    /// <summary>The rotation center; key frames must agree on it.</summary>
    public float CenterX { get; init; }

    public float CenterY { get; init; }

    /// <summary>
    /// Classifies a parsed SMIL entry for the composition channel. Only shapes
    /// the server key-frame clock reproduces exactly are accepted: linear
    /// interpolation, at least two values, an offset begin, and either an
    /// indefinite repeat or a whole-number repeat with <c>fill="freeze"</c>
    /// (a finished composition animation holds its final frame).
    /// </summary>
    public static bool TryClassify(SvgAnimationEntry entry, out SvgCompositionAnimation? animation)
    {
        animation = null;

        if (entry.IsSet || entry.Discrete || entry.Values.Length < 2
            || entry.Duration < TimeSpan.FromMilliseconds(1) || entry.Begin < TimeSpan.Zero)
        {
            return false;
        }

        var indefinite = double.IsPositiveInfinity(entry.RepeatCount);
        if (!indefinite && (entry.RepeatCount % 1 != 0 || entry.RepeatCount < 1 || !entry.Freeze))
            return false;

        if (entry.TransformType is { } transformType)
        {
            // The composition transform replaces the attribute from t=0; a
            // static transform that should show before a delayed begin cannot
            // be represented on the visual.
            if (entry.Begin > TimeSpan.Zero && entry.Target.GetAttribute("transform") != null)
                return false;

            var frames = new float[entry.Values.Length][];
            for (var i = 0; i < entry.Values.Length; i++)
            {
                if (!TryParseNumberList(entry.Values[i], out frames[i]!))
                    return false;
            }

            switch (transformType)
            {
                case SvgAnimationTransformType.Rotate:
                {
                    var centerX = 0f;
                    var centerY = 0f;
                    var angles = new float[frames.Length][];
                    for (var i = 0; i < frames.Length; i++)
                    {
                        var frame = frames[i];
                        if (frame.Length != 1 && frame.Length != 3)
                            return false;

                        var cx = frame.Length == 3 ? frame[1] : 0f;
                        var cy = frame.Length == 3 ? frame[2] : 0f;
                        if (i == 0)
                        {
                            centerX = cx;
                            centerY = cy;
                        }
                        else if (cx != centerX || cy != centerY)
                        {
                            // The visual has a single CenterPoint; per-frame
                            // centers stay on the structural channel.
                            return false;
                        }

                        angles[i] = new[] { frame[0] };
                    }

                    animation = new SvgCompositionAnimation(entry, SvgCompositionAnimationKind.Rotate, angles)
                    {
                        CenterX = centerX,
                        CenterY = centerY,
                    };
                    return true;
                }

                case SvgAnimationTransformType.Translate:
                {
                    var offsets = new float[frames.Length][];
                    for (var i = 0; i < frames.Length; i++)
                    {
                        var frame = frames[i];
                        if (frame.Length != 1 && frame.Length != 2)
                            return false;
                        offsets[i] = new[] { frame[0], frame.Length == 2 ? frame[1] : 0f };
                    }

                    animation = new SvgCompositionAnimation(entry, SvgCompositionAnimationKind.Translate, offsets);
                    return true;
                }

                case SvgAnimationTransformType.Scale:
                {
                    var scales = new float[frames.Length][];
                    for (var i = 0; i < frames.Length; i++)
                    {
                        var frame = frames[i];
                        if (frame.Length != 1 && frame.Length != 2)
                            return false;
                        scales[i] = new[] { frame[0], frame.Length == 2 ? frame[1] : frame[0] };
                    }

                    animation = new SvgCompositionAnimation(entry, SvgCompositionAnimationKind.Scale, scales);
                    return true;
                }

                default:
                    return false;
            }
        }

        if (entry.AttributeName == "opacity")
        {
            var frames = new float[entry.Values.Length][];
            for (var i = 0; i < entry.Values.Length; i++)
            {
                if (!float.TryParse(entry.Values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var o))
                    return false;
                frames[i] = new[] { Math.Clamp(o, 0f, 1f) };
            }

            animation = new SvgCompositionAnimation(entry, SvgCompositionAnimationKind.Opacity, frames);
            return true;
        }

        return false;
    }

    private static bool TryParseNumberList(string value, out float[] numbers)
    {
        var parts = value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        numbers = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        return parts.Length > 0;
    }
}

/// <summary>A node of the composition slice tree; see <see cref="SvgCompositionPartitioner"/>.</summary>
internal abstract class SvgCompositionNode
{
}

/// <summary>
/// A run of consecutive render-tree elements without structural or composition
/// animations: compiles into one recording visual, once.
/// </summary>
internal sealed class SvgStaticSlice : SvgCompositionNode
{
    public List<SvgElement> Roots { get; } = new();
}

/// <summary>
/// An element whose subtree re-compiles on structural ticks into its own
/// recording visual, leaving every other slice untouched.
/// </summary>
internal sealed class SvgStructuralSlice : SvgCompositionNode
{
    public SvgStructuralSlice(SvgElement root) => Root = root;

    public SvgElement Root { get; }
}

/// <summary>
/// A container visual: either a composition-animated element (its SMIL
/// animations run as server key-frame animations on the visual) or a plain
/// transformed wrapper whose static transform moves onto the visual so nested
/// animated transforms compose in the right order.
/// </summary>
internal sealed class SvgCompositionGroup : SvgCompositionNode
{
    public SvgCompositionGroup(SvgElement element) => Element = element;

    public SvgElement Element { get; }

    public List<SvgCompositionAnimation> Animations { get; } = new();

    /// <summary>The element's static transform attribute value, when it moves to the visual.</summary>
    public string? StaticTransform { get; set; }

    /// <summary>True when the element's transform attribute must be suppressed in slice compiles.</summary>
    public bool SuppressTransform { get; set; }

    /// <summary>True when the element's opacity attribute must be suppressed in slice compiles.</summary>
    public bool SuppressOpacity { get; set; }

    /// <summary>The element's static opacity, applied as the visual's initial Opacity.</summary>
    public float StaticOpacity { get; set; } = 1f;

    public List<SvgCompositionNode> Children { get; } = new();
}

/// <summary>
/// Partitions the render tree into an ordered slice tree for the composition
/// channel: static recordings, self-recompiling structural slices and
/// composition groups whose motion runs entirely on the render thread.
/// Returns null when the document gains nothing from the split.
/// </summary>
internal static class SvgCompositionPartitioner
{
    public static SvgCompositionGroup? TryBuild(SvgDocument document, SvgAnimator animator)
    {
        var root = document.Root;
        if (root == null)
            return null;

        // Layer state on the root wraps the whole document; slicing under it
        // would break the layer semantics.
        if (root.GetStyleOrAttribute("filter") != null
            || root.GetStyleOrAttribute("clip-path") != null
            || root.GetStyleOrAttribute("mask") != null)
        {
            return null;
        }

        // Pre-classify: per element, its composition candidates and whether
        // any entry needs more than brush mutation.
        var compositionByElement = new Dictionary<SvgElement, List<SvgCompositionAnimation>>();
        var needsHandling = new HashSet<SvgElement>();
        foreach (var entry in animator.Entries)
        {
            if (animator.IsPaintEntry(entry))
                continue;

            needsHandling.Add(entry.Target);

            if (SvgCompositionAnimation.TryClassify(entry, out var animation))
            {
                if (!compositionByElement.TryGetValue(entry.Target, out var list))
                    compositionByElement[entry.Target] = list = new List<SvgCompositionAnimation>();
                list.Add(animation!);
            }
        }

        if (needsHandling.Count == 0)
            return null;

        var rootGroup = new SvgCompositionGroup(root);
        var anyComposition = false;
        Partition(root, rootGroup.Children, compositionByElement, needsHandling, animator, ref anyComposition);

        return anyComposition ? rootGroup : null;
    }

    private static void Partition(
        SvgElement container,
        List<SvgCompositionNode> nodes,
        Dictionary<SvgElement, List<SvgCompositionAnimation>> compositionByElement,
        HashSet<SvgElement> needsHandling,
        SvgAnimator animator,
        ref bool anyComposition)
    {
        SvgStaticSlice? staticRun = null;

        foreach (var child in container.Children)
        {
            // Animation elements are consumed by the animator and never render.
            if (child.Name is "animate" or "set" or "animateTransform")
                continue;

            if (!SubtreeNeedsHandling(child, needsHandling))
            {
                (staticRun ??= NewStaticRun(nodes)).Roots.Add(child);
                continue;
            }

            var ownEntries = CollectOwnEntries(child, animator);
            var candidates = compositionByElement.TryGetValue(child, out var list) ? list : null;

            if (ownEntries.Count > 0 && candidates != null && CoversAll(ownEntries, candidates)
                && CountTransforms(candidates) <= 1 && CountOpacity(candidates) <= 1
                && !HasLayerState(child))
            {
                staticRun = null;
                var group = new SvgCompositionGroup(child);
                group.Animations.AddRange(candidates);

                foreach (var animation in candidates)
                {
                    if (animation.Kind != SvgCompositionAnimationKind.Opacity)
                    {
                        group.SuppressTransform = true;
                    }
                    else
                    {
                        group.SuppressOpacity = true;
                        group.StaticOpacity = ParseStaticOpacity(child);
                    }
                }

                Partition(child, group.Children, compositionByElement, needsHandling, animator, ref anyComposition);
                nodes.Add(group);
                anyComposition = true;
                continue;
            }

            if (ownEntries.Count == 0 && IsPlainContainer(child))
            {
                staticRun = null;
                var transform = child.GetStyleOrAttribute("transform");
                if (transform != null)
                {
                    // The static transform moves to the wrapper visual so
                    // nested animated transforms compose inside it.
                    var wrapper = new SvgCompositionGroup(child)
                    {
                        StaticTransform = transform,
                        SuppressTransform = true,
                    };
                    Partition(child, wrapper.Children, compositionByElement, needsHandling, animator, ref anyComposition);
                    nodes.Add(wrapper);
                }
                else
                {
                    // No state to carry: splice the partition inline.
                    Partition(child, nodes, compositionByElement, needsHandling, animator, ref anyComposition);
                    staticRun = null;
                }

                continue;
            }

            staticRun = null;
            nodes.Add(new SvgStructuralSlice(child));
        }
    }

    private static SvgStaticSlice NewStaticRun(List<SvgCompositionNode> nodes)
    {
        var run = new SvgStaticSlice();
        nodes.Add(run);
        return run;
    }

    private static bool SubtreeNeedsHandling(SvgElement element, HashSet<SvgElement> needsHandling)
    {
        if (needsHandling.Contains(element))
            return true;

        foreach (var child in element.Children)
        {
            if (SubtreeNeedsHandling(child, needsHandling))
                return true;
        }

        return false;
    }

    private static List<SvgAnimationEntry> CollectOwnEntries(SvgElement element, SvgAnimator animator)
    {
        var own = new List<SvgAnimationEntry>();
        foreach (var entry in animator.Entries)
        {
            if (entry.Target == element && !animator.IsPaintEntry(entry))
                own.Add(entry);
        }

        return own;
    }

    private static bool CoversAll(List<SvgAnimationEntry> entries, List<SvgCompositionAnimation> candidates)
    {
        foreach (var entry in entries)
        {
            var covered = false;
            foreach (var candidate in candidates)
            {
                if (candidate.Entry == entry)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
                return false;
        }

        return true;
    }

    private static int CountTransforms(List<SvgCompositionAnimation> candidates)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != SvgCompositionAnimationKind.Opacity)
                count++;
        }

        return count;
    }

    private static int CountOpacity(List<SvgCompositionAnimation> candidates)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind == SvgCompositionAnimationKind.Opacity)
                count++;
        }

        return count;
    }

    private static bool HasLayerState(SvgElement element) =>
        element.GetStyleOrAttribute("filter") != null
        || element.GetStyleOrAttribute("mask") != null
        || element.GetStyleOrAttribute("clip-path") != null;

    private static bool IsPlainContainer(SvgElement element) =>
        element.Name is "g" or "a"
        && element.GetStyleOrAttribute("opacity") == null
        && !HasLayerState(element)
        && element.GetStyleOrAttribute("display") != "none";

    private static float ParseStaticOpacity(SvgElement element)
    {
        var value = element.GetAttribute("opacity");
        if (value != null
            && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            return Math.Clamp(opacity, 0f, 1f);
        }

        return 1f;
    }

    /// <summary>
    /// Builds the membership set a slice compiles through: the subtrees of the
    /// given roots plus every ancestor up to the document root, so containers
    /// on the path still open and apply their state.
    /// </summary>
    public static HashSet<SvgElement> BuildMembership(IEnumerable<SvgElement> roots)
    {
        var membership = new HashSet<SvgElement>();
        foreach (var root in roots)
        {
            AddSubtree(root, membership);
            for (var ancestor = root.Parent; ancestor != null; ancestor = ancestor.Parent)
                membership.Add(ancestor);
        }

        return membership;
    }

    private static void AddSubtree(SvgElement element, HashSet<SvgElement> membership)
    {
        if (!membership.Add(element))
            return;

        foreach (var child in element.Children)
            AddSubtree(child, membership);
    }
}
