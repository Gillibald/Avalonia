namespace Avalonia.Rendering.Composition.Drawing.Nodes;

internal class RenderDataRecordingItemListNode : IRenderDataItem
{
    public required RenderItemList Items { get; init; }

    /// <summary>
    /// Transform fused into the node by
    /// <see cref="Media.DrawingContext.DrawRecording(DrawingRecording, Matrix)"/> so the
    /// common "draw this recording at this transform" case records a single node.
    /// </summary>
    public Matrix Transform { get; init; } = Matrix.Identity;

    public void Invoke(ref RenderDataNodeRenderContext context)
    {
        if (Transform.IsIdentity)
        {
            Items.Render(context.Context);
            return;
        }

        var ctx = context.Context;
        var saved = ctx.Transform;
        ctx.Transform = Transform * saved;
        Items.Render(ctx);
        ctx.Transform = saved;
    }

    public Rect? Bounds => Transform.IsIdentity ? Items.Bounds : Items.GetBounds(Transform);

    public bool HitTest(Point p)
    {
        if (Transform.IsIdentity)
            return Items.HitTest(p);
        return Transform.TryInvert(out var inverted) && Items.HitTest(p.Transform(inverted));
    }
}
