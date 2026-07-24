using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A backdrop inside a bitmap-cached subtree samples the HOST's surface (the
/// cache texture), so its invalidation and refresh grants must run against the
/// host's dirty space: damage inside the host classifies by paint position
/// there, and a grant certifies that the host's re-render clip covers the
/// filter's input.
/// </summary>
public class BackdropCacheHostTests : CompositorTestsBase
{
    private static Border Frosted(double left, double top, double width, double height, double blur) =>
        new()
        {
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            Width = width,
            Height = height,
            BackdropEffect = new ImmutableBlurEffect(blur),
            [Canvas.LeftProperty] = left,
            [Canvas.TopProperty] = top
        };

    private static BackdropLayerCache ServerCacheOf(CompositorTestServices s, Visual visual)
    {
        s.RunJobs();
        var composition = ElementComposition.GetElementVisual(visual)!;
        return ((ServerCompositionVisual)composition.Server!).BackdropCache!;
    }

    private static Canvas CachedHost() => new()
    {
        Width = 200, Height = 200,
        CacheMode = new BitmapCache()
    };

    [Fact]
    public void Overlay_Above_The_Glass_Inside_A_Cached_Subtree_Should_Not_Invalidate_The_Cached_Result()
    {
        using var s = new CompositorCanvas();

        var host = CachedHost();
        s.Canvas.Children.Add(host);

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        host.Children.Add(panel);

        // A later sibling inside the same cached subtree, above the panel.
        var overlay = new Border
        {
            Background = Brushes.Red, Width = 20, Height = 20,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        host.Children.Add(overlay);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // The overlay paints above the point where the panel's filter samples
        // the host surface, so its change cannot alter what the filter reads.
        overlay.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Behind_Change_Inside_The_Host_Should_Invalidate_And_Grant()
    {
        using var s = new CompositorCanvas();

        var host = CachedHost();
        s.Canvas.Children.Add(host);

        // Painted before (beneath) the panel, inside the same host.
        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        host.Children.Add(behind);
        host.Children.Add(Frosted(50, 50, 40, 40, blur: 4));
        s.RunJobs();

        var panel = (Border)host.Children[1];
        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // Content the filter reads changed: the retained result is stale and
        // this frame is the safe moment to capture a fresh one from the host.
        behind.Background = Brushes.Blue;
        s.RunJobs();

        Assert.False(cache.IsValid);
        Assert.True(cache.RefreshRequested);
    }
}
