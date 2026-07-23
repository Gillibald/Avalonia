using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// The region a backdrop fills is shaped by a geometry the control declares
/// rather than the subtree-cropping <see cref="Visual.Clip"/>: a Border feeds a
/// rounded rectangle built from its own CornerRadius, an explicit
/// <see cref="Visual.BackdropClip"/> wins over it, and everything else stays
/// square. These check what actually arrives at the server visual.
/// </summary>
public class BackdropClipTests : CompositorTestsBase
{
    private static Avalonia.Platform.IGeometryImpl? ServerClipOf(CompositorTestServices s, Visual visual)
    {
        s.RunJobs();
        var composition = ElementComposition.GetElementVisual(visual)!;
        return ((ServerCompositionVisual)composition.Server!).BackdropClip;
    }

    private static Border Frosted() => new()
    {
        Width = 40,
        Height = 40,
        Background = new ImmutableSolidColorBrush(Colors.White, 0.3),
        CornerRadius = new CornerRadius(16),
        BackdropEffect = new ImmutableBlurEffect(4)
    };

    [Fact]
    public void Border_Should_Feed_A_Rounded_Backdrop_Clip_From_Its_Corner_Radius()
    {
        using var s = new CompositorCanvas();
        var panel = Frosted();
        s.Canvas.Children.Add(panel);

        Assert.NotNull(ServerClipOf(s, panel));
    }

    [Fact]
    public void A_Square_Border_Should_Keep_A_Null_Backdrop_Clip()
    {
        using var s = new CompositorCanvas();
        var panel = Frosted();
        panel.CornerRadius = default;
        s.Canvas.Children.Add(panel);

        Assert.Null(ServerClipOf(s, panel));
    }

    [Fact]
    public void An_Explicit_Backdrop_Clip_Should_Win_Over_The_Borders_Shape()
    {
        using var s = new CompositorCanvas();
        var panel = Frosted();
        var ellipse = new EllipseGeometry(new Rect(0, 0, 40, 40));
        panel.BackdropClip = ellipse;
        s.Canvas.Children.Add(panel);

        Assert.Same(ellipse.PlatformImpl, ServerClipOf(s, panel));
    }

    [Fact]
    public void A_Plain_Visual_Should_Keep_A_Square_Backdrop()
    {
        using var s = new CompositorCanvas();
        var panel = new Panel
        {
            Width = 40, Height = 40,
            Background = new ImmutableSolidColorBrush(Colors.White, 0.3),
            BackdropEffect = new ImmutableBlurEffect(4)
        };
        s.Canvas.Children.Add(panel);

        Assert.Null(ServerClipOf(s, panel));
    }

    [Fact]
    public void Changing_The_Borders_Corner_Radius_Should_Rebuild_The_Backdrop_Clip()
    {
        using var s = new CompositorCanvas();
        var panel = Frosted();
        s.Canvas.Children.Add(panel);
        var before = ServerClipOf(s, panel);
        Assert.NotNull(before);

        panel.CornerRadius = new CornerRadius(8);

        var after = ServerClipOf(s, panel);
        Assert.NotNull(after);
        Assert.NotSame(before, after);
    }
}
