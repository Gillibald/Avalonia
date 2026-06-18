using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Media.Svg.Animation;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg;

/// <summary>
/// Hosts a partitioned document as a composition visual tree: static slices
/// compile once, structural slices re-compile into their own visuals on
/// structural ticks, and composition groups run their SMIL transform/opacity
/// timelines as server-side key-frame animations — the UI thread does no
/// per-frame work for them at all.
/// </summary>
internal sealed class SvgCompositionHost : IDisposable
{
    private readonly SvgDocument _document;
    private readonly Compositor _compositor;
    private readonly SvgAnimator _animator;
    private readonly SvgCompositionGroup _rootGroup;
    private readonly Size _viewport;
    private readonly SvgAnimationState _state;
    private readonly IReadOnlyCollection<(SvgElement Element, string Attribute)> _paintTargets;
    private readonly List<DrawingRecording> _recordings = new();
    private readonly List<(CompositionRecordingVisual Visual, HashSet<SvgElement> Membership)> _structuralSlices = new();
    private readonly Dictionary<(SvgElement Element, string Attribute), SolidColorBrush> _brushes = new();
    private bool _disposed;

    public SvgCompositionHost(
        SvgDocument document,
        Compositor compositor,
        SvgAnimator animator,
        SvgCompositionGroup rootGroup,
        Size viewport,
        SvgAnimationState state)
    {
        _document = document;
        _compositor = compositor;
        _animator = animator;
        _rootGroup = rootGroup;
        _viewport = viewport;
        _state = state;
        _paintTargets = animator.GetPaintTargets();

        ApplySuppressions(rootGroup);

        RootVisual = compositor.CreateRecordingVisual();
        BuildChildren(RootVisual, rootGroup.Children);

        _animator.BindPaintBrushes(_brushes);
    }

    /// <summary>The visual to attach as the control's child visual.</summary>
    public CompositionRecordingVisual RootVisual { get; }

    /// <summary>Maps control bounds onto the document viewport (stretch).</summary>
    public void UpdateStretch(Matrix transform) => RootVisual.Transform = transform;

    /// <summary>
    /// Re-compiles the structural slices after a structural tick. Static and
    /// composition slices are untouched.
    /// </summary>
    public void RecompileStructural()
    {
        if (_disposed)
            return;

        foreach (var (visual, membership) in _structuralSlices)
        {
            var previous = visual.Recording;
            visual.Recording = Compile(membership);
            if (previous != null)
            {
                _recordings.Remove(previous);
                previous.Dispose();
            }
        }

        // Re-compiles register fresh mutable brushes for paint targets inside
        // structural slices; rebind so paint ticks keep mutating live brushes.
        _animator.BindPaintBrushes(_brushes);
    }

    private void ApplySuppressions(SvgCompositionGroup group)
    {
        // The element states the visuals carry (animated/static transforms,
        // animated opacity) are suppressed in every slice compile through the
        // per-instance animated overrides; claimed entries never write these
        // keys. The state is materialized only during a compile, so nothing
        // leaks onto the shared document.
        if (group.SuppressTransform)
            _state.Set(group.Element, "transform", "");

        if (group.SuppressOpacity)
            _state.Set(group.Element, "opacity", "1");

        foreach (var child in group.Children)
        {
            if (child is SvgCompositionGroup nested)
                ApplySuppressions(nested);
        }
    }

    private void BuildChildren(CompositionRecordingVisual parent, List<SvgCompositionNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case SvgStaticSlice staticSlice:
                {
                    var visual = _compositor.CreateRecordingVisual();
                    visual.Recording = Compile(SvgCompositionPartitioner.BuildMembership(staticSlice.Roots));
                    parent.Children.Add(visual);
                    break;
                }

                case SvgStructuralSlice structuralSlice:
                {
                    var membership = SvgCompositionPartitioner.BuildMembership(new[] { structuralSlice.Root });
                    var visual = _compositor.CreateRecordingVisual();
                    visual.Recording = Compile(membership);
                    parent.Children.Add(visual);
                    _structuralSlices.Add((visual, membership));
                    break;
                }

                case SvgCompositionGroup group:
                {
                    var visual = _compositor.CreateRecordingVisual();

                    if (group.StaticTransform is { } transformValue
                        && SvgTransformParser.TryParse(transformValue.AsSpan(), out var matrix))
                    {
                        visual.Transform = matrix;
                    }

                    if (group.SuppressOpacity)
                        visual.Opacity = group.StaticOpacity;

                    BuildChildren(visual, group.Children);
                    StartAnimations(visual, group);
                    parent.Children.Add(visual);
                    break;
                }
            }
        }
    }

    private DrawingRecording Compile(HashSet<SvgElement> membership)
    {
        var options = new SvgCompileOptions
        {
            ElementFilter = membership.Contains,
            PaintAnimationTargets = _paintTargets.Count > 0 ? _paintTargets : null,
        };

        // DrawingRecording.Create compiles synchronously, so the instance's
        // overrides need to be live on the elements only for this call.
        DrawingRecording recording;
        using (_state.Materialize())
        {
            recording = DrawingRecording.Create(
                _compositor,
                ctx => SvgCompiler.CompileDocument(_document, ctx, _viewport, options));
        }

        _recordings.Add(recording);

        if (options.AnimatedBrushes != null)
        {
            foreach (var pair in options.AnimatedBrushes)
                _brushes[pair.Key] = pair.Value;
        }

        return recording;
    }

    private void StartAnimations(CompositionRecordingVisual visual, SvgCompositionGroup group)
    {
        foreach (var animation in group.Animations)
        {
            animation.Entry.Claimed = true;

            switch (animation.Kind)
            {
                case SvgCompositionAnimationKind.Rotate:
                {
                    visual.CenterPoint = new Vector3D(animation.CenterX, animation.CenterY, 0);
                    var frames = _compositor.CreateScalarKeyFrameAnimation();
                    Configure(frames, animation.Entry);
                    InsertFrames(frames, animation,
                        static (anim, key, frame, easing) =>
                            anim.InsertKeyFrame(key, frame[0] * MathF.PI / 180f, easing));
                    visual.StartAnimation("RotationAngle", frames);
                    break;
                }

                case SvgCompositionAnimationKind.Translate:
                {
                    var frames = _compositor.CreateVector3KeyFrameAnimation();
                    Configure(frames, animation.Entry);
                    InsertFrames(frames, animation,
                        static (anim, key, frame, easing) =>
                            anim.InsertKeyFrame(key, new Vector3(frame[0], frame[1], 0), easing));
                    visual.StartAnimation("Offset", frames);
                    break;
                }

                case SvgCompositionAnimationKind.Scale:
                {
                    // SVG scales about the user-space origin.
                    visual.CenterPoint = default;
                    var frames = _compositor.CreateVector3KeyFrameAnimation();
                    Configure(frames, animation.Entry);
                    InsertFrames(frames, animation,
                        static (anim, key, frame, easing) =>
                            anim.InsertKeyFrame(key, new Vector3(frame[0], frame[1], 1), easing));
                    visual.StartAnimation("Scale", frames);
                    break;
                }

                case SvgCompositionAnimationKind.Opacity:
                {
                    var frames = _compositor.CreateScalarKeyFrameAnimation();
                    Configure(frames, animation.Entry);
                    InsertFrames(frames, animation,
                        static (anim, key, frame, easing) =>
                            anim.InsertKeyFrame(key, frame[0], easing));
                    visual.StartAnimation("Opacity", frames);
                    break;
                }
            }
        }
    }

    private static void InsertFrames<TAnimation>(
        TAnimation animation,
        SvgCompositionAnimation source,
        Action<TAnimation, float, float[], Easing> insert)
        where TAnimation : KeyFrameAnimation
    {
        // SMIL values are evenly spaced over the simple duration with linear
        // interpolation (calcMode discrete and keyTimes never reach this channel).
        var linear = new LinearEasing();
        var last = source.Frames.Length - 1;
        for (var i = 0; i < source.Frames.Length; i++)
            insert(animation, last == 0 ? 1f : (float)i / last, source.Frames[i], linear);
    }

    private static void Configure(KeyFrameAnimation animation, SvgAnimationEntry entry)
    {
        animation.DelayTime = entry.Begin;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueAfterDelay;
        animation.Duration = entry.Duration;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        if (double.IsPositiveInfinity(entry.RepeatCount))
        {
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            animation.IterationBehavior = AnimationIterationBehavior.Count;
            animation.IterationCount = Math.Max(1, (int)entry.RepeatCount);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        UnclaimEntries(_rootGroup);

        foreach (var recording in _recordings)
            recording.Dispose();
        _recordings.Clear();
        _structuralSlices.Clear();
    }

    private static void UnclaimEntries(SvgCompositionGroup group)
    {
        foreach (var animation in group.Animations)
            animation.Entry.Claimed = false;

        foreach (var child in group.Children)
        {
            if (child is SvgCompositionGroup nested)
                UnclaimEntries(nested);
        }
    }
}
