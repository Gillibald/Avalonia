using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;

namespace Avalonia.Rendering.Composition;

/// <summary>
/// An immutable recorded draw list that can be replayed with minimal overhead.
/// Created via <see cref="DrawingRecording.Create(System.Action{DrawingRecordingContext})"/>
/// for immutable resources or
/// <see cref="DrawingRecording.Create(Compositor, System.Action{DrawingRecordingContext})"/>
/// for compositor-bound resources that support animations and change tracking.
/// </summary>
public sealed class DrawingRecording : IDisposable
{
    private readonly RenderItemList? _items;
    private readonly CompositionRenderData? _renderData;
    private readonly IReadOnlyList<DrawingRecording>? _ownedChildren;
    private bool _registeredForSerialization;
    private bool _disposed;
    private EventHandler<Rect>? _boundsChanged;
    private bool _subscribedToAfterCommit;
    private Rect _lastObservedBounds;

    internal DrawingRecording(RenderItemList items, IReadOnlyList<DrawingRecording>? ownedChildren = null)
    {
        _items = items;
        _ownedChildren = ownedChildren;
    }

    internal DrawingRecording(Compositor compositor, CompositionRenderData renderData,
        IReadOnlyList<DrawingRecording>? ownedChildren = null)
    {
        Compositor = compositor;
        _renderData = renderData;
        _ownedChildren = ownedChildren;
    }

    /// <summary>
    /// Creates a new <see cref="DrawingRecording"/> with immutable resources.
    /// No compositor is required. Only immutable brushes and pens are supported.
    /// </summary>
    public static DrawingRecording Create(Action<DrawingRecordingContext> record)
    {
        _ = record ?? throw new ArgumentNullException(nameof(record));

        using var context = new RenderDataDrawingContext(null);
        record(context);

        var items = context.GetRenderItemList();
        var owned = context.TakeOwnedRecordings();
        return new DrawingRecording(items, owned);
    }

    /// <summary>
    /// Creates a new <see cref="DrawingRecording"/> bound to a compositor.
    /// Supports mutable resources (animated brushes, pens) with automatic change tracking.
    /// </summary>
    public static DrawingRecording Create(Compositor compositor, Action<DrawingRecordingContext> record)
    {
        _ = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _ = record ?? throw new ArgumentNullException(nameof(record));

        using var context = new RenderDataDrawingContext(compositor);
        record(context);

        var renderData = context.GetRenderResultsWithoutRegistration()
            ?? new CompositionRenderData(compositor);
        var owned = context.TakeOwnedRecordings();

        return new DrawingRecording(compositor, renderData, owned);
    }

    /// <summary>
    /// The compositor this recording is bound to, or null for immutable recordings.
    /// </summary>
    public Compositor? Compositor { get; }

    /// <summary>
    /// Whether this recording is bound to a compositor and supports mutable resources.
    /// </summary>
    internal bool IsCompositorBound => _renderData != null;

    /// <summary>
    /// Gets the bounds of the recorded content in the recording's local coordinate space.
    /// Available synchronously after <see cref="Create(System.Action{DrawingRecordingContext})"/> or
    /// <see cref="Create(Compositor, System.Action{DrawingRecordingContext})"/> returns — no compositor
    /// commit is required. Returns a default (empty) <see cref="Rect"/> if the recording
    /// contains no drawn content.
    /// </summary>
    public Rect Bounds
    {
        get
        {
            ThrowIfDisposed();
            if (_renderData != null)
                return _renderData.Bounds ?? default;
            return _items!.Bounds ?? default;
        }
    }

    /// <summary>
    /// Gets the bounds of the recorded content after applying <paramref name="transform"/>
    /// to each drawn item. Produces tighter bounds than <see cref="Bounds"/>.TransformToAABB
    /// for rotations and skews over recordings containing multiple items, since each item's
    /// bounds are transformed individually before being unioned. Returns a default (empty)
    /// <see cref="Rect"/> if the recording contains no drawn content.
    /// </summary>
    /// <param name="transform">The outer transform to apply. When identity, equivalent to
    /// <see cref="Bounds"/>.</param>
    public Rect GetBounds(Matrix transform)
    {
        ThrowIfDisposed();
        if (_renderData != null)
            return _renderData.GetBounds(transform) ?? default;
        return _items!.GetBounds(transform) ?? default;
    }

    /// <summary>
    /// Whether this recording has been disposed.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// The render item list for immutable recordings.
    /// </summary>
    internal RenderItemList? ItemList => _disposed ? null : _items;

    /// <summary>
    /// The composition render data for compositor-bound recordings.
    /// </summary>
    internal CompositionRenderData? RenderData => _disposed ? null : _renderData;

    /// <summary>
    /// The server-side render data for compositor-bound recordings.
    /// </summary>
    internal ServerCompositionRenderData? ServerRenderData
    {
        get
        {
            ThrowIfDisposed();
            return _renderData?.Server;
        }
    }

    /// <summary>
    /// Ensures the compositor-bound render data is registered for serialization.
    /// Called lazily on first use to avoid leaking server resources for unused recordings.
    /// </summary>
    internal void EnsureRegisteredForSerialization()
    {
        if (!_registeredForSerialization && _renderData != null && Compositor != null)
        {
            Compositor.RegisterForSerialization(_renderData);
            _registeredForSerialization = true;
        }
    }

    /// <summary>
    /// Hit tests the recorded content against a point.
    /// </summary>
    public bool HitTest(Point point)
    {
        ThrowIfDisposed();
        if (_renderData != null)
            return _renderData.HitTest(point);
        return _items!.HitTest(point);
    }

    /// <summary>
    /// Returns the tag values pushed via
    /// <see cref="DrawingRecordingContext.PushElementTag"/> whose subtree contains
    /// <paramref name="point"/>. Traversal is in document order: nested tags precede
    /// their containing tag in the result. Sibling tags appear in the order they
    /// were drawn; consumers that want top-most-first (e.g. SVG
    /// <c>pointer-events</c> semantics) should reverse the returned enumeration.
    /// Returns an empty enumerable if no tagged content contains the point, or if
    /// the recording contains no tags at all.
    /// </summary>
    public IEnumerable<object> HitTestElements(Point point)
    {
        ThrowIfDisposed();
        var results = new List<object>();
        if (_renderData != null)
            _renderData.CollectHitTestTags(point, results);
        else
            _items!.CollectHitTestTags(point, results);
        return results;
    }

    /// <summary>
    /// Raised on the UI thread after a compositor commit when <see cref="Bounds"/>
    /// has changed since the previous commit (or since the first subscription).
    /// Supported on compositor-bound recordings only; immutable recordings never
    /// change bounds and subscribing throws.
    ///
    /// Fires at most once per commit. The event handler receives the new bounds.
    /// </summary>
    public event EventHandler<Rect> BoundsChanged
    {
        add
        {
            ThrowIfDisposed();
            if (Compositor == null)
                throw new InvalidOperationException(
                    "BoundsChanged is only supported on compositor-bound DrawingRecordings.");
            _boundsChanged += value;
            EnsureSubscribedToAfterCommit();
        }
        remove
        {
            _boundsChanged -= value;
            if (_boundsChanged == null)
                UnsubscribeFromAfterCommit();
        }
    }

    private void EnsureSubscribedToAfterCommit()
    {
        if (_subscribedToAfterCommit || Compositor == null)
            return;
        _lastObservedBounds = Bounds;
        Compositor.AfterCommit += OnCompositorAfterCommit;
        _subscribedToAfterCommit = true;
    }

    private void UnsubscribeFromAfterCommit()
    {
        if (!_subscribedToAfterCommit || Compositor == null)
            return;
        Compositor.AfterCommit -= OnCompositorAfterCommit;
        _subscribedToAfterCommit = false;
    }

    private void OnCompositorAfterCommit()
    {
        if (_disposed)
            return;
        var current = Bounds;
        if (current != _lastObservedBounds)
        {
            _lastObservedBounds = current;
            _boundsChanged?.Invoke(this, current);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            UnsubscribeFromAfterCommit();
            _boundsChanged = null;
            _renderData?.Dispose();
            if (_ownedChildren != null)
            {
                foreach (var child in _ownedChildren)
                    child.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DrawingRecording));
    }
}
