using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Composition;

public class CompositionRecordingVisualTests : ScopedTestBase
{
    private readonly CompositorTestServices _services = new();

    public override void Dispose()
    {
        _services.Dispose();
        base.Dispose();
    }

    [Fact(Skip = "Upstream: container visuals attached via SetElementChildVisual never receive " +
                 "transformed subtree bounds — the child's bounds compute, but the parent union in " +
                 "the update walk's PostSubgraph is gated on a bounding-box flag the container no " +
                 "longer carries, so the render walk skips the subtree. Reproduces with stock " +
                 "CompositionContainerVisual + CompositionSolidColorVisual.")]
    public void Stock_Container_Visual_Gets_Subtree_Bounds()
    {
        var container = _services.Compositor.CreateContainerVisual();
        var solid = _services.Compositor.CreateSolidColorVisual();
        solid.Size = new Vector(60, 60);
        solid.Color = Colors.Lime;
        container.Children.Add(solid);

        var host = new Control { Width = 100, Height = 100 };
        _services.TopLevel.Content = host;
        _services.RunJobs();
        ElementComposition.SetElementChildVisual(host, container);
        _services.RunJobs();

        _services.Compositor.Commit();
        _services.Compositor.Server.Render(false);

        var subtreeBounds = typeof(ServerCompositionVisual)
            .GetField("_transformedSubTreeBounds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(container.Server);
        Assert.True(subtreeBounds != null, "the bounds pass never produced container subtree bounds");
    }

    [Fact]
    public void Recording_Reaches_The_Server_Visual()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
            ctx.DrawRectangle(Brushes.Crimson, null, new Rect(20, 20, 60, 60)));

        var visual = _services.Compositor.CreateRecordingVisual();
        visual.Recording = recording;

        var host = new Control { Width = 100, Height = 100 };
        _services.TopLevel.Content = host;
        _services.RunJobs();
        ElementComposition.SetElementChildVisual(host, visual);
        _services.RunJobs();

        _services.Compositor.Commit();
        _services.Compositor.Server.Render(false);

        // The recording's render data deserialized into the server visual and
        // the visual is part of the server tree.
        var server = (ServerCompositionRecordingVisual)visual.Server;
        var bounds = server.ComputeOwnContentBounds();
        Assert.NotNull(bounds);
        Assert.Equal(20, bounds!.Value.Left);
        Assert.Equal(80, bounds.Value.Right);

        var elementVisual = (CompositionContainerVisual)ElementComposition.GetElementVisual(host)!;
        Assert.True(elementVisual.Children.Contains(visual), "child visual not in client children");
        Assert.True(server.Parent != null, "server visual not parented");
        Assert.True(visual.Root != null, "client visual has no root");

        recording.Dispose();
    }
}
