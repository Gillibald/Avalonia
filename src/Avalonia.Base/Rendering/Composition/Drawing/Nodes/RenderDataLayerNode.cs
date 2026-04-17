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
            var inner = base.Bounds;
            if (inner == null)
                return Options.Bounds;

            // Explicit layer bounds (if supplied) contain the effective rendered
            // area; otherwise the inner children's bounds drive the layer.
            var effectBounds = InflateForEffect(inner.Value);
            return Options.Bounds is { } bounds ? bounds.Union(effectBounds) : effectBounds;
        }
    }

    public override void Push(ref RenderDataNodeRenderContext context)
    {
        if (Children.Count == 0)
            return;

        if (context.Context is IDrawingContextImplWithLayers native)
        {
            native.PushLayer(Options);
            return;
        }

        if (!Options.IsPassthrough
            && Options.EffectiveBlendMode != BitmapBlendingMode.SourceOver
            && !s_warnedAboutLayerFallback)
        {
            s_warnedAboutLayerFallback = true;
            Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(
                this,
                "Backend does not implement IDrawingContextImplWithLayers; " +
                "layer blend mode '{0}' cannot be honored and will render as SourceOver.",
                Options.EffectiveBlendMode);
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
        if (Children.Count == 0)
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
        if (Options.Effect is IBlurEffect blur)
            return inner.Inflate(blur.Radius);
        if (Options.Effect is IDropShadowEffect drop)
        {
            var expanded = inner.Translate(new Vector(drop.OffsetX, drop.OffsetY));
            return inner.Union(expanded).Inflate(drop.BlurRadius);
        }
        return inner;
    }
}
