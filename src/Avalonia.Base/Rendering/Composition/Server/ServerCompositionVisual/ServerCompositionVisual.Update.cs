using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Avalonia.Rendering.Composition.Server;

internal partial class ServerCompositionVisual
{
    protected virtual bool HasEffect => Effect != null;
    
    struct UpdateContext : IServerTreeVisitor, IDisposable
    {
        private TreeWalkContext _context;

        private IDirtyRectCollector _dirtyRegion;
        private int _dirtyRegionDisableCount;
        private Stack<int> _dirtyRegionDisableCountStack;
        private Stack<IDirtyRectCollector> _dirtyRegionCollectorStack;
        private Stack<LtrbRect?> _localizedEffectOldBoundsStack;
        private readonly List<BackdropCapture>? _backdropCaptures;
        private readonly List<BackdropHostRecord>? _backdropHosts;
        private readonly List<LtrbRect>? _backdropDirtRects;
        private readonly IDirtyRectCollector _rootCollector;
        private readonly Matrix _rootTransform;
        // The bitmap-cache host the walk is currently inside (null = the render
        // target itself) and its collector. Unlike _dirtyRegion these are not
        // touched by the localized-effect union redirect: a backdrop under one
        // still belongs to its cache (or root) host.
        private ServerCompositionVisual? _currentHostOwner;
        private IDirtyRectCollector _currentHostCollector;
        private Stack<ServerCompositionVisual?>? _hostOwnerStack;
        private Stack<IDirtyRectCollector>? _hostCollectorStack;
        // Walk position: emission order equals paint order, so a rect present in
        // the root collector's working set at a given position painted at or
        // beneath that position.
        private int _dfsCounter;
        // Number of ancestors whose extra dirty rect (a removed or reparented
        // child's old bounds) is still pending: it only lands in the working set
        // at the ancestor's PostSubgraph, and the vanished content may have
        // painted beneath a backdrop captured before that.
        private int _extraDirtyAncestorCount;
        private List<LtrbRect>? _workingSetBuffer;
        private bool AreDirtyRegionsDisabled() => _dirtyRegionDisableCount != 0;

        public UpdateContext(CompositorPools pools, IDirtyRectCollector dirtyRects, Matrix transform, LtrbRect clip,
            List<BackdropCapture>? backdropCaptures, List<BackdropHostRecord>? backdropHosts,
            List<LtrbRect>? backdropDirtRects)
        {
            _dirtyRegion = dirtyRects;
            _rootCollector = dirtyRects;
            _currentHostCollector = dirtyRects;
            _rootTransform = transform;
            _backdropCaptures = backdropCaptures;
            _backdropHosts = backdropHosts;
            _backdropDirtRects = backdropDirtRects;
            _context = new TreeWalkContext(pools, transform, clip);
            _dirtyRegionDisableCountStack = pools.IntStackPool.Rent();
            _dirtyRegionCollectorStack = pools.DirtyRectCollectorStackPool.Rent();
            _localizedEffectOldBoundsStack = pools.NullableLtrbRectStackPool.Rent();
        }

        /// <summary>Unions redirected rects into a single local-space extent.</summary>
        private sealed class UnionDirtyRectCollector : IDirtyRectCollector
        {
            public LtrbRect? Union;
            public void AddRect(LtrbRect rect) => Union = LtrbRect.FullUnion(Union, rect);
            // Union rects have no readable position; consumers classify
            // conservatively.
            public DirtyRectWorkingSet GetWorkingSet() => default;
        }

        private static readonly ThreadSafeObjectPool<UnionDirtyRectCollector> s_unionCollectorPool = new();

        /// <summary>
        /// Whether the node's dirty subtree can invalidate just the changed
        /// content plus the effect's reach instead of the node's whole padded
        /// bounds: nothing about the node itself changed, and the effect's
        /// output can only differ near changed input.
        /// </summary>
        private static bool UseLocalizedEffectRegion(ServerCompositionVisual node)
            => node is { _isDirtyForRender: false, _isDirtyForRenderInSubgraph: true, HasEffect: true, Cache: null }
               && node.Effect is { } effect
               && effect.IsInputLocal();

        private void PushLocalizedEffectRegionIfNeeded(ServerCompositionVisual node)
        {
            if (!UseLocalizedEffectRegion(node))
                return;

            // Last frame's finalized subtree bounds, captured before the
            // bounding-box seed overwrites them: already effect-inflated and
            // clipped, they are the output extent the effect could have
            // painted last frame. Unioned with the recomputed bounds when the
            // redirect pops, that is exactly where vacated output may need
            // repainting.
            _localizedEffectOldBoundsStack.Push(node._subTreeBounds);

            _dirtyRegionCollectorStack.Push(_dirtyRegion);
            _dirtyRegion = s_unionCollectorPool.Get();
            _dirtyRegionDisableCountStack.Push(_dirtyRegionDisableCount);
            _dirtyRegionDisableCount = 0;

            _context.PushSetTransform(Matrix.Identity);
            _context.ResetClip(LtrbRect.Infinite);
        }

        private void PopLocalizedEffectRegionIfNeeded(ServerCompositionVisual node)
        {
            if (!UseLocalizedEffectRegion(node))
                return;

            _context.PopClip();
            _context.PopTransform();
            var union = (UnionDirtyRectCollector?)_dirtyRegion;
            _dirtyRegion = _dirtyRegionCollectorStack.Pop();
            _dirtyRegionDisableCount = _dirtyRegionDisableCountStack.Pop();

            var oldBounds = _localizedEffectOldBoundsStack.Pop();
            if (union!.Union is { } changed
                && LtrbRect.FullUnion(oldBounds, node._subTreeBounds) is { } outputExtent)
            {
                // Everything the effect's output can have changed in: the
                // changed input plus the filter's reach, clamped to the output
                // extent the effect could have painted in either frame.
                AddToDirtyRegion(
                    changed.Inflate(node.Effect!.GetEffectOutputPadding()).IntersectOrNull(outputExtent));
            }

            union.Union = null;
            s_unionCollectorPool.ReturnAndSetNull(ref union);
        }

        private void PushCacheIfNeeded(ServerCompositionVisual visual)
        {
            if (visual.Cache != null)
            {
                if (_backdropCaptures != null)
                {
                    // The propagation matrix is only meaningful when the walk
                    // pushed the ancestor transforms; under a disabled region
                    // the disabling ancestor's whole bounds already cover this
                    // cache in the parent host, so propagation can be skipped.
                    _backdropHosts?.Add(new BackdropHostRecord(
                        visual.Cache.DirtyRectCollector,
                        _currentHostCollector,
                        visual,
                        _dfsCounter,
                        _context.Transform,
                        OwnerInnerToParentValid: !AreDirtyRegionsDisabled()));

                    (_hostOwnerStack ??= new Stack<ServerCompositionVisual?>()).Push(_currentHostOwner);
                    (_hostCollectorStack ??= new Stack<IDirtyRectCollector>()).Push(_currentHostCollector);
                    _currentHostOwner = visual;
                    _currentHostCollector = visual.Cache.DirtyRectCollector;
                }

                _dirtyRegionCollectorStack.Push(_dirtyRegion);
                _dirtyRegion = visual.Cache.DirtyRectCollector;
                _dirtyRegionDisableCountStack.Push(_dirtyRegionDisableCount);
                _dirtyRegionDisableCount = 0;

                _context.PushSetTransform(Matrix.Identity);
                _context.ResetClip(LtrbRect.Infinite);
            }
        }

        private void PopCacheIfNeeded(ServerCompositionVisual visual)
        {
            if (visual.Cache != null)
            {
                _context.PopClip();
                _context.PopTransform();
                _dirtyRegion = _dirtyRegionCollectorStack.Pop();
                _dirtyRegionDisableCount = _dirtyRegionDisableCountStack.Pop();

                if (_backdropCaptures != null)
                {
                    _currentHostOwner = _hostOwnerStack!.Pop();
                    _currentHostCollector = _hostCollectorStack!.Pop();
                }

                if (visual.Cache.IsDirty)
                    AddToDirtyRegion(visual._subTreeBounds);
            }
        }
        
        private bool NeedToPushBoundsAffectingProperties(ServerCompositionVisual node)
        {
            return (node._isDirtyForRenderInSubgraph || node._needsToAddExtraDirtyRectToDirtyRegion || node._contentChanged);
        }
        
        public void PreSubgraph(ServerCompositionVisual node, out bool visitChildren)
        {
            _dfsCounter++;

            visitChildren = node._isDirtyForRenderInSubgraph || node._needsBoundingBoxUpdate;

            // If this node has an alpha mask an we caused its inner bounds to change
            // then treat the node as if _isDirtyForRender was set.
            if (node is { _needsBoundingBoxUpdate: true, OpacityMaskBrush: not null })
                node._isDirtyForRender = true;

            // Classify this backdrop's frame before any of this node's own or
            // descendant damage is emitted: everything already in the working
            // set painted at or beneath the sample point, everything later
            // paints above it.
            if (node._registeredAsBackdrop)
                CaptureBackdrop(node);

            // The extra rect (a removed child's old bounds) is only emitted at
            // this node's PostSubgraph, above this node's own sample point but
            // possibly beneath backdrops captured deeper in the subtree.
            if (node._needsToAddExtraDirtyRectToDirtyRegion)
                _extraDirtyAncestorCount++;

            // Keep descending a clean spine toward registered backdrops while
            // the current host has damage, so they get classified at their
            // walk position. A clean cache boundary is never entered: its host
            // has no new damage, so backdrops inside it are unaffected.
            if (!visitChildren && node.Cache == null && node._backdropsInSubTree > 0
                && !_dirtyRegion.GetWorkingSet().IsEmpty)
                visitChildren = true;

            // Special handling for effects: just add the entire node's old subtree bounds as a dirty region
            // WPF does this because they had legacy effects with non-affine transforms, we do this because
            // it's something to be done in the future (maybe)
            // Input-local effects skip this whole-bounds mechanism: the subtree's
            // rects are redirected below and re-added with the effect's reach.
            if ((node._isDirtyForRender || node is { _isDirtyForRenderInSubgraph: true, HasEffect: true })
                && !UseLocalizedEffectRegion(node))
            {
                // If bounds haven't actually changed, there is no point in adding them now since they will be added
                // again in PostSubgraph.
                if (node._needsBoundingBoxUpdate && !AreDirtyRegionsDisabled())
                {
                    // We add this node's bbox to the dirty region. Alternatively we could walk the sub-graph and add the
                    // bbox of each node's content to the dirty region. Note that this is much harder to do because if the
                    // transform changes we don't know anymore the old transform. We would have to use to a two phased dirty
                    // region algorithm.
                    AddToDirtyRegion(node._transformedSubTreeBounds);
                }

                // If we added a node in the parent chain to the bbox we don't need to add anything below this node
                // to the dirty region.
                _dirtyRegionDisableCount++;
            }

            // If a node in the sub-graph of this node is dirty for render and we haven't collected the bbox of one of pNode's
            // ascendants as dirty region, then we need to maintain the transform and clip stack so that we have a world transform
            // when we need to collect the bbox of the descendant node that is dirty for render.  If something has changed
            // in the contents or subgraph, we need to update the cache on this node.
            if (NeedToPushBoundsAffectingProperties(node))
            {
                // Dirty regions will be enabled if we haven't collected an ancestor's bbox or if they were re-enabled
                // by an ancestor's cache.
                if (!AreDirtyRegionsDisabled())
                {
                    PushBoundsAffectingProperties(node);
                }

                PushCacheIfNeeded(node);
                PushLocalizedEffectRegionIfNeeded(node);
            }

            if (node._needsBoundingBoxUpdate)
            {
                // This node's bbox needs to be updated. We start out by setting his bbox to the bbox of its content. All its
                // children will union their bbox into their parent's bbox. PostSubgraph will clip the bbox and transform it
                // to outer space.
                node._subTreeBounds = node._ownContentBounds;
            }
        }


        public void PostSubgraph(ServerCompositionVisual node)
        {
            var parent = node.Parent;
            if (node._needsBoundingBoxUpdate)
            {
                //
                // If pNode's bbox got recomputed it is at this point still in inner
                // space. We need to apply the clip and transform.
                //
                FinalizeSubtreeBounds(node);
            }

            //
            // Update state on the parent node if we have a parent.

            if (parent != null)
            {
                // Update the bounding box on the parent.
                if (parent._needsBoundingBoxUpdate)
                    parent._subTreeBounds = LtrbRect.FullUnion(parent._subTreeBounds, node._transformedSubTreeBounds);
            }
            
            //
            // If there are additional dirty regions, pick them up. (Additional dirty regions are
            // specified before the tranform, i.e. in inner space, hence we have to pick them
            // up before we pop the transform from the transform stack.
            //
            if (node._needsToAddExtraDirtyRectToDirtyRegion)
            {
                AddToDirtyRegion(node._extraDirtyRect);
            }

            // If we pushed transforms here, we need to pop them again.  If we're handling a cache we need
            // to finish handling it here as well.
            if (NeedToPushBoundsAffectingProperties(node))
            {
                PopLocalizedEffectRegionIfNeeded(node);
                PopCacheIfNeeded(node);
                if(!AreDirtyRegionsDisabled())
                    PopBoundsAffectingProperties(node);

            }

            // Special handling for effects: just add the entire node's old subtree bounds as a dirty region
            // WPF does this because they had legacy effects with non-affine transforms, we do this because
            // it's something to be done in the future (maybe)
            // Localized effect nodes already added their reach-inflated rect
            // when the redirect popped, and never incremented the disable count.
            if((node._isDirtyForRender || node is { _isDirtyForRenderInSubgraph: true, Effect: not null })
               && !UseLocalizedEffectRegion(node))
            {
                _dirtyRegionDisableCount--;
                AddToDirtyRegion(node._transformedSubTreeBounds);
            }

            if (node._needsToAddExtraDirtyRectToDirtyRegion)
                _extraDirtyAncestorCount--;

            node._isDirtyForRender = false;
            node._isDirtyForRenderInSubgraph = false;
            node._needsBoundingBoxUpdate = false;
            node._needsToAddExtraDirtyRectToDirtyRegion = false;
            node._contentChanged = false;
        }

        /// <summary>
        /// Records this frame's classification for a registered backdrop at its
        /// walk position. Only the node's own changes - size, transform, clip,
        /// all of which set _isDirtyForRender (see the flags cheatsheet) - can
        /// move the sample region; subtree damage, including the Effect
        /// whole-bounds re-add a child change triggers for a drop shadow,
        /// paints above the sample point and keeps the retained result usable.
        /// </summary>
        private void CaptureBackdrop(ServerCompositionVisual node)
        {
            if (_backdropCaptures == null)
                return;

            // The backdrop belongs to the nearest enclosing bitmap-cache host
            // (or the target): it samples that host's surface, so bounds and
            // classification are resolved in host space.
            var bounds = node.TryGetHostBounds(_rootTransform, _currentHostOwner, out var cacheable,
                out var localToHost);

            var workingSet = _dirtyRegion.GetWorkingSet();
            bool belowDirt;
            var dirtStart = _backdropDirtRects?.Count ?? 0;
            var dirtCount = 0;
            var dirtOverflow = false;
            if (AreDirtyRegionsDisabled() || _extraDirtyAncestorCount > 0 || !workingSet.IsUsable)
            {
                // A covering dirty-for-render ancestor (its rect only lands in
                // the working set at its own PostSubgraph), a pending extra rect
                // above (a removed child that may have painted beneath), or an
                // unreadable collector (a localized-effect union, a cache that
                // never drew): the provenance is unknowable here, so the input
                // conservatively counts as changed beneath, everywhere.
                belowDirt = true;
                dirtOverflow = true;
            }
            else if (bounds is not { } hostBounds)
            {
                belowDirt = false;
            }
            else
            {
                var area = hostBounds.Inflate(node.BackdropEffect.GetEffectOutputPadding());
                belowDirt = false;
                var buffer = _workingSetBuffer ??= new List<LtrbRect>();
                buffer.Clear();
                workingSet.CollectTo(buffer);
                foreach (var rect in buffer)
                {
                    if (!rect.Intersects(area))
                        continue;

                    belowDirt = true;
                    // Keep the individual dirt rects so a valid cache can be
                    // refreshed partially; past the cap the frame degrades to
                    // a full refresh.
                    if (_backdropDirtRects != null && dirtCount < ServerCompositionTarget.MaxPartialRefreshRects)
                    {
                        _backdropDirtRects.Add(rect.IntersectOrEmpty(area));
                        dirtCount++;
                    }
                    else
                    {
                        dirtOverflow = true;
                    }
                }
            }

            // A grant left unconsumed (the backend never pushed the layer, or
            // ignored it) must escalate to a full refresh on the next touch:
            // its certified region is gone.
            var stalePartialGrant = node.BackdropCache is { IsValid: true, RefreshRequested: true };

            _backdropCaptures.Add(new BackdropCapture(
                node, _currentHostCollector, _dfsCounter, node._isDirtyForRender, belowDirt, bounds, cacheable,
                Captured: true, dirtStart, dirtCount, dirtOverflow, localToHost, stalePartialGrant));
        }

        private void FinalizeSubtreeBounds(ServerCompositionVisual node)
        {
            // WPF simply removes drawing commands from every visual in invisible subtree (on UI thread).
            // We set the bounds to null when computing subtree bounds for invisible nodes.
            if (!node.Visible)
                node._subTreeBounds = null;

            if (node._subTreeBounds != null)
            {
                if (node.Effect != null)
                    node._subTreeBounds = node._subTreeBounds.Value.Inflate(node.Effect.GetEffectOutputPadding());

                if (node._ownClipRect.HasValue)
                    node._subTreeBounds = node._subTreeBounds.Value.IntersectOrNull(node._ownClipRect.Value);
            }

            if (node._subTreeBounds == null)
                node._transformedSubTreeBounds = null;
            else if (node._ownTransform.HasValue)
                node._transformedSubTreeBounds = node._subTreeBounds?.TransformToAABB(node._ownTransform.Value);
            else
                node._transformedSubTreeBounds = node._subTreeBounds;

            node.EnqueueForReadbackUpdate();
        }

        private void AddToDirtyRegion(LtrbRect? bounds)
        {
            if(_dirtyRegionDisableCount != 0 || !bounds.HasValue)
                return;

            var transformed = bounds.Value.TransformToAABB(_context.Transform).IntersectOrEmpty(_context.Clip);
            if(transformed.IsZeroSize)
                return;

            _dirtyRegion.AddRect(transformed);
        }
        
        private void PushBoundsAffectingProperties(ServerCompositionVisual node)
        {
            if (node._ownTransform.HasValue)
                _context.PushTransform(node._ownTransform.Value);
            if (node._ownClipRect.HasValue) 
                _context.PushClip(node._ownClipRect.Value.TransformToAABB(_context.Transform));
        }
        
        private void PopBoundsAffectingProperties(ServerCompositionVisual node)
        {
            if (node._ownTransform.HasValue)
                _context.PopTransform();
            if (node._ownClipRect.HasValue)
                _context.PopClip();
        }

        public void Dispose()
        {
            _context.Pools.IntStackPool.Return(ref _dirtyRegionDisableCountStack);
            _context.Pools.DirtyRectCollectorStackPool.Return(ref _dirtyRegionCollectorStack);
            _context.Pools.NullableLtrbRectStackPool.Return(ref _localizedEffectOldBoundsStack);
            _context.Dispose();
        }
    }
    
    public void UpdateRoot(IDirtyRectCollector tracker, Matrix transform, LtrbRect clip,
        List<BackdropCapture>? backdropCaptures = null, List<BackdropHostRecord>? backdropHosts = null,
        List<LtrbRect>? backdropDirtRects = null)
    {
        var context = new UpdateContext(Compositor.Pools, tracker, transform, clip, backdropCaptures, backdropHosts,
            backdropDirtRects);
        ServerTreeWalker<UpdateContext>.Walk(ref context, this);
        context.Dispose();
    }

}