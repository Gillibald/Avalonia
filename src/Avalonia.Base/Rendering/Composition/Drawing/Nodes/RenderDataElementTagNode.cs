namespace Avalonia.Rendering.Composition.Drawing.Nodes;

/// <summary>
/// A push-node that carries an opaque <see cref="Tag"/> associated with a range of
/// recorded draw operations. Tags are preserved in the recording and surfaced via
/// <see cref="DrawingRecording.HitTestElements(Point)"/>; they have no effect on the
/// rendered output or on plain hit-testing bounds.
/// </summary>
internal class RenderDataElementTagNode : RenderDataPushNode
{
    public required object Tag { get; init; }

    public override void Push(ref RenderDataNodeRenderContext context)
    {
        // Tags are metadata and do not alter the render context.
    }

    public override void Pop(ref RenderDataNodeRenderContext context)
    {
    }
}
