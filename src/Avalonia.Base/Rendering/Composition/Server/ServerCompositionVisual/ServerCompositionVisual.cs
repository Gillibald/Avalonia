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
            target.RemoveBackdropVisual(this);
            BackdropCache?.Dispose();
            BackdropCache = null;
        }

        /// <summary>
        /// The bit identifying this backdrop in the per-frame dirty-rect masks,
        /// assigned by the target each update. Zero past 64 registered backdrops,
        /// which just makes classification conservative for the overflow.
        /// </summary>
        internal ulong BackdropMaskBit { get; set; }

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
        {
            cacheable = Opacity == 1 && OpacityMaskBrush == null;

            if (_transformedSubTreeBounds == null || !Visible)
                return null;

            // _transformedSubTreeBounds is expressed in the parent's space, so each
            // ancestor clips in its own space first and then maps up one level.
            // An own Effect widens the subtree bounds by its output padding, but
            // the backdrop samples only the content's area beneath it.
            var rect = (_transformedSubTreeBoundsWithoutEffect ?? _transformedSubTreeBounds).Value;
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
