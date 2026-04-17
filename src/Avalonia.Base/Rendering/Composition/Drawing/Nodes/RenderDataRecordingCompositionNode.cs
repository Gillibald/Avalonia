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

    public void Invoke(ref RenderDataNodeRenderContext context)
    {
        Server.Render(context.Context);
    }

    public Rect? Bounds => Client.Bounds;

    public bool HitTest(Point p) => Client.HitTest(p);

    public void Collect(IRenderDataServerResourcesCollector collector)
    {
        collector.AddRenderDataServerResource(Server);
    }
}
