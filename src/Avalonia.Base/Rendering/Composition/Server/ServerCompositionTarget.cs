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
        private bool _updateRequested;
        private bool _redrawRequested;
        private bool _fullRedrawRequested;
        private bool _disposed;
        private readonly HashSet<ServerCompositionVisual> _attachedVisuals = new();
        private readonly HashSet<ServerCompositionVisual> _backdropVisuals = new();
        private readonly List<BackdropCapture> _backdropCaptures = new();
        private readonly List<(LtrbRect Rect, int DfsIndex)> _backdropExpansions = new();
        private readonly List<LtrbRect> _backdropWorkingSet = new();
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
                // knows: it classifies each backdrop at its own walk position.
                List<BackdropCapture>? backdropCaptures = null;
                if (_backdropVisuals.Count > 0)
                {
                    _backdropCaptures.Clear();
                    backdropCaptures = _backdropCaptures;
                }

                Root.UpdateRoot(collector, transform, new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height),
                    backdropCaptures);

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

            var needLayer = _overlays.RequireLayer // Check if we don't need overlays
                            // Check if render target can be rendered to directly and preserves the previous frame
                            || !(_renderTarget.Properties.RetainsPreviousFrameContents
                                 && _renderTarget.Properties.IsSuitableForDirectRendering);

            IDrawingContextImpl renderTargetContext;
            RenderTargetDrawingContextProperties properties;
            try
            {
                renderTargetContext =
                    _renderTarget.CreateDrawingContext(new(PixelSize, Scaling, Size, TransparencyLevel), out properties);
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
                }

                if (!DirtyRects.IsEmpty)
                {
                    DirtyRects.FinalizeFrame(renderBounds);
                    if (_layer != null)
                    {
                        using (var context = _layer.CreateDrawingContext())
                            RenderRootToContextWithClip(context, Root);

                        renderTargetContext.Clear(Colors.Transparent);
                        renderTargetContext.Transform = Matrix.Identity;
                        if (_layer.CanBlit)
                            _layer.Blit(renderTargetContext);
                        else
                        {
                            var rect = new PixelRect(default, PixelSize).ToRect(1);
                            renderTargetContext.DrawBitmap(_layer, 1, rect, rect);
                        }
                        _overlays.Draw(renderTargetContext, true);
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
        private void ExpandDirtyRegionForBackdrops(IDirtyRectCollector collector, Matrix transform)
        {
            if (_backdropVisuals.Count == 0)
                return;

            var surface = new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height);

            // A save-layer around the whole frame means every backdrop samples
            // that layer instead of the base surface the snapshot would read.
            var rootUsesLayer = Compositor.Options.UseSaveLayerRootClip ?? false;

            // Registered backdrops the walk never reached (a clean spine): no
            // position and no classification exist, so any touch invalidates.
            foreach (var visual in _backdropVisuals)
            {
                var captured = false;
                for (var i = 0; i < _backdropCaptures.Count && !captured; i++)
                    captured = ReferenceEquals(_backdropCaptures[i].Visual, visual);
                if (!captured)
                {
                    var worldBounds = visual.TryGetWorldBounds(transform, out var uncapturedCacheable);
                    _backdropCaptures.Add(new BackdropCapture(
                        visual, int.MaxValue, SelfDirty: false, BelowDirt: false,
                        worldBounds, uncapturedCacheable, Captured: false));
                }
            }

            _backdropExpansions.Clear();

            // Expanding one backdrop can reach another, so repeat until a pass adds
            // nothing. Each pass covers at least one more backdrop, so the count
            // bounds the loop.
            for (var pass = 0; pass < _backdropCaptures.Count; pass++)
            {
                var added = false;

                // The raw working set only grows via the expansions this loop
                // adds, so one refresh per pass is enough for the touch and
                // containment tests.
                _backdropWorkingSet.Clear();
                collector.GetWorkingSet().CollectTo(_backdropWorkingSet);

                foreach (var capture in _backdropCaptures)
                {
                    var visual = capture.Visual;
                    if (visual.BackdropEffect is not { } effect)
                        continue;
                    if (capture.WorldBounds is not { } bounds)
                        continue;
                    var cacheable = capture.Cacheable && !rootUsesLayer;

                    var area = bounds.Inflate(effect.GetEffectOutputPadding()).IntersectOrEmpty(surface);
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
                    var invalidated = capture.SelfDirty || capture.BelowDirt || !capture.Captured;
                    if (!invalidated)
                    {
                        foreach (var expansion in _backdropExpansions)
                        {
                            if (expansion.DfsIndex < capture.DfsIndex && expansion.Rect.Intersects(area))
                            {
                                invalidated = true;
                                break;
                            }
                        }
                    }

                    var cache = visual.BackdropCache;
                    if (invalidated && cache != null)
                        cache.IsValid = false;

                    if (cacheable && cache is { IsValid: true })
                        continue; // the retained result gets drawn; nothing beneath needs repainting

                    if (cacheable && cache != null)
                        cache.RefreshRequested = true;

                    if (contained)
                        continue; // some rect already repaints the full input; nothing to widen

                    collector.AddRect(area);
                    _backdropExpansions.Add((area, capture.DfsIndex));
                    added = true;
                }

                if (!added)
                    return;
            }
        }

        public void RequestFullRedraw() => _redrawRequested = true;
    }

    /// <summary>
    /// A registered backdrop's per-frame classification, captured by the update
    /// walk at the visual's own walk position. Damage already emitted at that
    /// point painted at or beneath the sample point; later damage paints above
    /// it inside the backdrop's layer and cannot change what the filter reads.
    /// <paramref name="Captured"/> is false for backdrops the walk never
    /// reached - they carry no position, so any touch invalidates.
    /// </summary>
    internal readonly record struct BackdropCapture(
        ServerCompositionVisual Visual,
        int DfsIndex,
        bool SelfDirty,
        bool BelowDirt,
        LtrbRect? WorldBounds,
        bool Cacheable,
        bool Captured);
}
