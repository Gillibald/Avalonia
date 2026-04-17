using System;
using Avalonia.Media;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;

namespace Avalonia.Rendering.Composition;

/// <summary>
/// An immutable recorded draw list that can be replayed with minimal overhead.
/// Created via <see cref="DrawingRecording.Create(System.Action{DrawingContext})"/> for immutable resources
/// or <see cref="DrawingRecording.Create(Compositor, System.Action{DrawingContext})"/> for compositor-bound
/// resources that support animations and change tracking.
/// </summary>
public sealed class DrawingRecording : IDisposable
{
    private readonly RenderItemList? _items;
    private readonly CompositionRenderData? _renderData;
    private bool _registeredForSerialization;
    private bool _disposed;

    internal DrawingRecording(RenderItemList items)
    {
        _items = items;
    }

    internal DrawingRecording(Compositor compositor, CompositionRenderData renderData)
    {
        Compositor = compositor;
        _renderData = renderData;
    }

    /// <summary>
    /// Creates a new <see cref="DrawingRecording"/> with immutable resources.
    /// No compositor is required. Only immutable brushes and pens are supported.
    /// </summary>
    public static DrawingRecording Create(Action<DrawingContext> record)
    {
        _ = record ?? throw new ArgumentNullException(nameof(record));

        using var context = new RenderDataDrawingContext(null);
        record(context);

        var items = context.GetRenderItemList();
        return new DrawingRecording(items);
    }

    /// <summary>
    /// Creates a new <see cref="DrawingRecording"/> bound to a compositor.
    /// Supports mutable resources (animated brushes, pens) with automatic change tracking.
    /// </summary>
    public static DrawingRecording Create(Compositor compositor, Action<DrawingContext> record)
    {
        _ = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _ = record ?? throw new ArgumentNullException(nameof(record));

        using var context = new RenderDataDrawingContext(compositor);
        record(context);

        var renderData = context.GetRenderResultsWithoutRegistration()
            ?? new CompositionRenderData(compositor);

        return new DrawingRecording(compositor, renderData);
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
    /// Available synchronously after <see cref="Create(System.Action{DrawingContext})"/> or
    /// <see cref="Create(Compositor, System.Action{DrawingContext})"/> returns — no compositor
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _renderData?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DrawingRecording));
    }
}
