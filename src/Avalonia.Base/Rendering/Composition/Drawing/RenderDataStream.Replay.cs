using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Drawing;

internal partial class RenderDataStream
{
    private static bool s_warnedAboutLayerFallback;
    private static bool s_warnedAboutLuminanceFallback;

    internal struct ReplayScope
    {
        public RenderDataOpcode Kind;
        public bool Active;
        public Matrix SavedTransform;
        public bool FallbackOpacity;
        public bool FallbackEffect;
    }

    internal struct ReplayVisitor : IRenderDataVisitor<ReplayScope>
    {
        private readonly IDrawingContextImpl _context;

        public ReplayVisitor(IDrawingContextImpl context)
        {
            _context = context;
        }

        public bool StopVisiting => false;

        public void OnDrawLine(IPen? serverPen, IPen? clientPen, Point p1, Point p2)
            => _context.DrawLine(serverPen, p1, p2);

        public void OnDrawRectangle(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, RoundedRect rect,
            BoxShadows boxShadows)
            => _context.DrawRectangle(serverBrush, serverPen, rect, boxShadows);

        public void OnDrawEllipse(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, Rect rect)
            => _context.DrawEllipse(serverBrush, serverPen, rect);

        public void OnDrawGeometry(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, IGeometryImpl? geometry)
        {
            if (geometry != null)
                _context.DrawGeometry(serverBrush, serverPen, geometry);
        }

        public void OnDrawGlyphRun(IBrush? serverBrush, IRef<IGlyphRunImpl>? glyphRun)
        {
            if (glyphRun != null)
                _context.DrawGlyphRun(serverBrush, glyphRun.Item);
        }

        public void OnDrawBitmap(IRef<IBitmapImpl>? bitmap, double opacity, Rect sourceRect, Rect destRect)
        {
            if (bitmap != null)
                _context.DrawBitmap(bitmap.Item, opacity, sourceRect, destRect);
        }

        public void OnDrawCustom(ICustomDrawOperation? operation)
            => operation?.Render(new ImmediateDrawingContext(_context, false));

        public void OnDrawRecording(ServerCompositionRenderData? server, CompositionRenderData? client,
            RenderDataStream? stream, Matrix transform)
        {
            // Replay always draws through the server-side data (server brush and
            // pen instances), matching what the compositor renders on both the
            // UI-thread and render-thread replay paths; an immutable child
            // replays its own retained stream.
            if (transform.IsIdentity)
            {
                RenderRecordingContent(server, stream);
                return;
            }

            var saved = _context.Transform;
            _context.Transform = transform * saved;
            RenderRecordingContent(server, stream);
            _context.Transform = saved;
        }

        private void RenderRecordingContent(ServerCompositionRenderData? server, RenderDataStream? stream)
        {
            if (server != null)
                server.Render(_context);
            else
                stream?.Replay(_context);
        }

        public ReplayScope OnPushClip(RoundedRect clip)
        {
            _context.PushClip(clip);
            return new ReplayScope { Kind = RenderDataOpcode.PushClip, Active = true };
        }

        public ReplayScope OnPushGeometryClip(IGeometryImpl? geometry)
        {
            if (geometry != null)
                _context.PushGeometryClip(geometry);
            return new ReplayScope
                { Kind = RenderDataOpcode.PushGeometryClip, Active = geometry != null };
        }

        public ReplayScope OnPushOpacity(double opacity)
        {
            if (opacity != 1)
                _context.PushOpacity(opacity, null);
            return new ReplayScope { Kind = RenderDataOpcode.PushOpacity, Active = opacity != 1 };
        }

        public ReplayScope OnPushOpacityMask(IBrush? brush, Rect bounds, MaskType maskType)
        {
            if (brush != null)
            {
                if (maskType == MaskType.Luminance && _context is IDrawingContextImplWithLuminanceMask luminanceImpl)
                {
                    luminanceImpl.PushOpacityMask(brush, bounds, MaskType.Luminance);
                }
                else
                {
                    if (maskType == MaskType.Luminance && !s_warnedAboutLuminanceFallback)
                    {
                        s_warnedAboutLuminanceFallback = true;
                        Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(
                            _context,
                            "Backend does not implement IDrawingContextImplWithLuminanceMask; " +
                            "falling back to alpha mask. SVG luminance-mode masks will render " +
                            "incorrectly.");
                    }

                    _context.PushOpacityMask(brush, bounds);
                }
            }
            return new ReplayScope { Kind = RenderDataOpcode.PushOpacityMask, Active = brush != null };
        }

        public ReplayScope OnPushTransform(Matrix matrix)
        {
            var saved = _context.Transform;
            _context.Transform = matrix * saved;
            return new ReplayScope
                { Kind = RenderDataOpcode.PushTransform, Active = true, SavedTransform = saved };
        }

        public ReplayScope OnPushRenderOptions(RenderOptions options)
        {
            _context.PushRenderOptions(options);
            return new ReplayScope { Kind = RenderDataOpcode.PushRenderOptions, Active = true };
        }

        public ReplayScope OnPushTextOptions(TextOptions options)
        {
            _context.PushTextOptions(options);
            return new ReplayScope { Kind = RenderDataOpcode.PushTextOptions, Active = true };
        }

        public ReplayScope OnPushEffect(IEffect? effect, Rect bounds)
        {
            var active = false;
            if (effect != null && _context is IDrawingContextImplWithEffects effectImpl)
            {
                effectImpl.PushEffect(bounds, effect);
                active = true;
            }
            return new ReplayScope { Kind = RenderDataOpcode.PushEffect, Active = active };
        }

        public ReplayScope OnPushLayer(LayerOptions options)
        {
            if (options.IsPassthrough)
                return new ReplayScope { Kind = RenderDataOpcode.PushLayer, Active = false };

            if (_context is IDrawingContextImplWithLayers layerImpl)
            {
                layerImpl.PushLayer(options);
                return new ReplayScope { Kind = RenderDataOpcode.PushLayer, Active = true };
            }

            if ((options.EffectiveBlendMode != BitmapBlendingMode.SourceOver
                    || options.Isolate
                    || options.BackdropEffect != null)
                && !s_warnedAboutLayerFallback)
            {
                s_warnedAboutLayerFallback = true;
                Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(
                    _context,
                    "Backend does not implement IDrawingContextImplWithLayers; " +
                    "layer blend mode / isolation / backdrop effect cannot be honored.");
            }

            // Closest approximation without native layer support: compose the existing
            // effect / opacity capabilities; blend mode, isolation and the backdrop
            // effect (which needs the destination, not the layer content) cannot be
            // honored here.
            var scope = new ReplayScope { Kind = RenderDataOpcode.PushLayer };

            if (options.Effect is { } effect && _context is IDrawingContextImplWithEffects effectImpl)
            {
                effectImpl.PushEffect(options.Bounds, effect);
                scope.FallbackEffect = true;
                scope.Active = true;
            }

            if (options.EffectiveOpacity < 1.0)
            {
                _context.PushOpacity(options.EffectiveOpacity, options.Bounds);
                scope.FallbackOpacity = true;
                scope.Active = true;
            }

            return scope;
        }

        public void OnPop(in ReplayScope scope)
        {
            if (!scope.Active)
                return;

            switch (scope.Kind)
            {
                case RenderDataOpcode.PushClip:
                    _context.PopClip();
                    break;
                case RenderDataOpcode.PushGeometryClip:
                    _context.PopGeometryClip();
                    break;
                case RenderDataOpcode.PushOpacity:
                    _context.PopOpacity();
                    break;
                case RenderDataOpcode.PushOpacityMask:
                    _context.PopOpacityMask();
                    break;
                case RenderDataOpcode.PushTransform:
                    _context.Transform = scope.SavedTransform;
                    break;
                case RenderDataOpcode.PushRenderOptions:
                    _context.PopRenderOptions();
                    break;
                case RenderDataOpcode.PushTextOptions:
                    _context.PopTextOptions();
                    break;
                case RenderDataOpcode.PushEffect:
                    ((IDrawingContextImplWithEffects)_context).PopEffect();
                    break;
                case RenderDataOpcode.PushLayer:
                    if (scope.FallbackOpacity || scope.FallbackEffect)
                    {
                        // Pop in reverse push order: opacity (innermost) first, then effect.
                        if (scope.FallbackOpacity)
                            _context.PopOpacity();
                        if (scope.FallbackEffect)
                            ((IDrawingContextImplWithEffects)_context).PopEffect();
                    }
                    else
                        _context.PopLayer();
                    break;
            }
        }
    }

    public void Replay(IDrawingContextImpl context)
    {
        var visitor = new ReplayVisitor(context);
        Visit<ReplayVisitor, ReplayScope>(ref visitor);
    }
}
