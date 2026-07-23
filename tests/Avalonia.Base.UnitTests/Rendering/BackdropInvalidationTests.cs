using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A backdrop filters the surface it is drawn over, so everything beneath it has
/// to be repainted before it is read. Invalidating only the part that changed
/// leaves the rest of the surface holding the previous frame - which already
/// contains the backdrop's own output - and re-filtering that smears it outward
/// a little more every frame.
/// </summary>
public class BackdropInvalidationTests : CompositorTestsBase
{
    /// <summary>
    /// What matters is the area the frame ends up repainting, not how many rects
    /// it was collected as, so these assert against the union.
    /// </summary>
    private static void AssertCovers(CompositorTestServices s, Rect expected)
    {
        s.RunJobs();

        Rect? union = null;
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;

        Assert.True(union is { } total && total.Contains(expected),
            $"Invalidated {(union?.ToString() ?? "nothing")}, which does not cover {expected}");
    }

    private static void AssertDoesNotCover(CompositorTestServices s, Rect forbidden)
    {
        s.RunJobs();

        foreach (var rect in s.Events.Rects)
        {
            Assert.False(rect.Intersects(forbidden),
                $"Invalidated {rect}, which reaches into {forbidden} for an unrelated change");
        }
    }

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

    [Fact]
    public void Backdrop_Should_Invalidate_Its_Whole_Area_When_Content_Behind_It_Changes()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);
        s.Canvas.Children.Add(Frosted(50, 50, 40, 40, blur: 0));
        s.RunJobs();
        s.Events.Rects.Clear();

        // A small change under the panel. Only its 10x10 rect is dirty by itself,
        // but the panel re-reads its whole 40x40 area, so all of it must repaint.
        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(50, 50, 40, 40));
    }

    [Fact]
    public void Backdrop_Should_Invalidate_The_Area_Its_Filter_Reads_Beyond_Its_Bounds()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);
        s.Canvas.Children.Add(Frosted(50, 50, 40, 40, blur: 4));
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        // A blur reads past the panel, so the invalidated area has to cover the
        // panel inflated by the filter's reach rather than just the panel.
        var padding = new ImmutableBlurEffect(4).GetEffectOutputPadding();
        AssertCovers(s, new Rect(50, 50, 40, 40).Inflate(padding));
    }

    [Fact]
    public void Backdrop_Should_Not_Inflate_The_Dirty_Region_When_Nothing_Touches_It()
    {
        using var s = new CompositorCanvas();

        s.Canvas.Children.Add(Frosted(50, 50, 40, 40, blur: 4));
        s.RunJobs();
        s.Events.Rects.Clear();

        // Well clear of the panel and its blur reach: the panel must not drag its
        // own area into the dirty region for unrelated changes.
        var far = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 200, [Canvas.TopProperty] = 200
        };
        s.Canvas.Children.Add(far);

        AssertCovers(s, new Rect(200, 200, 10, 10));
        AssertDoesNotCover(s, new Rect(50, 50, 40, 40));
    }

    /// <summary>
    /// The server-side cache slot of a backdrop visual. The tests drive the
    /// implementation's half of the <see cref="BackdropLayerCache"/> handshake
    /// by hand - the mock render interface never captures anything.
    /// </summary>
    private static BackdropLayerCache ServerCacheOf(CompositorTestServices s, Visual visual)
    {
        s.RunJobs();
        var composition = ElementComposition.GetElementVisual(visual)!;
        return ((ServerCompositionVisual)composition.Server!).BackdropCache!;
    }

    [Fact]
    public void Backdrop_Child_Change_Should_Not_Repaint_Beneath_When_Result_Is_Cached()
    {
        using var s = new CompositorCanvas();

        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        panel.Child = child;
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        // Simulate the backend having captured the filtered result last frame.
        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        s.Events.Rects.Clear();

        // The child paints inside the backdrop layer, above the sample point,
        // so it cannot change what the filter reads: the cached result stays
        // usable and nothing beneath the panel may be dragged into the region.
        child.Background = Brushes.Blue;

        AssertCovers(s, new Rect(80, 80, 10, 10));
        AssertDoesNotCover(s, new Rect(45, 45, 25, 50));
    }

    [Fact]
    public void Backdrop_Child_Change_Should_Repaint_Beneath_While_No_Cached_Result_Exists()
    {
        using var s = new CompositorCanvas();

        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        panel.Child = child;
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        // No usable cached result (a backend that never captures leaves the
        // slot invalid forever): the filter will re-sample the surface, so the
        // whole input area has to be freshly painted even for a change that sits
        // above the sample point.
        child.Background = Brushes.Blue;

        var padding = new ImmutableBlurEffect(4).GetEffectOutputPadding();
        AssertCovers(s, new Rect(50, 50, 40, 40).Inflate(padding));
    }

    [Fact]
    public void Backdrop_Should_Be_Invalidated_And_Granted_A_Refresh_When_Content_Behind_It_Changes()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;
        s.RunJobs();

        // The content the filter reads changed: the retained result is stale,
        // and because this frame's region now covers the whole input area it is
        // the safe moment for the backend to capture a fresh one.
        Assert.False(cache.IsValid);
        Assert.True(cache.RefreshRequested);
    }
}
