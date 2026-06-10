using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Rendering.Composition.Drawing.Nodes;

class RenderDataOpacityMaskNode : RenderDataPushNode, IRenderDataItemWithServerResources
{
    private static bool s_warnedAboutLuminanceFallback;

    public IBrush? ServerBrush { get; set; }

    public Rect BoundsRect { get; set; }

    public MaskType MaskType { get; set; } = MaskType.Alpha;

    public void Collect(IRenderDataServerResourcesCollector collector)
    {
        collector.AddRenderDataServerResource(ServerBrush);
    }

    public override void Push(ref RenderDataNodeRenderContext context)
    {
        if (ServerBrush == null)
            return;

        if (MaskType == MaskType.Luminance
            && context.Context is IDrawingContextImplWithLuminanceMask probe)
        {
            probe.PushOpacityMask(ServerBrush, BoundsRect, MaskType.Luminance);
            return;
        }

        if (MaskType == MaskType.Luminance && !s_warnedAboutLuminanceFallback)
        {
            s_warnedAboutLuminanceFallback = true;
            Logger.TryGet(LogEventLevel.Warning, LogArea.Visual)?.Log(
                this,
                "Backend does not implement IDrawingContextImplWithLuminanceMask; " +
                "falling back to alpha mask. SVG luminance-mode masks will render " +
                "incorrectly.");
        }

        context.Context.PushOpacityMask(ServerBrush, BoundsRect);
    }

    public override void Pop(ref RenderDataNodeRenderContext context)
    {
        // Mirror Push: when no mask brush was pushed there is nothing to pop.
        if (ServerBrush == null)
            return;
        context.Context.PopOpacityMask();
    }
}