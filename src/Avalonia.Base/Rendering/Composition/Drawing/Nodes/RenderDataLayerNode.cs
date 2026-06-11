using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Rendering.Composition.Drawing.Nodes;

/// <summary>
/// A push-node that records a <see cref="LayerOptions"/> and replays it as a
/// compositing layer. Drives SVG <c>&lt;g opacity&gt;</c>,
/// <c>mix-blend-mode</c> and <c>&lt;filter&gt;</c>.
/// </summary>
internal class RenderDataLayerNode : RenderDataPushNode
{
    private static bool s_warnedAboutLayerFallback;

    public LayerOptions Options { get; set; }

    public override Rect? Bounds
    {
        get
        {
            // The recording's visible bounds reflect drawn pixels. LayerOptions.Bounds
            // is a backend hint for the compositor's offscreen buffer extent — it
            // does not by itself produce visible pixels and must not extend the
            // recorded bounds. Effect inflation (e.g. blur) does affect visible
            // pixels and is included. An effect can paint without any content
            // (a flood fills its whole layer), so an empty layer with an
            // effect is as large as the layer itself.
            var inner = base.Bounds;
            if (inner == null)
                return Options.Effect != null ? Options.Bounds : null;
            return InflateForEffect(inner.Value);
        }
    }

    // The recorded effect is always an immutable snapshot (see PushLayerCore),
    // so the inflation itself is thread-safe; only the child union differs
    // between the client and server passes.
    public override Rect? ServerBounds
    {
        get
        {
            var inner = base.ServerBounds;
            if (inner == null)
                return Options.Effect != null ? Options.Bounds : null;
            return InflateForEffect(inner.Value);
        }
    }

    // An effect produces output even over empty content (a flood fills the
    // layer), so effect layers survive empty-node elision.
    public override bool ProducesOutputWithoutChildren => Options.Effect != null;

    public override void Push(ref RenderDataNodeRenderContext context)
    {
        if (Children.Count == 0 && !ProducesOutputWithoutChildren)
            return;

        if (context.Context is IDrawingContextImplWithLayers native)
        {
            native.PushLayer(Options);
            return;
        }

        if (!Options.IsPassthrough
            && (Options.EffectiveBlendMode != BitmapBlendingMode.SourceOver || Options.Isolate)
            && !s_warnedAboutLayerFallback)
        {
            s_warnedAboutLayerFallback = true;
            Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(
                this,
                "Backend does not implement IDrawingContextImplWithLayers; " +
                "layer blend mode / isolation cannot be honored.");
        }

        // Compose fallback: effect via IDrawingContextImplWithEffects, then opacity.
        if (Options.Effect is { } effect
            && context.Context is IDrawingContextImplWithEffects effects)
            effects.PushEffect(Options.Bounds, effect);

        if (Options.EffectiveOpacity < 1.0)
            context.Context.PushOpacity(Options.EffectiveOpacity, Options.Bounds);
    }

    public override void Pop(ref RenderDataNodeRenderContext context)
    {
        if (Children.Count == 0 && !ProducesOutputWithoutChildren)
            return;

        if (context.Context is IDrawingContextImplWithLayers native)
        {
            native.PopLayer();
            return;
        }

        if (Options.EffectiveOpacity < 1.0)
            context.Context.PopOpacity();

        if (Options.Effect is not null
            && context.Context is IDrawingContextImplWithEffects effects)
            effects.PopEffect();
    }

    private Rect InflateForEffect(Rect inner)
    {
        // The shared output-padding helper covers every effect type (blur,
        // drop shadow, offset, color matrix, composite chains).
        var padding = Options.Effect.GetEffectOutputPadding();
        return padding == default ? inner : inner.Inflate(padding);
    }
}
