using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
    private readonly Dictionary<SvgElement, CompositionRecordingVisual> _compositionVisuals = new();
    private readonly Dictionary<(SvgElement Element, string Attribute), CompositionSolidColorBrush> _liftedBrushes = new();
    private readonly List<(SvgAnimationEntry Entry, Color[] Frames)> _liftedEntries = new();
    private readonly HashSet<(SvgElement Element, string Attribute)> _seededLifted = new();
    private readonly Dictionary<SvgElement, IBrush> _liftedGradients = new();
    private readonly List<(SvgGradientLift Plan, CompositionGradientBrush Brush, List<CompositionGradientStop> Stops)> _gradientLifts = new();
    private List<Action>? _deferredStarts;
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
        _paintTargets = PartitionPaintTargets(animator, compositor);
        ClassifyGradientLifts();

        ApplySuppressions(rootGroup);

        RootVisual = compositor.CreateRecordingVisual();
        BuildChildren(RootVisual, rootGroup.Children);

        _animator.BindPaintBrushes(_brushes);
        StartPaintAnimations();
        StartGradientAnimations();

        if (_deferredStarts != null)
            StartAfterSeedCommit(_deferredStarts);
    }

    /// <summary>
    /// Splits the paint targets between the channels: a target lifts to a
    /// composition brush when every entry driving it classifies for a
    /// server-side color key-frame animation (a sampled client write on a
    /// lifted brush would detach the running animation, so mixed targets stay
    /// sampled together). Returns the targets left on the sampled channel;
    /// lifted ones get their brush created here and painted with at compile.
    /// </summary>
    private IReadOnlyCollection<(SvgElement Element, string Attribute)> PartitionPaintTargets(
        SvgAnimator animator, Compositor compositor)
    {
        var sampled = new HashSet<(SvgElement Element, string Attribute)>();

        foreach (var target in animator.GetPaintTargets())
        {
            var lifts = new List<(SvgAnimationEntry Entry, Color[] Frames)>();
            var allLift = true;

            foreach (var entry in animator.Entries)
            {
                if (entry.Target != target.Element
                    || entry.AttributeName != target.Attribute
                    || !animator.IsPaintEntry(entry))
                {
                    continue;
                }

                if (SvgCompositionAnimation.TryClassifyPaint(entry, out var frames))
                {
                    lifts.Add((entry, frames));
                }
                else
                {
                    allLift = false;
                    break;
                }
            }

            if (allLift && lifts.Count > 0)
            {
                _liftedBrushes[target] = compositor.CreateSolidColorBrush();
                _liftedEntries.AddRange(lifts);
            }
            else
            {
                sampled.Add(target);
            }
        }

        return sampled;
    }

    /// <summary>The visual to attach as the control's child visual.</summary>
    public CompositionRecordingVisual RootVisual { get; }

    /// <summary>Maps control bounds onto the document viewport (stretch).</summary>
    public void UpdateStretch(Matrix transform) => RootVisual.Transform = transform;

    /// <summary>Whether any group runs a server-side transform/opacity animation.</summary>
    public bool HasServerAnimations => _compositionVisuals.Count > 0;

    /// <summary>
    /// The current (read-back) server transform of a composition-animated
    /// element's visual — what the compositor is actually drawing it with. Used
    /// to fold the live transform into hit testing. False when the element is not
    /// composition-animated or has no readback yet (not rendered).
    /// </summary>
    public bool TryGetServerTransform(SvgElement element, out Matrix transform)
    {
        if (!_disposed
            && _compositionVisuals.TryGetValue(element, out var visual)
            && visual.TryGetValidReadback() is { } readback)
        {
            transform = readback.Matrix;
            return true;
        }

        transform = default;
        return false;
    }

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

                    // Remember the visual of an actually-animated group so its
                    // current server transform can be read back for hit testing.
                    if (group.Animations.Count > 0)
                        _compositionVisuals[group.Element] = visual;
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
            LiftedPaintBrushes = _liftedBrushes.Count > 0 ? _liftedBrushes : null,
            SeededLiftedTargets = _liftedBrushes.Count > 0 ? _seededLifted : null,
            LiftedGradients = _liftedGradients.Count > 0 ? _liftedGradients : null,
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

    /// <summary>
    /// Starts the lifted paint timelines as server-side color key-frame
    /// animations on their composition brushes. Several entries on one target
    /// start in document order, so the last one drives the brush - the same
    /// later-wins the sampled channel produces per tick. Delayed begins start
    /// after the seed commit; see <see cref="StartAfterSeedCommit"/>.
    /// </summary>
    private void StartPaintAnimations()
    {
        foreach (var (entry, colorFrames) in _liftedEntries)
        {
            entry.Claimed = true;

            if (entry.Begin > TimeSpan.Zero)
            {
                var (deferredEntry, deferredFrames) = (entry, colorFrames);
                (_deferredStarts ??= new()).Add(() => StartPaintAnimation(deferredEntry, deferredFrames));
                continue;
            }

            StartPaintAnimation(entry, colorFrames);
        }
    }

    private void StartPaintAnimation(SvgAnimationEntry entry, Color[] colorFrames)
    {
        var brush = _liftedBrushes[(entry.Target, entry.AttributeName)];
        var frames = _compositor.CreateColorKeyFrameAnimation();
        Configure(frames, entry);

        var linear = new LinearEasing();
        foreach (var (key, index) in SvgCompositionAnimation.BuildKeyFrames(entry))
            frames.InsertKeyFrame(key, colorFrames[index], linear);

        brush.StartAnimation("Color", frames);
    }

    /// <summary>
    /// Starts delayed timelines one commit after the seed: a seeded base value
    /// and a started animation batched into the same commit let the animated
    /// flag swallow the write, losing the base value the SMIL begin delay must
    /// show. The one-commit skew on the begin offset is a frame at most.
    /// </summary>
    private async void StartAfterSeedCommit(List<Action> deferred)
    {
        await _compositor.RequestCommitAsync();
        if (_disposed)
            return;

        foreach (var start in deferred)
            start();
    }

    /// <summary>
    /// Plans and builds the lifted gradient definitions: for each definition
    /// whose timelines all lift, the static resolution seeds one shared
    /// composition gradient brush (bounds do not matter - eligibility excludes
    /// every bounds-dependent resolution path) and plain consumers paint with
    /// it at compile time.
    /// </summary>
    private void ClassifyGradientLifts()
    {
        Dictionary<SvgElement, List<SvgAnimationEntry>>? byGradient = null;
        foreach (var entry in _animator.Entries)
        {
            if (_animator.GetGradientElement(entry) is { } gradient)
            {
                byGradient ??= new Dictionary<SvgElement, List<SvgAnimationEntry>>();
                if (!byGradient.TryGetValue(gradient, out var list))
                    byGradient[gradient] = list = new List<SvgAnimationEntry>();
                list.Add(entry);
            }
        }

        if (byGradient == null)
            return;

        foreach (var pair in byGradient)
        {
            var gradient = pair.Key;
            if (gradient.GetAttribute("id") is not { Length: > 0 } id)
                continue;

            var context = new SvgCompileContext(_document, _viewport);
            var style = SvgStyle.CreateDefault(_viewport);
            if (SvgPaintServers.Resolve(context, id, style, new Rect(0, 0, 1, 1))
                is not ImmutableGradientBrush resolved)
            {
                continue;
            }

            if (SvgGradientLift.TryPlan(gradient, pair.Value, resolved, _viewport) is not { } plan)
                continue;

            var (brush, stops) = BuildGradientBrush(resolved);
            _liftedGradients[gradient] = brush;
            _gradientLifts.Add((plan, brush, stops));
        }
    }

    /// <summary>Builds the shared composition brush seeded from the static resolution.</summary>
    private (CompositionGradientBrush Brush, List<CompositionGradientStop> Stops) BuildGradientBrush(
        ImmutableGradientBrush resolved)
    {
        CompositionGradientBrush brush;
        if (resolved is ImmutableRadialGradientBrush radial)
        {
            var radialBrush = _compositor.CreateRadialGradientBrush();
            radialBrush.Center = radial.Center;
            radialBrush.GradientOrigin = radial.GradientOrigin;
            radialBrush.RadiusX = radial.RadiusX;
            radialBrush.RadiusY = radial.RadiusY;
            radialBrush.FocalRadius = radial.FocalRadius;
            brush = radialBrush;
        }
        else
        {
            var linear = (ImmutableLinearGradientBrush)resolved;
            var linearBrush = _compositor.CreateLinearGradientBrush();
            linearBrush.StartPoint = linear.StartPoint;
            linearBrush.EndPoint = linear.EndPoint;
            brush = linearBrush;
        }

        brush.SpreadMethod = resolved.SpreadMethod;
        brush.Transform = resolved.Transform;
        brush.TransformOrigin = resolved.TransformOrigin;
        brush.RelativeTransform = resolved.RelativeTransform;

        var stops = new List<CompositionGradientStop>(resolved.GradientStops.Count);
        foreach (var stop in resolved.GradientStops)
        {
            var compositionStop = _compositor.CreateGradientStop(stop.Offset, stop.Color);
            stops.Add(compositionStop);
            brush.GradientStops.Add(compositionStop);
        }

        return (brush, stops);
    }

    /// <summary>
    /// Starts the lifted gradient timelines as server-side key-frame
    /// animations on the shared brushes and their stops. Delayed begins defer
    /// past the seed commit like the paint lifts.
    /// </summary>
    private void StartGradientAnimations()
    {
        foreach (var (plan, brush, stops) in _gradientLifts)
        {
            foreach (var animation in plan.Animations)
            {
                animation.Entry.Claimed = true;

                // Zipped pair partners and superseded duplicates carry no
                // frames of their own.
                if (animation.Colors == null && animation.Scalars == null && animation.Points == null)
                    continue;

                if (animation.Entry.Begin > TimeSpan.Zero)
                {
                    var (deferredAnimation, deferredBrush, deferredStops, deferredUnits) =
                        (animation, brush, stops, plan.ObjectBoundingBox);
                    (_deferredStarts ??= new()).Add(() =>
                        StartGradientAnimation(deferredAnimation, deferredBrush, deferredStops, deferredUnits));
                    continue;
                }

                StartGradientAnimation(animation, brush, stops, plan.ObjectBoundingBox);
            }
        }
    }

    private void StartGradientAnimation(
        SvgGradientLiftAnimation animation,
        CompositionGradientBrush brush,
        List<CompositionGradientStop> stops,
        bool objectBoundingBox)
    {
        var unit = objectBoundingBox ? RelativeUnit.Relative : RelativeUnit.Absolute;
        var linear = new LinearEasing();
        var keyFrames = SvgCompositionAnimation.BuildKeyFrames(animation.Entry);

        switch (animation.Target)
        {
            case SvgGradientLiftTarget.StopColor:
            {
                var frames = _compositor.CreateColorKeyFrameAnimation();
                Configure(frames, animation.Entry);
                foreach (var (key, index) in keyFrames)
                    frames.InsertKeyFrame(key, animation.Colors![index], linear);
                stops[animation.StopIndex].StartAnimation("Color", frames);
                break;
            }

            case SvgGradientLiftTarget.StopOffset:
            {
                var frames = _compositor.CreateDoubleKeyFrameAnimation();
                Configure(frames, animation.Entry);
                foreach (var (key, index) in keyFrames)
                    frames.InsertKeyFrame(key, animation.Scalars![index], linear);
                stops[animation.StopIndex].StartAnimation("Offset", frames);
                break;
            }

            case SvgGradientLiftTarget.StartPoint:
            case SvgGradientLiftTarget.EndPoint:
            case SvgGradientLiftTarget.Center:
            case SvgGradientLiftTarget.GradientOrigin:
            {
                var frames = _compositor.CreateRelativePointKeyFrameAnimation();
                Configure(frames, animation.Entry);
                foreach (var (key, index) in keyFrames)
                    frames.InsertKeyFrame(key, new RelativePoint(animation.Points![index], unit), linear);
                brush.StartAnimation(animation.Target.ToString(), frames);
                break;
            }

            case SvgGradientLiftTarget.Radius:
            {
                // One r timeline drives both radii, matching the static
                // resolution that sets them from the single attribute.
                StartScalar("RadiusX");
                StartScalar("RadiusY");
                break;
            }

            case SvgGradientLiftTarget.FocalRadius:
            {
                StartScalar("FocalRadius");
                break;
            }
        }

        void StartScalar(string property)
        {
            var frames = _compositor.CreateRelativeScalarKeyFrameAnimation();
            Configure(frames, animation.Entry);
            foreach (var (key, index) in keyFrames)
                frames.InsertKeyFrame(key, new RelativeScalar(animation.Scalars![index], unit), linear);
            brush.StartAnimation(property, frames);
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

        foreach (var (entry, _) in _liftedEntries)
            entry.Claimed = false;

        foreach (var recording in _recordings)
            recording.Dispose();
        _recordings.Clear();
        _structuralSlices.Clear();

        // Composition objects expose Dispose internally only; the IVT lets the
        // host release its brushes so re-hosting does not accumulate server
        // objects for the compositor's lifetime.
        foreach (var brush in _liftedBrushes.Values)
        {
            brush.StopAnimation("Color");
            brush.Dispose();
        }

        _liftedBrushes.Clear();
        _liftedEntries.Clear();

        foreach (var (plan, brush, stops) in _gradientLifts)
        {
            foreach (var animation in plan.Animations)
                animation.Entry.Claimed = false;

            foreach (var stop in stops)
                stop.Dispose();
            brush.Dispose();
        }

        _gradientLifts.Clear();
        _liftedGradients.Clear();
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
