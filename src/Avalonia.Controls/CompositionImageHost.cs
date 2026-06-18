using System;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Avalonia.Controls;

/// <summary>
/// Hosts an <see cref="ICompositionImage"/> source as a render-thread-animating
/// child composition visual under a control: it materializes one instance per
/// host, attaches the instance's visual, keeps its stretch transform current,
/// and runs a per-frame clock only while the instance still needs one. Sources
/// that are not composition images (or composition images with nothing to host)
/// fall through, so the owner draws them statically via <see cref="IImage.Draw"/>.
/// Shared by the <see cref="Image"/> control and the SVG control.
/// </summary>
internal sealed class CompositionImageHost
{
    private readonly Visual _owner;
    private IImage? _source;
    private ICompositionImageInstance? _instance;
    private bool _instanceCreated;
    private bool _childVisualAttached;
    private Action<TimeSpan>? _clockFrame;
    private bool _clockRunning;
    private TimeSpan? _clockStart;

    public CompositionImageHost(Visual owner) => _owner = owner;

    /// <summary>Whether a composition instance is currently hosting the source.</summary>
    public bool IsHosting => _instance != null;

    /// <summary>Sets the current source, tearing down the instance built for any previous one.</summary>
    public void SetSource(IImage? source)
    {
        if (ReferenceEquals(_source, source))
            return;

        ReleaseInstance();
        _source = source;
        _instanceCreated = false;
    }

    /// <summary>
    /// Materializes the instance (once a compositor is available) and attaches its
    /// visual. Safe to call repeatedly; the owner should call it from both attach
    /// and the layout pass, since the layout pass is the first point where the
    /// compositor is reliably available yet still outside the render pass.
    /// </summary>
    public void EnsureAttached()
    {
        EnsureInstance();
        AttachChildVisual();
        // Re-arm the residual clock after a detach/re-attach; idempotent while
        // it is already running or when the instance is fully server-side.
        StartClock();
    }

    public void Detach()
    {
        StopClock();
        DetachChildVisual();
    }

    /// <summary>
    /// Updates the hosted visual's stretch and returns true when a composition
    /// instance is hosting — the owner then skips its static draw. Returns false
    /// for a static source, so the owner draws it normally.
    /// </summary>
    public bool TryHost(Rect bounds, Size sourceSize, Stretch stretch, StretchDirection direction)
    {
        EnsureInstance();
        if (_instance is null)
            return false;

        ComputeStretch(bounds, sourceSize, stretch, direction, out var sourceRect, out var destRect, out var scale);
        _instance.SetStretchTransform(
            Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y)
            * Matrix.CreateScale(scale.X, scale.Y)
            * Matrix.CreateTranslation(destRect.X, destRect.Y));

        // Attaching invalidates, so it must never happen inside the render pass.
        if (!_childVisualAttached)
            Dispatcher.UIThread.Post(AttachChildVisual, DispatcherPriority.Render);

        return true;
    }

    public void Dispose() => ReleaseInstance();

    private void EnsureInstance()
    {
        if (_instanceCreated)
            return;
        if (_source is not ICompositionImage compositionImage)
            return;
        if (GetCompositor() is not { } compositor)
            return; // not attached yet — retry on attach / next render

        _instanceCreated = true;
        _instance = compositionImage.CreateInstance(compositor);
        StartClock();
    }

    private void AttachChildVisual()
    {
        if (_instance is null || _childVisualAttached || _owner.VisualRoot is null)
            return;

        ElementComposition.SetElementChildVisual(_owner, _instance.Visual);
        _childVisualAttached = true;
    }

    private void DetachChildVisual()
    {
        if (!_childVisualAttached)
            return;

        ElementComposition.SetElementChildVisual(_owner, null);
        _childVisualAttached = false;
    }

    private void StartClock()
    {
        if (_instance is not { NeedsClock: true } || _clockRunning
            || TopLevel.GetTopLevel(_owner) is not { } topLevel)
        {
            return;
        }

        _clockRunning = true;
        _clockFrame ??= OnClock;
        topLevel.RequestAnimationFrame(_clockFrame);
    }

    private void StopClock()
    {
        _clockRunning = false;
        _clockStart = null;
    }

    private void OnClock(TimeSpan time)
    {
        if (!_clockRunning || _instance is null)
            return;

        // The first tick anchors the document timeline.
        _clockStart ??= time;

        // Structural slices re-compile into their own visuals and paint brushes
        // mutate server-side, so no UI-thread invalidation is needed.
        _instance.OnClock(time - _clockStart.Value);

        if (TopLevel.GetTopLevel(_owner) is { } topLevel)
            topLevel.RequestAnimationFrame(_clockFrame!);
        else
            _clockRunning = false;
    }

    private void ReleaseInstance()
    {
        StopClock();
        DetachChildVisual();
        _instance?.Dispose();
        _instance = null;
        _instanceCreated = false;
    }

    // The render root is composited before any of its children, so its visual
    // reliably carries the compositor — unlike the owner's own composition
    // visual, which is not attached until the first compose pass.
    private Compositor? GetCompositor() =>
        _owner.VisualRoot is { } root ? ElementComposition.GetElementVisual(root)?.Compositor : null;

    private static void ComputeStretch(
        Rect bounds, Size sourceSize, Stretch stretch, StretchDirection direction,
        out Rect sourceRect, out Rect destRect, out Vector scale)
    {
        var viewPort = new Rect(bounds.Size);
        scale = stretch.CalculateScaling(bounds.Size, sourceSize, direction);
        var scaledSize = sourceSize * scale;
        destRect = viewPort.CenterRect(new Rect(scaledSize)).Intersect(viewPort);
        sourceRect = scale.X > 0 && scale.Y > 0
            ? new Rect(sourceSize).CenterRect(new Rect(destRect.Size / scale))
            : default;
    }
}
