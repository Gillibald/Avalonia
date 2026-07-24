using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Server
{
    /// <summary>
    /// Server-side <see cref="CompositionVisual"/> counterpart.
    /// Is responsible for computing the transformation matrix, for applying various visual
    /// properties before calling visual-specific drawing code and for notifying the
    /// <see cref="ServerCompositionTarget"/> for new dirty rects
    /// </summary>
    partial class ServerCompositionVisual : ServerObject
    {
        public ServerCompositionVisualCollection? Children { get; private set; } = null!;
        public ServerCompositionVisualCache? Cache { get; private set; }

        partial void OnRootChanging()
        {
            if (Root != null)
            {
                UnregisterBackdrop(Root);
                Root.RemoveVisual(this);
                OnDetachedFromRoot(Root);
            }
        }

        protected virtual void OnDetachedFromRoot(ServerCompositionTarget target)
        {
        }

        partial void OnRootChanged()
        {
            if (Root != null)
            {
                Root.AddVisual(this);
                OnAttachedToRoot(Root);
                AdornerHelper_AttachedToRoot();
            }
            Cache?.FreeResources();
            UpdateBackdropRegistration();
        }

        partial void OnBackdropEffectChanged() => UpdateBackdropRegistration();

        private bool _registeredAsBackdrop;

        // Number of registered backdrops in this node's subtree (excluding this
        // node itself), maintained eagerly along the whole parent chain. The
        // update walk uses it to keep descending clean spines toward backdrops
        // that need their frame classified.
        internal int _backdropsInSubTree;

        private int BackdropContributionToParent =>
            (_registeredAsBackdrop ? 1 : 0) + _backdropsInSubTree;

        private static void PropagateBackdropCountDelta(ServerCompositionVisual? start, int delta)
        {
            if (delta == 0)
                return;
            for (var node = start; node != null; node = node.Parent)
                node._backdropsInSubTree += delta;
        }

        private void Backdrop_OnParentChanging() =>
            // Parent still points at the old parent here.
            PropagateBackdropCountDelta(Parent, -BackdropContributionToParent);

        private void Backdrop_OnParentChanged() =>
            PropagateBackdropCountDelta(Parent, BackdropContributionToParent);

        /// <summary>
        /// Retains the filtered backdrop across frames so that changes painted
        /// on top of this visual do not force everything beneath it to repaint.
        /// Created with the backdrop registration, freed with it; see
        /// <see cref="BackdropLayerCache"/> for the update/render handshake.
        /// </summary>
        internal BackdropLayerCache? BackdropCache { get; private set; }

        /// <summary>
        /// A backdrop reads the surface beneath it, which no other visual's dirty
        /// rect accounts for, so the target keeps a list of them and widens the
        /// dirty region to cover their whole area. Registration is kept in step
        /// with both the effect and the visual's attachment to a target.
        /// </summary>
        private void UpdateBackdropRegistration()
        {
            var shouldRegister = BackdropEffect != null && Root != null;
            if (shouldRegister == _registeredAsBackdrop)
                return;

            _registeredAsBackdrop = shouldRegister;
            PropagateBackdropCountDelta(Parent, shouldRegister ? 1 : -1);
            if (shouldRegister)
            {
                BackdropCache = new BackdropLayerCache();
                Root!.AddBackdropVisual(this);
            }
            else
            {
                Root?.RemoveBackdropVisual(this);
                BackdropCache?.Dispose();
                BackdropCache = null;
            }
        }

        private void UnregisterBackdrop(ServerCompositionTarget target)
        {
            if (!_registeredAsBackdrop)
                return;

            _registeredAsBackdrop = false;
            PropagateBackdropCountDelta(Parent, -1);
            target.RemoveBackdropVisual(this);
            BackdropCache?.Dispose();
            BackdropCache = null;
        }

        /// <summary>
        /// This visual's bounds in the target's coordinate space, or null when it
        /// contributes nothing. The update walk only descends into dirty subtrees,
        /// so a backdrop that did not itself change is never visited and its world
        /// transform has to be rebuilt from the parent chain.
        /// </summary>
        /// <param name="rootTransform">The target's root transform.</param>
        /// <param name="cacheable">
        /// Whether a backdrop on this visual samples the base surface directly.
        /// The visual's own opacity or mask, or any ancestor opacity, mask,
        /// effect or backdrop, wraps it in a save-layer; the live sample then
        /// reads that layer while a snapshot reads the base surface, so such a
        /// backdrop cannot use the cached path.
        /// </param>
        internal LtrbRect? TryGetWorldBounds(Matrix rootTransform, out bool cacheable)
            => TryGetHostBounds(rootTransform, stopAtHostOwner: null, out cacheable);

        /// <summary>
        /// Same as <see cref="TryGetWorldBounds"/>, but resolved in a bitmap
        /// cache host's space: the ancestor walk stops at the host owner (its
        /// clip applies, its own transform does not - the cache texture lives
        /// in its inner space), and the root transform is not applied. The
        /// cacheable accumulation covers only ancestors inside the host, since
        /// the backdrop samples the host surface.
        /// </summary>
        internal LtrbRect? TryGetHostBounds(Matrix rootTransform, ServerCompositionVisual? stopAtHostOwner,
            out bool cacheable)
        {
            cacheable = Opacity == 1 && OpacityMaskBrush == null;

            // A subtree that paints nothing at all is culled by the render
            // pass, so there is no fill to keep fresh either.
            if (_transformedSubTreeBounds == null || !Visible)
                return null;

            // The backdrop region is the control's own box refined by its clip
            // - never the subtree's paint union - so overflowing children and
            // effect output do not widen it. Mapped to the parent's space
            // first; each ancestor then clips in its own space and maps up one
            // level.
            var rect = new LtrbRect(0, 0, Size.X, Size.Y);
            if (_ownClipRect.HasValue)
                rect = rect.IntersectOrEmpty(_ownClipRect.Value);
            if (rect.IsZeroSize)
                return null;
            if (_ownTransform.HasValue)
                rect = rect.TransformToAABB(_ownTransform.Value);
            for (var parent = Parent; parent != null; parent = parent.Parent)
            {
                if (!parent.Visible)
                    return null;

                if (parent.Opacity != 1 || parent.OpacityMaskBrush != null
                    || parent.Effect != null || parent.BackdropEffect != null)
                    cacheable = false;

                if (parent._ownClipRect.HasValue)
                {
                    rect = rect.IntersectOrEmpty(parent._ownClipRect.Value);
                    if (rect.IsZeroSize)
                        return null;
                }

                // The host owner's inner space is where its cache texture
                // lives: apply its clip, but stop before its own transform.
                if (ReferenceEquals(parent, stopAtHostOwner))
                    return rect;

                if (parent._ownTransform.HasValue)
                    rect = rect.TransformToAABB(parent._ownTransform.Value);
            }

            return rect.TransformToAABB(rootTransform);
        }

        protected virtual void OnAttachedToRoot(ServerCompositionTarget target)
        {
        }

        partial void OnCacheModeChanging()
        {
            CacheMode?.Unsubscribe(this);
            Cache?.FreeResources();
            Cache = null;
        }
        
        partial void OnCacheModeChanged()
        {
            Cache = CacheMode is ServerCompositionBitmapCache bitmapCache ? new ServerCompositionVisualCache(this, bitmapCache) : null;
            CacheMode?.Subscribe(this);
            OnCacheModeStateChanged();
        }

        public void OnCacheModeStateChanged()
        {
            Cache?.InvalidateProperties();
            InvalidateContent();
        }


        protected virtual void RenderCore(ServerVisualRenderContext context, LtrbRect currentTransformedClip)
        {
        }

        partial void Initialize()
        {
            Children = new ServerCompositionVisualCollection(Compositor);
        }
    }
}
