using Avalonia.Rendering.Composition.Server;

namespace Avalonia.Rendering.Composition.Drawing.Nodes;

internal class RenderDataRecordingCompositionNode : IRenderDataItemWithServerResources
{
    /// <summary>
    /// The server-side render data used at render time. Populated after the
    /// child recording's batch is serialized.
    /// </summary>
    public required ServerCompositionRenderData Server { get; init; }

    /// <summary>
    /// The client-side render data used for synchronous bounds and hit-test
    /// queries. The client-side item list is always populated after the
    /// record delegate returns, so it is the source of truth for bounds
    /// until (and after) the server has been updated.
    /// </summary>
    public required CompositionRenderData Client { get; init; }

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
            Server.Render(context.Context);
            return;
        }

        var ctx = context.Context;
        var saved = ctx.Transform;
        ctx.Transform = Transform * saved;
        Server.Render(ctx);
        ctx.Transform = saved;
    }

    public Rect? Bounds => Transform.IsIdentity ? Client.Bounds : Client.GetBounds(Transform);

    /// <summary>
    /// The server-side bounds pass must answer from the server render data:
    /// the client item list reads UI-thread-affine resources (mutable pens) and
    /// is not safe on the render thread. The server data only references
    /// resource shadows and recomputes when they change, so animated resources
    /// keep these bounds current.
    /// </summary>
    public Rect? ServerBounds
    {
        get
        {
            if (Server.Bounds?.ToRect() is not { } bounds)
                return null;
            return Transform.IsIdentity ? bounds : bounds.TransformToAABB(Transform);
        }
    }

    public bool HitTest(Point p)
    {
        if (Transform.IsIdentity)
            return Client.HitTest(p);
        return Transform.TryInvert(out var inverted) && Client.HitTest(p.Transform(inverted));
    }

    public void Collect(IRenderDataServerResourcesCollector collector)
    {
        collector.AddRenderDataServerResource(Server);
    }
}
