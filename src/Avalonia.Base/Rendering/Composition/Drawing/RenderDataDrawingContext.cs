using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Drawing.Nodes;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Drawing;

internal class RenderDataDrawingContext : DrawingContext
{
    private readonly Compositor? _compositor;
    private readonly bool _buildingRecording;
    private CompositionRenderData? _renderData;
    private HashSet<object>? _resourcesHashSet;
    private List<DrawingRecording>? _ownedRecordings;
    private HashSet<DrawingRecording>? _ownedRecordingsDedup;
    private bool _containsCompositorResources;
    private bool _containsMutableResources;
    private static readonly ThreadSafeObjectPool<HashSet<object>> s_hashSetPool = new();
    private CompositionRenderData RenderData
    {
        get
        {
            Debug.Assert(_compositor != null);
            return _renderData ??= new(_compositor);
        }
    }

    struct ParentStackItem
    {
        public RenderDataPushNode? Node;
        public List<IRenderDataItem> Items;
    }

    private List<IRenderDataItem>? _currentItemList;
    private static readonly ThreadSafeObjectPool<List<IRenderDataItem>> s_listPool = new();

    private Stack<ParentStackItem>? _parentNodeStack;
    private static readonly ThreadSafeObjectPool<Stack<ParentStackItem>> s_parentStackPool = new();

    /// <param name="compositor">The compositor whose server resources the recorded
    /// content binds to, or null for content that only uses immutable resources.</param>
    /// <param name="buildingRecording">True when this context builds a long-lived
    /// <see cref="DrawingRecording"/>. Recording-building contexts honor
    /// <see cref="DrawingRecordingOwnership.Owned"/> registrations, and — when
    /// <paramref name="compositor"/> is null — enforce that captured resources are
    /// (or can be snapshotted to) immutable so the recording can be replayed on the
    /// render thread. Transient contexts (the visual-content recorder, immediate
    /// scene-brush content) capture resources as-is.</param>
    public RenderDataDrawingContext(Compositor? compositor, bool buildingRecording = false)
    {
        _compositor = compositor;
        _buildingRecording = buildingRecording;
    }

    void Add(IRenderDataItem item)
    {
        _currentItemList ??= s_listPool.Get();
        _currentItemList.Add(item);
    }

    void Push(RenderDataPushNode? node = null)
    {
        // Push a fake no-op node so something could be popped by the corresponding Pop call
        // Since there is no nesting, we don't update the item list
        if (node == null)
        {
            (_parentNodeStack ??= s_parentStackPool.Get()).Push(default);
            return;
        }    
        Add(node);
        (_parentNodeStack ??= s_parentStackPool.Get()).Push(new ParentStackItem
        {
            Node = node,
            Items = _currentItemList!
        });
        _currentItemList = null;
    }

    void Pop<T>() where T : IRenderDataItem
    {
        var parent = _parentNodeStack!.Pop();
        
        // No-op node
        if (parent.Node == null)
            return;

        if (!(parent.Node is T))
            throw new InvalidOperationException("Invalid Pop operation");

        // Empty push/pop pairs are elided from the recording — except nodes
        // that render output even without children (effect layers).
        var removeLastPush = !parent.Node.ProducesOutputWithoutChildren;
        if (_currentItemList != null)
        {
            removeLastPush &= _currentItemList.Count == 0;
            foreach (var item in _currentItemList)
                parent.Node.Children.Add(item);
            _currentItemList.Clear();
            s_listPool.ReturnAndSetNull(ref _currentItemList);
        }
        _currentItemList = parent.Items;
        if (removeLastPush)
            _currentItemList.RemoveAt(_currentItemList.Count - 1);
    }

    void AddResource(object? resource)
    {
        if (_compositor == null)
            return;

        if (resource == null
            || resource is IImmutableBrush
            || resource is ImmutablePen
            || resource is ImmutableTransform)
            return;

        if (resource is ICompositionRenderResource renderResource)
        {
            _resourcesHashSet ??= s_hashSetPool.Get();
            if (!_resourcesHashSet.Add(renderResource))
                return;

            renderResource.AddRefOnCompositor(_compositor);
            RenderData.AddResource(renderResource);
            return;
        }

        throw new InvalidOperationException(resource.GetType().FullName + " can not be used with this DrawingContext");
    }

    /// <summary>
    /// Captures a brush for a recorded node. Compositor-bound contexts register the
    /// brush as a composition resource and store its server-side counterpart. Contexts
    /// building an immutable <see cref="DrawingRecording"/> snapshot the brush (see
    /// <see cref="SnapshotBrush"/>) so the recording stays valid and thread-safe when
    /// replayed on the render thread. Transient contexts capture the brush as-is and
    /// only track whether non-immutable resources were seen.
    /// </summary>
    IBrush? CaptureBrush(IBrush? brush)
    {
        if (brush == null)
            return null;

        if (_compositor != null)
        {
            AddResource(brush);
            return brush.GetServer(_compositor);
        }

        if (!_buildingRecording)
        {
            if (brush is not IImmutableBrush)
                _containsMutableResources = true;
            return brush;
        }

        return SnapshotBrush(brush);
    }

    /// <summary>
    /// Captures a pen, returning the client-side instance (used for bounds and
    /// hit-test queries) and the server-side instance (used at replay). For
    /// immutable recordings both are the same immutable snapshot.
    /// </summary>
    (IPen? Client, IPen? Server) CapturePen(IPen? pen)
    {
        if (pen == null)
            return (null, null);

        if (_compositor != null)
        {
            AddResource(pen);
            return (pen, pen.GetServer(_compositor));
        }

        if (!_buildingRecording)
        {
            if (pen is not ImmutablePen)
                _containsMutableResources = true;
            return (pen, pen);
        }

        var snapshot = SnapshotPen(pen);
        return (snapshot, snapshot);
    }

    /// <summary>
    /// Snapshots a brush for capture into an immutable <see cref="DrawingRecording"/>.
    /// Immutable brushes pass through; everything else converts via
    /// <see cref="BrushExtensions.ToImmutable(IBrush)"/> (mutable brushes are cloned;
    /// scene brushes are resolved to their current content), so no
    /// <see cref="AvaloniaObject"/> is touched at replay time. Throws for brushes
    /// that cannot be made immutable and for scene-brush content that references
    /// compositor-bound or mutable resources.
    /// </summary>
    IImmutableBrush SnapshotBrush(IBrush brush)
    {
        switch (brush)
        {
            case IImmutableBrush immutable:
                return immutable;
            case ISceneBrush:
            case IMutableBrush:
            {
                var snapshot = brush.ToImmutable();
                ThrowIfRestrictedSceneContent(snapshot, brush);
                return snapshot;
            }
            default:
                throw new InvalidOperationException(
                    brush.GetType() + " cannot be captured by an immutable DrawingRecording. Use an immutable brush.");
        }
    }

    /// <summary>
    /// Snapshots a pen for capture into an immutable <see cref="DrawingRecording"/>
    /// via <see cref="BrushExtensions.ToImmutable(IPen)"/>.
    /// </summary>
    IPen SnapshotPen(IPen pen)
    {
        switch (pen)
        {
            case ImmutablePen immutable:
                return immutable;
            case Pen:
            {
                var snapshot = pen.ToImmutable();
                ThrowIfRestrictedSceneContent(snapshot.Brush, pen);
                return snapshot;
            }
            default:
                throw new InvalidOperationException(
                    pen.GetType() + " cannot be captured by an immutable DrawingRecording. Use ImmutablePen.");
        }
    }

    /// <summary>
    /// Rejects scene-brush content snapshots that an immutable recording must not
    /// embed: content referencing compositor-bound render data (could be neither
    /// retained nor tracked) or live mutable resources (unsafe to read at replay
    /// time on the render thread).
    /// </summary>
    static void ThrowIfRestrictedSceneContent(IBrush? snapshot, object source)
    {
        if (snapshot is EmbeddedSceneBrushContent { ContainsCompositorResources: true })
            throw new InvalidOperationException(
                source.GetType().Name + " content references compositor-bound resources and cannot be " +
                "captured by an immutable DrawingRecording. Use DrawingRecording.Create(Compositor, ...) instead.");
        if (snapshot is EmbeddedSceneBrushContent { ContainsMutableResources: true })
            throw new InvalidOperationException(
                source.GetType().Name + " content references mutable resources and cannot be captured " +
                "by an immutable DrawingRecording. Use immutable brushes and pens inside the brush content.");
    }

    protected override void DrawLineCore(IPen? pen, Point p1, Point p2)
    {
        if(pen == null)
            return;
        var (clientPen, serverPen) = CapturePen(pen);
        Add(new RenderDataLineNode
        {
            ClientPen = clientPen,
            ServerPen = serverPen,
            P1 = p1,
            P2 = p2
        });
    }

    protected override void DrawGeometryCore(IBrush? brush, IPen? pen, IGeometryImpl geometry)
    {
        if (brush == null && pen == null)
            return;
        var (clientPen, serverPen) = CapturePen(pen);
        Add(new RenderDataGeometryNode
        {
            ServerBrush = CaptureBrush(brush),
            ServerPen = serverPen,
            ClientPen = clientPen,
            Geometry = geometry
        });
    }

    protected override void DrawRectangleCore(IBrush? brush, IPen? pen, RoundedRect rrect, BoxShadows boxShadows = default)
    {
        if (rrect.IsEmpty())
            return;
        if(brush == null && pen == null && boxShadows == default)
            return;
        var (clientPen, serverPen) = CapturePen(pen);
        Add(new RenderDataRectangleNode
        {
            ServerBrush = CaptureBrush(brush),
            ServerPen = serverPen,
            ClientPen = clientPen,
            Rect = rrect,
            BoxShadows = boxShadows
        });
    }

    protected override void DrawEllipseCore(IBrush? brush, IPen? pen, Rect rect)
    {
        if (rect.IsEmpty())
            return;

        if(brush == null && pen == null)
            return;
        var (clientPen, serverPen) = CapturePen(pen);
        Add(new RenderDataEllipseNode
        {
            ServerBrush = CaptureBrush(brush),
            ServerPen = serverPen,
            ClientPen = clientPen,
            Rect = rect,
        });
    }

    public override void Custom(ICustomDrawOperation custom) => Add(new RenderDataCustomNode
    {
        Operation = custom
    });

    public override void DrawGlyphRun(IBrush? foreground, GlyphRun? glyphRun)
    {
        if (foreground == null || glyphRun == null)
            return;
        Add(new RenderDataGlyphRunNode
        {
            ServerBrush = CaptureBrush(foreground),
            GlyphRun = glyphRun.PlatformImpl.Clone()
        });
    }

    protected override void PushClipCore(RoundedRect rect) => Push(new RenderDataClipNode
    {
        Rect = rect
    });

    protected override void PushClipCore(Rect rect) => Push(new RenderDataClipNode
    {
        Rect = rect
    });

    protected override void PushGeometryClipCore(Geometry? clip)
    {
        if (clip == null)
            Push();
        else
            Push(new RenderDataGeometryClipNode
            {
                Geometry = clip?.PlatformImpl
            });
    }

    protected override void PushOpacityCore(double opacity)
    {
        if (opacity == 1)
            Push();
        else
            Push(new RenderDataOpacityNode
            {
                Opacity = opacity
            });
    }

    protected override void PushLayerCore(LayerOptions options)
    {
        if (options.IsPassthrough)
        {
            Push();
            return;
        }

        // The recorded node replays on the render thread and is not registered as a
        // composition resource, so the effect must be captured as an immutable
        // snapshot regardless of the context mode.
        if (options.Effect is { } effect && effect is not IImmutableEffect)
        {
            options = options with
            {
                Effect = effect is IMutableEffect
                    ? effect.ToImmutable()
                    : throw new InvalidOperationException(
                        effect.GetType() + " cannot be recorded. LayerOptions.Effect must be an " +
                        "immutable effect or a mutable effect that supports snapshotting.")
            };
        }

        Push(new RenderDataLayerNode
        {
            Options = options
        });
    }

    protected override void PopLayerCore() => Pop<RenderDataLayerNode>();

    protected override void PushOpacityMaskCore(IBrush? mask, Rect bounds)
        => PushOpacityMaskCore(mask, bounds, MaskType.Alpha);

    protected override void PushOpacityMaskCore(IBrush? mask, Rect bounds, MaskType maskType)
    {
        if (mask == null)
            Push();
        else
        {
            Push(new RenderDataOpacityMaskNode
            {
                ServerBrush = CaptureBrush(mask),
                BoundsRect = bounds,
                MaskType = maskType
            });
        }
    }

    protected override void PushTransformCore(Matrix matrix)
    {
        if (matrix.IsIdentity)
            Push();
        else
            Push(new RenderDataPushMatrixNode()
            {
                Matrix = matrix
            });
    }

    protected override void PushRenderOptionsCore(RenderOptions renderOptions) => Push(new RenderDataRenderOptionsNode()
    {
        RenderOptions = renderOptions
    });

    protected override void PushTextOptionsCore(TextOptions textOptions) => Push(new RenderDataTextOptionsNode()
    {
        TextOptions = textOptions
    });

    /// <inheritdoc />
    protected override void PushEffectCore(IEffect effect, Rect bounds) => Push(new RenderDataEffectNode()
    {
        Effect = effect.ToImmutable(),
        BoundsRect = bounds.Inflate(effect.GetEffectOutputPadding())
    });

    protected override void PopClipCore() => Pop<RenderDataClipNode>();

    protected override void PopGeometryClipCore() => Pop<RenderDataGeometryClipNode>();

    protected override void PopOpacityCore() => Pop<RenderDataOpacityNode>();

    protected override void PopOpacityMaskCore() => Pop<RenderDataOpacityMaskNode>();

    protected override void PopTransformCore() => Pop<RenderDataPushMatrixNode>();

    protected override void PopRenderOptionsCore() => Pop<RenderDataRenderOptionsNode>();

    protected override void PopTextOptionsCore() => Pop<RenderDataTextOptionsNode>();

    /// <inheritdoc />
    protected override void PopEffectCore() => Pop<RenderDataEffectNode>();

    internal override void DrawBitmap(IRef<IBitmapImpl>? source, double opacity, Rect sourceRect, Rect destRect)
    {
        if (source == null || sourceRect.IsEmpty() || destRect.IsEmpty())
            return;
        Add(new RenderDataBitmapNode
        {
            Bitmap = source.Clone(),
            Opacity = opacity,
            SourceRect = sourceRect,
            DestRect = destRect
        });
    }

    internal override void RegisterOwnedRecording(DrawingRecording recording)
    {
        // Ownership is honored only when this context builds a DrawingRecording
        // (per the DrawingRecordingOwnership contract). The shared visual-content
        // recorder and transient scene-brush contents leave disposal to the caller.
        if (!_buildingRecording)
            return;
        _ownedRecordingsDedup ??= new();
        if (!_ownedRecordingsDedup.Add(recording))
            return;
        (_ownedRecordings ??= new()).Add(recording);
    }

    /// <summary>
    /// Returns (and clears) the list of <see cref="DrawingRecordingOwnership.Owned"/>
    /// child recordings registered during this context's lifetime, for transfer to
    /// the resulting <see cref="DrawingRecording"/>.
    /// </summary>
    public IReadOnlyList<DrawingRecording>? TakeOwnedRecordings()
    {
        var list = _ownedRecordings;
        _ownedRecordings = null;
        _ownedRecordingsDedup?.Clear();
        _ownedRecordingsDedup = null;
        return list;
    }

    internal override void DrawRecordingCore(DrawingRecording recording) =>
        DrawRecordingCore(recording, Matrix.Identity);

    internal override void DrawRecordingCore(DrawingRecording recording, Matrix transform)
    {
        if (recording.IsCompositorBound)
        {
            if (_compositor != null && recording.Compositor != _compositor)
                throw new InvalidOperationException(
                    "Cannot draw a compositor-bound DrawingRecording into a context belonging to a different compositor.");

            if (_compositor == null)
            {
                if (_buildingRecording)
                    throw new InvalidOperationException(
                        "An immutable DrawingRecording cannot reference a compositor-bound DrawingRecording: " +
                        "it would neither retain nor track the compositor-bound content. " +
                        "Use DrawingRecording.Create(Compositor, ...) for the enclosing recording instead.");
                _containsCompositorResources = true;
            }

            recording.EnsureRegisteredForSerialization();
            var renderData = recording.RenderData!;
            AddResource(new CompositionRenderDataResourceRef(renderData));
            Add(new RenderDataRecordingCompositionNode
            {
                Server = renderData.Server,
                Client = renderData,
                Transform = transform
            });
        }
        else
        {
            Add(new RenderDataRecordingItemListNode { Items = recording.ItemList!, Transform = transform });
        }
    }


    void FlushStack()
    {
        // Flush stack
        if (_parentNodeStack != null)
        {
            // TODO: throw error, unbalanced stack
            while (_parentNodeStack.Count > 0) 
                Pop<IRenderDataItem>();
        }
        

    }
    
    public CompositionRenderData? GetRenderResults()
    {
        var rv = GetRenderResultsCore();
        if (rv != null)
            _compositor!.RegisterForSerialization(rv);
        return rv;
    }

    internal CompositionRenderData? GetRenderResultsWithoutRegistration()
    {
        return GetRenderResultsCore();
    }

    private CompositionRenderData? GetRenderResultsCore()
    {
        Debug.Assert(_compositor != null);

        FlushStack();

        // Transfer items to RenderData
        if (_currentItemList is { Count: > 0 })
        {
            foreach (var i in _currentItemList)
                RenderData.Add(i);
            _currentItemList.Clear();
        }

        var rv = _renderData;
        _renderData = null;
        _resourcesHashSet?.Clear();

        return rv;
    }

    public RenderItemList GetRenderItemList()
    {
        Debug.Assert(_compositor == null);
        Debug.Assert(_resourcesHashSet == null);
        Debug.Assert(_renderData == null);

        FlushStack();

        var list = new RenderItemList();
        if (_currentItemList is { Count: > 0 })
        {
            foreach (var i in _currentItemList)
                list.Add(i);
            _currentItemList.Clear();
        }

        return list;
    }

    public ImmediateRenderDataSceneBrushContent? GetImmediateSceneBrushContent(ITileBrush brush, Rect? rect, bool useScalableRasterization)
    {
        Debug.Assert(_compositor == null);
        Debug.Assert(_resourcesHashSet == null);
        Debug.Assert(_renderData == null);
        
        FlushStack();
        if (_currentItemList == null || _currentItemList.Count == 0)
            return null;

        var itemList = _currentItemList;
        _currentItemList = null;

        return new ImmediateRenderDataSceneBrushContent(brush, itemList, rect, useScalableRasterization, s_listPool,
            _containsCompositorResources, _containsMutableResources);
    }

    public void Reset()
    {
        // This means that render data should be discarded
        if (_renderData != null)
        {
            _renderData.Dispose();
            _renderData = null;
        }

        // Ownership of these children was transferred by DrawRecording(..., Owned).
        // If they are still here the discarded work never reached a DrawingRecording
        // (e.g. the record delegate threw), so disposing them is this context's job.
        if (_ownedRecordings != null)
        {
            foreach (var owned in _ownedRecordings)
                owned.Dispose();
            _ownedRecordings = null;
        }
        _ownedRecordingsDedup?.Clear();

        _currentItemList?.Clear();
        _parentNodeStack?.Clear();
        _resourcesHashSet?.Clear();
        _containsCompositorResources = false;
        _containsMutableResources = false;
    }
    
    protected override void DisposeCore()
    {
        Reset();
        if (_resourcesHashSet != null) 
            s_hashSetPool.ReturnAndSetNull(ref _resourcesHashSet);
    }
}
