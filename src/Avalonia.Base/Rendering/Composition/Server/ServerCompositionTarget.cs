using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Collections.Pooled;
using Avalonia.Diagnostics;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Server
{
    /// <summary>
    /// Server-side counterpart of the <see cref="CompositionTarget"/>
    /// That's the place where we update visual transforms, track dirty rects and actually do rendering
    /// </summary>
    internal partial class ServerCompositionTarget : IDisposable
    {
        private readonly ServerCompositor _compositor;
        private readonly Func<IEnumerable<IPlatformRenderSurface>> _surfaces;
        private CompositionTargetOverlays _overlays;
        private static long s_nextId = 1;
        private IRenderTarget? _renderTarget;
        private PixelSize _layerSize;
        private IDrawingContextLayerImpl? _layer;
        private Matrix _outputTransform = Matrix.Identity;
        private bool _updateRequested;
        private bool _redrawRequested;
        private bool _fullRedrawRequested;
        private bool _disposed;
        private readonly HashSet<ServerCompositionVisual> _attachedVisuals = new();
        private readonly HashSet<ServerCompositionVisual> _backdropVisuals = new();
        private readonly List<BackdropCapture> _backdropCaptures = new();
        private readonly List<BackdropHostRecord> _backdropHosts = new();
        private readonly List<(IDirtyRectCollector Collector, LtrbRect Rect, int DfsIndex)> _backdropExpansions = new();
        private readonly List<(IDirtyRectCollector Collector, ServerCompositionVisual? Owner, int Depth)> _backdropHostOrder = new();
        private readonly List<LtrbRect> _backdropWorkingSet = new();
        private readonly List<LtrbRect> _backdropDirtRects = new();
        private readonly List<LtrbRect> _partialDirtScratch = new();
        private readonly List<BackdropJournalEntry> _backdropJournal = new();
        private long _backdropFrame;
        private readonly List<BackdropLayerCache> _fullRedrawGrants = new();

        // A partial refresh past this many sub-rects degrades to a full one:
        // per-rect staging stops paying off and the bookkeeping stays tiny.
        internal const int MaxPartialRefreshRects = 4;
        public IDirtyRectTracker DirtyRects { get; }

        public long Id { get; }
        public ulong Revision { get; private set; }
        public ICompositionTargetDebugEvents? DebugEvents { get; set; }
        public int RenderedVisuals { get; set; }
        public int VisitedVisuals { get; set; }

        internal PixelSize PixelSize => Avalonia.PixelSize.FromSizeCeiling(Size, Scaling);
        
        /// <summary>
        /// Returns true if the target is enabled and has pending work but its render target was not ready.
        /// </summary>
        internal bool IsWaitingForReadyRenderTarget { get; private set; }
        
        /// <summary>
        /// Returns true if the target's render target is waiting for a render loop wakeup
        /// (i.e. the platform will call Wakeup() when ready, no need to keep polling).
        /// </summary>
        internal bool IsWaitingForRenderLoopWakeup { get; private set; }

        public ServerCompositionTarget(ServerCompositor compositor, Func<IEnumerable<IPlatformRenderSurface>> surfaces)
            : base(compositor)
        {
            _compositor = compositor;
            _surfaces = surfaces;
            _overlays = new CompositionTargetOverlays(this);
            var platformRender = AvaloniaLocator.Current.GetService<IPlatformRenderInterface>();

            if (platformRender?.SupportsRegions == true && compositor.Options.UseRegionDirtyRectClipping == true)
            {
                var maxRects = compositor.Options.MaxDirtyRects ?? 8;
                DirtyRects = maxRects <= 0
                    ? new RegionDirtyRectTracker(platformRender)
                    : new MultiDirtyRectTracker(platformRender, maxRects,
                        // WPF uses 50K, but that merges stuff rather aggressively 
                        compositor.Options.DirtyRectMergeEagerness ?? 1000); 
            }

            DirtyRects ??= new SingleDirtyRectTracker();
            
            Id = Interlocked.Increment(ref s_nextId);
        }
        
        partial void OnIsEnabledChanged()
        {
            if (IsEnabled)
            {
                _compositor.AddCompositionTarget(this);
                foreach (var v in _attachedVisuals)
                    v.Activate();
            }
            else
            {
                _compositor.RemoveCompositionTarget(this);
                foreach (var v in _attachedVisuals)
                    v.Deactivate();
            }
        }

        partial void OnDebugOverlaysChanged()
        {
            _fullRedrawRequested = true;
            _overlays.OnChanged(DebugOverlays);
        }

        partial void OnLastLayoutPassTimingChanged() => _overlays.OnLastLayoutPassTimingChanged(LastLayoutPassTiming);

        partial void DeserializeChangesExtra(BatchStreamReader c)
        {
            _redrawRequested = true;
            _fullRedrawRequested = true;
        }
        
        
        public void Update(TimeSpan diagnosticsCompositorGlobalUpdateElapsedTime = default)
        {
            if (_disposed)
            {
                Compositor.RemoveCompositionTarget(this);
                return;
            }

            if (Root == null)
                return;
            
            _overlays.RecordGlobalCompositorUpdateTime(diagnosticsCompositorGlobalUpdateElapsedTime);
            _overlays.MarkUpdateCallStart();
            using (Diagnostic.BeginCompositorUpdatePass())
            {
                var transform = Matrix.CreateScale(Scaling, Scaling);

                var collector = DebugEvents != null
                    ? new DebugEventsDirtyRectCollectorProxy(DirtyRects, DebugEvents)
                    : (IDirtyRectCollector)DirtyRects;

                // Backdrops need to know where this frame's damage sits
                // relative to their sample point, which only the update walk
                // knows: it classifies each backdrop at its own walk position,
                // against the bitmap-cache host (or target) whose surface the
                // backdrop samples.
                List<BackdropCapture>? backdropCaptures = null;
                List<BackdropHostRecord>? backdropHosts = null;
                List<LtrbRect>? backdropDirtRects = null;
                List<BackdropJournalEntry>? backdropJournal = null;
                WithdrawFullRedrawGrants();
                if (_backdropVisuals.Count > 0)
                {
                    _backdropCaptures.Clear();
                    _backdropHosts.Clear();
                    _backdropDirtRects.Clear();
                    _backdropJournal.Clear();
                    backdropCaptures = _backdropCaptures;
                    backdropHosts = _backdropHosts;
                    backdropDirtRects = _backdropDirtRects;
                    backdropJournal = _backdropJournal;
                }

                Root.UpdateRoot(collector, transform, new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height),
                    backdropCaptures, backdropHosts, backdropDirtRects, backdropJournal);

                ExpandDirtyRegionForBackdrops(collector, transform);

                _updateRequested = false;

                _overlays.MarkUpdateCallEnd();
            }
        }

        public void Render()
        {
            IsWaitingForReadyRenderTarget = false;
            IsWaitingForRenderLoopWakeup = false;
            
            if (_disposed)
                return;

            if (Root == null) 
                return;

            if (_renderTarget?.PlatformRenderTargetState.IsCorrupted == true)
            {
                _layer?.Dispose();
                _layer = null;
                _renderTarget.Dispose();
                _renderTarget = null;
                _redrawRequested = true;
            }

            try
            {
                if (_renderTarget == null)
                {
                    if (!_compositor.IsReadyToCreateRenderTarget(_surfaces()))
                    {
                        IsWaitingForReadyRenderTarget = IsEnabled;
                        return;
                    }

                    _renderTarget = _compositor.CreateRenderTarget(_surfaces());
                }
            }
            catch (RenderTargetNotReadyException)
            {
                IsWaitingForReadyRenderTarget = IsEnabled;
                return;
            }
            catch (RenderTargetCorruptedException)
            {
                return;
            }

            if (DirtyRects.IsEmpty && !_redrawRequested && !_updateRequested)
                return;

            _redrawRequested |= !DirtyRects.IsEmpty;

            if (!_redrawRequested)
                return;
            
            if (!_renderTarget.PlatformRenderTargetState.IsReady)
            {
                IsWaitingForReadyRenderTarget = IsEnabled;
                IsWaitingForRenderLoopWakeup = IsEnabled && _renderTarget.PlatformRenderTargetState.WillWakeUpRenderLoopWhenReady;
                return;
            }

            var targetProperties = _renderTarget.Properties;
            // Exact quarter turns keep pixel edges aligned.
            var outputTransform = targetProperties.RequiredRotationForOrientation switch
            {
                SurfaceOrientation.Rotation90 => new Matrix(0, 1, -1, 0, PixelSize.Height, 0),
                SurfaceOrientation.Rotation180 => new Matrix(-1, 0, 0, -1, PixelSize.Width, PixelSize.Height),
                SurfaceOrientation.Rotation270 => new Matrix(0, -1, 1, 0, 0, PixelSize.Width),
                _ => Matrix.Identity
            };
            if (_outputTransform != outputTransform)
            {
                _outputTransform = outputTransform;
                _fullRedrawRequested = true;
            }
            var needLayer = !outputTransform.IsIdentity || _overlays.RequireLayer
                            || !(targetProperties.IsSuitableForDirectRendering
                                 && targetProperties.RetainsPreviousFrameContents);

            IDrawingContextImpl renderTargetContext;
            RenderTargetDrawingContextProperties properties;
            try
            {
                renderTargetContext =
                    _renderTarget.CreateDrawingContext(
                        new(PixelSize, Scaling, Size, TransparencyLevel, PlatformSpecificSceneInfo),
                        out properties);
            }
            catch (RenderTargetNotReadyException)
            {
                IsWaitingForReadyRenderTarget = IsEnabled;
                return;
            }
            catch (RenderTargetCorruptedException)
            {
                return;
            }
            
            using (renderTargetContext)
            using (var renderTiming = Diagnostic.BeginCompositorRenderPass())
            {
                var fullRedraw = false;
                
                if(needLayer && (PixelSize != _layerSize || _layer == null || _layer.IsCorrupted))
                {
                    _layer?.Dispose();
                    _layer = null;
                    _layer = renderTargetContext.CreateLayer(PixelSize);
                    _layerSize = PixelSize;
                    fullRedraw = true;
                }
                else if (!needLayer)
                {
                    _layer?.Dispose();
                    _layer = null;
                }

                if (_fullRedrawRequested || (!needLayer && !properties.PreviousFrameIsRetained))
                {
                    _fullRedrawRequested = false;
                    fullRedraw = true;
                }

                var renderBounds = new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height);
                if (fullRedraw)
                {
                    DirtyRects.Initialize(renderBounds);
                    DirtyRects.AddRect(renderBounds);
                    GrantFullRedrawCaptures();
                }

                if (!DirtyRects.IsEmpty)
                {
                    DirtyRects.FinalizeFrame(renderBounds);
                    if (_layer != null)
                    {
                        using (var context = _layer.CreateDrawingContext())
                            RenderRootToContextWithClip(context, Root);

                        renderTargetContext.Transform = Matrix.Identity;
                        var rect = new PixelRect(PixelSize).ToRect(1);
                        renderTargetContext.Clear(Colors.Transparent);
                        renderTargetContext.Transform = outputTransform;
                        if (_layer.CanBlit && outputTransform.IsIdentity)
                            _layer.Blit(renderTargetContext);
                        else
                            renderTargetContext.DrawBitmap(_layer, 1, rect, rect);
                        _overlays.Draw(renderTargetContext, true, outputTransform);
                    }
                    else
                    {
                        RenderRootToContextWithClip(renderTargetContext, Root);
                        _overlays.Draw(renderTargetContext, false);
                    }
                }

                RenderedVisuals = 0;
                VisitedVisuals = 0;

                _redrawRequested = false;
                DirtyRects.Initialize(renderBounds);
            }
        }

        void RenderRootToContextWithClip(IDrawingContextImpl context, ServerCompositionVisual root)
        {
            var useLayerClip = Compositor.Options.UseSaveLayerRootClip ?? false;
            
            using (DirtyRects.BeginDraw(context))
            {
                context.Clear(Colors.Transparent);
                if (useLayerClip)
                    context.PushLayer(DirtyRects.CombinedRect.ToRect());

                context.Transform = Matrix.CreateScale(Scaling, Scaling);
                (VisitedVisuals, RenderedVisuals) = root.Render(context, new LtrbRect(0,0, PixelSize.Width, PixelSize.Height), DirtyRects);
                if (DebugEvents != null)
                {
                    DebugEvents.RenderedVisuals = RenderedVisuals;
                    DebugEvents.VisitedVisuals = VisitedVisuals;
                }

                if (useLayerClip)
                    context.PopLayer();
            }
        }
        
        public void RequestUpdate() => _updateRequested = true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ResetRenderTarget();
            _compositor.RemoveCompositionTarget(this);
        }

        public void ResetRenderTarget()
        {
            if (_layer == null && _renderTarget == null)
                return;
            try
            {
                using (_compositor.RenderInterface.EnsureCurrent())
                {
                    if (_layer != null)
                    {
                        _layer.Dispose();
                        _layer = null;
                    }
                    _renderTarget?.Dispose();
                    _renderTarget = null;
                }
            }
            catch (Exception ex)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this, "Unable to make the render interface current: {Error}", ex);
                // Set to null for now
                // TODO: Check per-platform to make sure that it's safe to dispose anyay
                _layer = null;
                _renderTarget = null;
                
            }

        }

        public void AddVisual(ServerCompositionVisual visual)
        {
            if (_attachedVisuals.Add(visual) && IsEnabled)
                visual.Activate();
        }

        public void RemoveVisual(ServerCompositionVisual visual)
        {
            if (_attachedVisuals.Remove(visual) && IsEnabled)
                visual.Deactivate();
        }

        public void AddBackdropVisual(ServerCompositionVisual visual) => _backdropVisuals.Add(visual);

        public void RemoveBackdropVisual(ServerCompositionVisual visual) => _backdropVisuals.Remove(visual);

        /// <summary>
        /// Collects this frame's dirt beneath a backdrop into the partial dirt
        /// scratch list - the rects captured at the walk position plus expansion
        /// output delivered from earlier-painted backdrops in the same host - and
        /// reports whether it is small enough for a partial refresh. Anything
        /// unclassifiable (the glass itself moved or changed, too many rects, or
        /// changed output covering most of the box) counts as wide.
        /// </summary>
        private bool CollectSmallBehindDirt(in BackdropCapture capture, IEffect effect, LtrbRect box,
            LtrbRect inputArea, IDirtyRectCollector hostCollector)
        {
            _partialDirtScratch.Clear();
            if (capture.SelfDirty || capture.DirtOverflow)
                return false;

            for (var i = 0; i < capture.DirtCount; i++)
                _partialDirtScratch.Add(_backdropDirtRects[capture.DirtStart + i]);
            foreach (var expansion in _backdropExpansions)
            {
                if (ReferenceEquals(expansion.Collector, hostCollector)
                    && expansion.DfsIndex < capture.DfsIndex
                    && expansion.Rect.IntersectOrNull(inputArea) is { } hit)
                {
                    if (_partialDirtScratch.Count >= MaxPartialRefreshRects)
                        return false;
                    _partialDirtScratch.Add(hit);
                }
            }

            if (_partialDirtScratch.Count == 0)
                return false;

            // Once the changed output approaches the whole box a full refresh
            // is both cheaper and simpler, whatever is retained.
            var padding = effect.GetEffectOutputPadding();
            LtrbRect? union = null;
            foreach (var dirt in _partialDirtScratch)
            {
                var output = dirt.Inflate(padding).IntersectOrEmpty(box);
                if (!output.IsZeroSize)
                    union = LtrbRect.FullUnion(union, output);
            }

            return union is { } unionRect
                   && unionRect.Width * unionRect.Height <= 0.5 * box.Width * box.Height;
        }

        /// <summary>
        /// Grants a partial refresh to a valid, cacheable backdrop whose dirt
        /// <see cref="CollectSmallBehindDirt"/> left in the scratch list. Behind-
        /// dirt D changes the filter's output within O = (D + reach) clamped to
        /// the box, so O is always repainted. A retained output re-filters O,
        /// which reads input O + reach: that is repainted too and O becomes the
        /// granted <see cref="BackdropLayerCache.RefreshRects"/>. A retained
        /// input already holds everything around D, so D itself becomes the
        /// granted <see cref="BackdropLayerCache.IngestRects"/> and nothing past O
        /// is repainted. The rects go to the visual's local space; a chain the
        /// local space cannot be recovered from exactly, or a stale unconsumed
        /// grant, refuses, and the caller falls back to full invalidation.
        /// </summary>
        /// <remarks>
        /// Later fixpoint passes revisit the glass with the dirt delivered so
        /// far, so a revisit within the frame rebuilds the grant instead of
        /// adding to it.
        /// </remarks>
        private bool TryGrantPartialBackdropRefresh(in BackdropCapture capture, BackdropLayerCache cache,
            IEffect effect, LtrbRect box, LtrbRect inputArea, IDirtyRectCollector hostCollector,
            ref bool added, ref bool receivedDamage)
        {
            if (capture.StalePartialGrant
                || capture.LocalToHost is not { } localToHost
                || !localToHost.TryInvert(out var hostToLocal))
                return false;

            if (cache.PartialGrantFrame == _backdropFrame)
            {
                cache.RefreshRects.Clear();
                cache.IngestRects.Clear();
            }
            else if (cache.RefreshRects.Count > 0 || cache.IngestRects.Count > 0)
            {
                return false;
            }

            var input = cache.Payload == BackdropCachePayload.Input;
            var padding = effect.GetEffectOutputPadding();
            foreach (var dirt in _partialDirtScratch)
            {
                var output = dirt.Inflate(padding).IntersectOrEmpty(box);
                if (input)
                {
                    var ingest = dirt.IntersectOrEmpty(inputArea);
                    if (!ingest.IsZeroSize)
                        cache.IngestRects.Add(ingest.TransformToAABB(hostToLocal).ToRect());
                }
                else if (!output.IsZeroSize)
                {
                    cache.RefreshRects.Add(output.TransformToAABB(hostToLocal).ToRect());
                }

                var repaint = input ? output : output.Inflate(padding).IntersectOrEmpty(inputArea);
                if (repaint.IsZeroSize)
                    continue;
                var covered = false;
                foreach (var rect in _backdropWorkingSet)
                {
                    if (rect.Contains(repaint))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    hostCollector.AddRect(repaint);
                    _backdropExpansions.Add((hostCollector, repaint,
                        capture.DfsIndex == int.MaxValue ? int.MinValue : capture.DfsIndex));
                    added = true;
                    receivedDamage = true;
                }
            }

            cache.PartialGrantFrame = _backdropFrame;
            cache.RefreshRequested = true;
            return true;
        }

        private void SweepUncapturedBackdrop(ServerCompositionVisual visual, IDirtyRectCollector rootCollector,
            Matrix transform)
        {
            var hostOwner = NearestCacheHostOwner(visual);
            var hostCollector = hostOwner?.Cache?.DirtyRectCollector ?? rootCollector;
            var bounds = visual.TryGetHostBounds(transform, hostOwner, out var cacheable, out var localToHost);

            var belowDirt = false;
            var dirtStart = _backdropDirtRects.Count;
            var dirtCount = 0;
            var dirtOverflow = false;
            if (bounds is { } hostBounds && visual.BackdropEffect is { } effect)
            {
                var area = hostBounds.Inflate(effect.GetEffectOutputPadding());
                foreach (var entry in _backdropJournal)
                {
                    if (!ReferenceEquals(entry.Host, hostCollector) || !entry.Rect.Intersects(area))
                        continue;

                    var before = PaintsBefore(entry.Emitter, visual);
                    if (before == false)
                        continue; // provably painted above the sample point

                    belowDirt = true;
                    if (before == null)
                    {
                        // An ancestor's covering rect (or an unresolvable
                        // relation) mixes content from both sides: full only.
                        dirtOverflow = true;
                    }
                    else if (dirtCount < MaxPartialRefreshRects)
                    {
                        _backdropDirtRects.Add(entry.Rect.IntersectOrEmpty(area));
                        dirtCount++;
                    }
                    else
                    {
                        dirtOverflow = true;
                    }
                }
            }

            var stalePartialGrant = visual.BackdropCache is { IsValid: true, RefreshRequested: true };

            // DfsIndex int.MaxValue: the glass has no walk position, so every
            // expansion counts as painted beneath it (conservative receive);
            // its own expansions are tagged int.MinValue at the add sites so
            // they deliver to everyone (conservative send).
            _backdropCaptures.Add(new BackdropCapture(
                visual, hostCollector, int.MaxValue, SelfDirty: false, belowDirt, bounds, cacheable,
                dirtStart, dirtCount, dirtOverflow, localToHost, stalePartialGrant));
        }

        /// <summary>
        /// Whether <paramref name="emitter"/> paints before (beneath)
        /// <paramref name="backdrop"/> in document order; false when provably
        /// after (above); null when the relation cannot be established - an
        /// emitter on the backdrop's ancestor chain (its rect mixes content
        /// from both sides of the sample point) or a broken parent chain.
        /// </summary>
        private static bool? PaintsBefore(ServerCompositionVisual emitter, ServerCompositionVisual backdrop)
        {
            if (ReferenceEquals(emitter, backdrop))
                return null;

            var emitterDepth = DepthOf(emitter);
            var backdropDepth = DepthOf(backdrop);

            var e = emitter;
            var b = backdrop;
            ServerCompositionVisual? emitterBranch = null;
            ServerCompositionVisual? backdropBranch = null;
            while (emitterDepth > backdropDepth)
            {
                emitterBranch = e;
                e = e.Parent!;
                emitterDepth--;
            }

            while (backdropDepth > emitterDepth)
            {
                backdropBranch = b;
                b = b.Parent!;
                backdropDepth--;
            }

            while (!ReferenceEquals(e, b))
            {
                if (e.Parent == null || b.Parent == null)
                    return null;
                emitterBranch = e;
                backdropBranch = b;
                e = e.Parent;
                b = b.Parent;
            }

            if (emitterBranch == null)
                return null; // the emitter is an ancestor of the backdrop
            // An emitter inside the backdrop's subtree cannot happen for a
            // swept backdrop (a dirty descendant forces the walk through the
            // glass, which captures it); classify as beneath defensively.
            if (backdropBranch == null)
                return true;

            var children = e.Children?.List;
            if (children == null)
                return null;
            var emitterIndex = children.IndexOf(emitterBranch);
            var backdropIndex = children.IndexOf(backdropBranch);
            if (emitterIndex < 0 || backdropIndex < 0)
                return null;

            return emitterIndex < backdropIndex;
        }

        private static int DepthOf(ServerCompositionVisual visual)
        {
            var depth = 0;
            for (var parent = visual.Parent; parent != null; parent = parent.Parent)
                depth++;
            return depth;
        }

        private static ServerCompositionVisual? NearestCacheHostOwner(ServerCompositionVisual visual)
        {
            for (var parent = visual.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.Cache != null)
                    return parent;
            }

            return null;
        }

        // The cache owner's subtree bounds mapped into its parent host's space,
        // for propagating host damage upward as the boundary's coarse rect.
        private static LtrbRect? OwnerCoarseBoundsInParentHost(ServerCompositionVisual owner,
            ServerCompositionVisual? parentHostOwner, Matrix rootTransform)
        {
            if (owner.SubTreeBounds is not { } bounds)
                return null;

            var rect = owner.OwnTransform is { } ownTransform ? bounds.TransformToAABB(ownTransform) : bounds;
            for (var parent = owner.Parent; parent != null; parent = parent.Parent)
            {
                if (ReferenceEquals(parent, parentHostOwner))
                    return rect;
                if (parent.OwnTransform is { } parentTransform)
                    rect = rect.TransformToAABB(parentTransform);
            }

            return parentHostOwner == null ? rect.TransformToAABB(rootTransform) : null;
        }

        private void AddBackdropHostOrdered(IDirtyRectCollector hostCollector, ServerCompositionVisual? owner)
        {
            foreach (var host in _backdropHostOrder)
            {
                if (ReferenceEquals(host.Collector, hostCollector))
                    return;
            }

            var depth = 0;
            for (var parent = owner; parent != null; parent = parent.Parent)
                depth++;
            _backdropHostOrder.Add((hostCollector, owner, depth));
        }

        private void ExpandDirtyRegionForBackdrops(IDirtyRectCollector collector, Matrix transform)
        {
            if (_backdropVisuals.Count == 0)
                return;

            // Registered backdrops the walk never reached (a clean spine or a
            // clean cache boundary): the walk stamped no position for them, so
            // classify from the positional journal instead - each emitted rect
            // carries its emitting visual, and document order against the
            // glass decides above/below exactly as the in-walk capture would
            // have. Anything the comparison cannot prove counts as beneath.
            foreach (var visual in _backdropVisuals)
            {
                var captured = false;
                for (var i = 0; i < _backdropCaptures.Count && !captured; i++)
                    captured = ReferenceEquals(_backdropCaptures[i].Visual, visual);
                if (!captured)
                    SweepUncapturedBackdrop(visual, collector, transform);
            }

            // Process hosts deepest-first so a child host's propagated damage is
            // already in its parent's tracker and expansion list before the
            // parent's own pass runs; the target's own collector goes last.
            _backdropHostOrder.Clear();
            foreach (var record in _backdropHosts)
                AddBackdropHostOrdered(record.HostCollector, record.Owner);
            foreach (var capture in _backdropCaptures)
            {
                if (!ReferenceEquals(capture.Collector, collector))
                    AddBackdropHostOrdered(capture.Collector, NearestCacheHostOwner(capture.Visual));
            }

            _backdropHostOrder.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
            _backdropHostOrder.Add((collector, null, 0));

            _backdropExpansions.Clear();

            // A payload switch decided last frame lands before any grant is
            // computed, so the invalidation it causes makes this frame's touch
            // a full refresh of the new payload.
            _backdropFrame++;
            foreach (var visual in _backdropVisuals)
                visual.BackdropCache?.ApplyPendingPayload();

            foreach (var host in _backdropHostOrder)
                RunBackdropHostPass(host.Collector, host.Owner, collector, transform);

            foreach (var visual in _backdropVisuals)
                visual.BackdropCache?.EndFrame(_backdropFrame);
        }

        /// <summary>
        /// Decides, per backdrop this frame touched, whether its retained result
        /// is still usable and widens the dirty region when it is not. A backdrop
        /// composites what is already on the surface, so when its filter has to
        /// run, everything it reads - its whole area plus the filter's reach -
        /// must be freshly painted first; anything left unpainted is last frame's
        /// output, which already contains the backdrop, and filtering that again
        /// smears it outward. Rects tagged as coming from the backdrop's own
        /// subtree paint above the sample point and cannot change what the filter
        /// reads, so a backdrop with a valid cache skips both the re-filter and
        /// the repaint underneath - the point of the cache.
        /// </summary>
        /// <remarks>
        /// This works off the rects collected during the update rather than
        /// <see cref="IDirtyRectTracker.Intersects"/>, because the tracker only
        /// rebuilds what that queries in FinalizeFrame, which has not run yet and
        /// would answer for the previous frame.
        /// </remarks>
        private void RunBackdropHostPass(IDirtyRectCollector hostCollector, ServerCompositionVisual? hostOwner,
            IDirtyRectCollector rootCollector, Matrix transform)
        {
            var isRootHost = ReferenceEquals(hostCollector, rootCollector);
            var surface = new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height);

            // A save-layer around the whole frame means a root-host backdrop
            // samples that layer instead of the base surface the snapshot would
            // read; cache hosts render into their own surface and are immune.
            var rootUsesLayer = isRootHost && (Compositor.Options.UseSaveLayerRootClip ?? false);

            var receivedDamage = false;

            // Expanding one backdrop can reach another, so repeat until a pass
            // adds nothing. Each pass covers at least one more backdrop, so the
            // count bounds the loop.
            for (var pass = 0; pass < _backdropCaptures.Count + 1; pass++)
            {
                var added = false;

                // The raw working set only grows via the expansions this loop
                // adds, so one refresh per pass is enough for the touch and
                // containment tests. An unusable view (a cache that never drew)
                // has nothing retained to keep fresh.
                var workingSet = hostCollector.GetWorkingSet();
                if (!workingSet.IsUsable)
                    break;
                _backdropWorkingSet.Clear();
                workingSet.CollectTo(_backdropWorkingSet);
                if (_backdropWorkingSet.Count == 0)
                    break;

                foreach (var capture in _backdropCaptures)
                {
                    if (!ReferenceEquals(capture.Collector, hostCollector))
                        continue;

                    var visual = capture.Visual;
                    if (visual.BackdropEffect is not { } effect)
                        continue;
                    if (capture.HostBounds is not { } bounds)
                        continue;
                    var cacheable = capture.Cacheable && !rootUsesLayer;

                    var area = bounds.Inflate(effect.GetEffectOutputPadding());
                    if (isRootHost)
                        area = area.IntersectOrEmpty(surface);
                    if (area.IsZeroSize)
                        continue;

                    var touched = false;
                    var contained = false;
                    foreach (var rect in _backdropWorkingSet)
                    {
                        if (!rect.Intersects(area))
                            continue;

                        touched = true;
                        if (rect.Contains(area))
                            contained = true;
                    }

                    if (!touched)
                        continue;

                    // The walk classified damage by position: only content
                    // painted at or beneath the sample point (or the visual's
                    // own relocation) changes what the filter reads. Expansion
                    // output of an earlier-painted backdrop also repaints
                    // beneath this one.
                    var invalidated = capture.SelfDirty || capture.BelowDirt;
                    if (!invalidated)
                    {
                        foreach (var expansion in _backdropExpansions)
                        {
                            if (ReferenceEquals(expansion.Collector, hostCollector)
                                && expansion.DfsIndex < capture.DfsIndex
                                && expansion.Rect.Intersects(area))
                            {
                                invalidated = true;
                                break;
                            }
                        }
                    }

                    var cache = visual.BackdropCache;
                    if (cache is { Payload: BackdropCachePayload.None })
                        cacheable = false;

                    var smallDirt = invalidated
                        && CollectSmallBehindDirt(capture, effect, bounds, area, hostCollector);
                    cache?.Observe(_backdropFrame, !invalidated ? BackdropTouch.Above
                        : smallDirt ? BackdropTouch.SmallBehind : BackdropTouch.WideBehind);

                    // A still-valid cache touched by small, classifiable dirt
                    // can be refreshed partially; which part depends on what it
                    // retains.
                    if (smallDirt && cacheable && cache is { IsValid: true }
                        && TryGrantPartialBackdropRefresh(capture, cache, effect, bounds, area, hostCollector,
                            ref added, ref receivedDamage))
                        continue;

                    if (invalidated && cache != null)
                    {
                        cache.IsValid = false;
                        cache.RefreshRects.Clear();
                        cache.IngestRects.Clear();
                    }

                    if (cacheable && cache is { IsValid: true })
                        continue; // the retained result gets drawn; nothing beneath needs repainting

                    if (cacheable && cache != null)
                    {
                        // An invalid cache makes the grant a full refresh.
                        cache.RefreshRequested = true;
                        cache.RefreshRects.Clear();
                        cache.IngestRects.Clear();
                    }

                    if (contained)
                        continue; // some rect already repaints the full input; nothing to widen

                    hostCollector.AddRect(area);
                    // A swept glass (DfsIndex MaxValue) has no provable paint
                    // position, so its expansion delivers to everyone.
                    _backdropExpansions.Add((hostCollector, area,
                        capture.DfsIndex == int.MaxValue ? int.MinValue : capture.DfsIndex));
                    added = true;
                    receivedDamage = true;
                }

                if (!added)
                    break;
            }

            if (!receivedDamage || hostOwner == null)
                return;

            // The host redraws content its parent composes as the boundary
            // visual's coarse bounds: mirror that into the parent host so the
            // refreshed texture reaches the screen, and deliver it to backdrops
            // painted later than the boundary there.
            BackdropHostRecord? record = null;
            foreach (var candidate in _backdropHosts)
            {
                if (ReferenceEquals(candidate.HostCollector, hostCollector))
                {
                    record = candidate;
                    break;
                }
            }

            if (record is { OwnerInnerToParentValid: false })
                return; // a disabled ancestor's whole bounds already cover the boundary

            LtrbRect? coarse;
            IDirtyRectCollector parentCollector;
            int boundaryDfsIndex;
            if (record is { } r)
            {
                coarse = hostOwner.SubTreeBounds?.TransformToAABB(r.OwnerInnerToParent);
                parentCollector = r.ParentCollector;
                boundaryDfsIndex = r.OwnerDfsIndex;
            }
            else
            {
                var parentHostOwner = NearestCacheHostOwner(hostOwner);
                coarse = OwnerCoarseBoundsInParentHost(hostOwner, parentHostOwner, transform);
                parentCollector = parentHostOwner?.Cache?.DirtyRectCollector ?? rootCollector;
                // The boundary was not visited this walk, so its paint position
                // is unknown; deliver to every backdrop in the parent host.
                boundaryDfsIndex = int.MinValue;
            }

            if (coarse is not { IsZeroSize: false } coarseRect)
                return;

            parentCollector.AddRect(coarseRect);
            _backdropExpansions.Add((parentCollector, coarseRect, boundaryDfsIndex));
        }

        public void RequestFullRedraw() => _redrawRequested = true;

        /// <summary>
        /// A full redraw is decided after the update pass computed its grants from
        /// the frame's damage, so a backdrop that damage never touched got none.
        /// The whole surface is painted before any backdrop samples it, which makes
        /// the frame safe to capture from for every backdrop sampling the target:
        /// grant a full refresh to each one that retains nothing yet. A valid
        /// payload still matches what is beneath it and is left alone. Backdrops in
        /// a bitmap-cache host sample that host's surface, which a target redraw
        /// does not repaint.
        /// </summary>
        private void GrantFullRedrawCaptures()
        {
            if (_backdropVisuals.Count == 0 || (Compositor.Options.UseSaveLayerRootClip ?? false))
                return;

            foreach (var visual in _backdropVisuals)
            {
                if (visual.BackdropCache is not { IsValid: false } cache
                    || cache.Payload == BackdropCachePayload.None
                    || NearestCacheHostOwner(visual) != null
                    || visual.TryGetWorldBounds(Matrix.Identity, out var cacheable) == null
                    || !cacheable)
                    continue;

                cache.RefreshRequested = true;
                cache.RefreshRects.Clear();
                cache.IngestRects.Clear();
                _fullRedrawGrants.Add(cache);
            }
        }

        /// <summary>
        /// A full-redraw grant certifies only the frame it was issued for. One the
        /// render pass did not consume (the backdrop was culled, or drawn where the
        /// backend could not capture) must not fire on a later frame that repaints
        /// only part of the surface.
        /// </summary>
        private void WithdrawFullRedrawGrants()
        {
            foreach (var cache in _fullRedrawGrants)
            {
                if (!cache.IsValid)
                    cache.RefreshRequested = false;
            }

            _fullRedrawGrants.Clear();
        }
    }

    /// <summary>
    /// A registered backdrop's per-frame classification within its host - the
    /// bitmap cache (or the target) whose surface the backdrop samples. The
    /// update walk captures visited backdrops at their own walk position:
    /// damage already emitted at that point painted at or beneath the sample
    /// point; later damage paints above it inside the backdrop's layer and
    /// cannot change what the filter reads. Backdrops the walk never reached
    /// (a clean spine) are classified by the post-walk journal sweep instead
    /// and carry <see cref="DfsIndex"/> <see cref="int.MaxValue"/>: they have
    /// no walk position, so every expansion delivers to them.
    /// </summary>
    internal readonly record struct BackdropCapture(
        ServerCompositionVisual Visual,
        IDirtyRectCollector Collector,
        int DfsIndex,
        bool SelfDirty,
        bool BelowDirt,
        LtrbRect? HostBounds,
        bool Cacheable,
        int DirtStart = 0,
        int DirtCount = 0,
        bool DirtOverflow = true,
        Matrix? LocalToHost = null,
        bool StalePartialGrant = false);

    /// <summary>
    /// A dirty rect emitted by the update walk into a host collector, tagged
    /// with the visual that emitted it. Backdrops the walk never visited have
    /// no stamped position, so the post-walk sweep classifies these entries by
    /// document order against the glass instead.
    /// </summary>
    internal readonly record struct BackdropJournalEntry(
        IDirtyRectCollector Host,
        LtrbRect Rect,
        ServerCompositionVisual Emitter);

    /// <summary>
    /// A bitmap-cache host the update walk entered, recorded at the cache
    /// redirect push: its collector, the collector of the host it nests in,
    /// the owning visual with its walk position there, and the matrix mapping
    /// the owner's inner space to the parent host's space (only meaningful
    /// when the walk had pushed the ancestor transforms).
    /// </summary>
    internal readonly record struct BackdropHostRecord(
        IDirtyRectCollector HostCollector,
        IDirtyRectCollector ParentCollector,
        ServerCompositionVisual Owner,
        int OwnerDfsIndex,
        Matrix OwnerInnerToParent,
        bool OwnerInnerToParentValid);
}
