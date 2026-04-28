using Avalonia.Media;

namespace Avalonia.Rendering.Composition.Drawing.Nodes;

class RenderDataRectangleNode : RenderDataBrushAndPenNode
{
    public RoundedRect Rect { get; set; }
    public BoxShadows BoxShadows { get; set; }

    public override bool HitTest(Point p)
    {
        var strokeThicknessAdjustment = (ClientPen?.Thickness / 2) ?? 0;

        if (Rect.IsRounded)
        {
            var outerRoundedRect = Rect.Inflate(strokeThicknessAdjustment, strokeThicknessAdjustment);
            if (outerRoundedRect.ContainsExclusive(p))
            {
                if (ServerBrush != null) // it's safe to check for null
                    return true;

                var innerRoundedRect = Rect.Deflate(strokeThicknessAdjustment, strokeThicknessAdjustment);
                return !innerRoundedRect.ContainsExclusive(p);
            } 
        }
        else
        {
            var outerRect = Rect.Rect.Inflate(strokeThicknessAdjustment);
            if (outerRect.ContainsExclusive(p))
            {
                if (ServerBrush != null) // it's safe to check for null
                    return true;

                var innerRect = Rect.Rect.Deflate(strokeThicknessAdjustment);
                return !innerRect.ContainsExclusive(p);
            }
        }

        return false;
    }

    public override void Invoke(ref RenderDataNodeRenderContext context) =>
        context.Context.DrawRectangle(ServerBrush, ServerPen, Rect, BoxShadows);

    // Prefer ClientPen for bounds when set (client-side query): it's the live
    // mutable pen whose Thickness reflects pending writes that the server proxy
    // hasn't yet applied. On server-side instances ClientPen is null after
    // deserialize, so ServerPen carries the immutable snapshot.
    public override Rect? Bounds => BoxShadows.TransformBounds(Rect.Rect).Inflate(((ClientPen ?? ServerPen)?.Thickness ?? 0) / 2);
}
