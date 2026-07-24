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
    public void Backdrop_Region_Should_Stop_At_The_Controls_Bounds_Under_Overflowing_Content()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);

        var panel = Frosted(50, 50, 40, 40, blur: 0);
        // A child shifted past the panel's right edge: it paints outside the
        // control's box (nothing clips it), but the backdrop region is the box
        // itself, so the overhang must not widen what gets repainted.
        panel.Child = new Border
        {
            Background = Brushes.Cyan, Width = 40, Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(20, 0)
        };
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(50, 50, 40, 40));
        AssertDoesNotCover(s, new Rect(95, 50, 15, 40));
    }

    [Fact]
    public void Backdrop_With_BoxShadow_Child_Change_Should_Repaint_Only_The_Child()
    {
        using var s = new CompositorCanvas();

        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var panel = Frosted(50, 50, 40, 40, blur: 0);
        // A geometry box shadow is the border's own drawing and does not depend
        // on the subtree, unlike a drop shadow Effect whose whole-bounds
        // re-render makes any child change repaint the full panel. This is the
        // composition the demo uses: glass plus shadow plus cheap child
        // changes.
        panel.BoxShadow = BoxShadows.Parse("0 4 8 0 #66000000");
        panel.Child = child;
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        child.Background = Brushes.Blue;

        AssertCovers(s, new Rect(80, 80, 10, 10));
        AssertDoesNotCover(s, new Rect(50, 50, 25, 40));
        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Backdrop_With_DropShadow_Should_Not_Expand_Into_The_Shadow_Zone()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);

        var panel = Frosted(50, 50, 40, 40, blur: 0);
        // The shadow inflates the visual's subtree bounds to (49,49)-(111,111),
        // but the backdrop still samples only the panel's own 40x40 area.
        panel.Effect = new ImmutableDropShadowEffect(10, 10, 10, Colors.Black, 1);
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        // The repaint has to cover what the filter reads - the panel's area -
        // and must not be dragged out into the shadow zone, which contains only
        // content painted above the sample point.
        AssertCovers(s, new Rect(50, 50, 40, 40));
        AssertDoesNotCover(s, new Rect(95, 50, 16, 40));
    }

    [Fact]
    public void Small_Behind_Change_Should_Refresh_Only_Its_Neighborhood()
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
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // D = (60,60,10,10) with reach 5 (blur 4): the filter's output can
        // differ only within O = (D + 5) clamped to the box, and re-filtering
        // O needs input O + 5 = (50,50,30,30) freshly painted. The panel's far
        // side is neither, and the retained result stays usable for a partial
        // refresh of O.
        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(50, 50, 30, 30));
        AssertDoesNotCover(s, new Rect(84, 52, 8, 36));
        Assert.True(cache.IsValid);
        Assert.True(cache.RefreshRequested);
    }

    [Fact]
    public void Overlay_Above_The_Glass_Should_Not_Invalidate_The_Cached_Result()
    {
        using var s = new CompositorCanvas();

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);

        // A later sibling drawn above the panel, overlapping it.
        var overlay = new Border
        {
            Background = Brushes.Red, Width = 20, Height = 20,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(overlay);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // The overlay paints above the point where the panel's filter samples
        // the surface, so its change cannot alter what the filter reads: the
        // retained result stays usable and no refresh is needed.
        overlay.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Backdrop_With_DropShadow_Child_Change_Should_Keep_The_Cached_Result()
    {
        using var s = new CompositorCanvas();

        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        panel.Effect = new ImmutableDropShadowEffect(6, 6, 8, Colors.Black, 1);
        panel.Child = child;
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        // A capture consumes the refresh grant and marks the slot valid; the
        // mock backend does neither, so the test plays that half by hand.
        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // The shadow depends on the panel's content, so a child change
        // re-renders the panel's whole bounds - but everything re-rendered
        // paints above the backdrop's sample point, so the retained filtered
        // result stays usable and no refresh is needed.
        child.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Backdrop_Registers_When_The_Effect_Is_Set_Before_Attaching()
    {
        using var s = new CompositorCanvas();

        // The effect exists before the visual ever sees a root; registration
        // must converge regardless of the order.
        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        var detachedParent = new Canvas { Width = 200, Height = 200 };
        detachedParent.Children.Add(behind);
        detachedParent.Children.Add(panel);

        s.Canvas.Children.Add(detachedParent);
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(50, 50, 40, 40));
    }

    [Fact]
    public void Detaching_The_Glass_Stops_Expanding_The_Region()
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

        s.Canvas.Children.Remove(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        // With the glass gone its area must not be dragged into the region
        // for a change that only touches the content that was beneath it.
        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(60, 60, 10, 10));
        AssertDoesNotCover(s, new Rect(80, 80, 15, 15));
    }

    [Fact]
    public void Clearing_The_Effect_Stops_Expanding_The_Region()
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

        panel.BackdropEffect = null;
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(60, 60, 10, 10));
        AssertDoesNotCover(s, new Rect(80, 80, 15, 15));
    }

    [Fact]
    public void Dirty_Ancestor_Should_Invalidate_The_Cached_Result()
    {
        using var s = new CompositorCanvas();

        var wrapper = new Canvas { Width = 200, Height = 200, Background = Brushes.Beige };
        s.Canvas.Children.Add(wrapper);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        wrapper.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // The ancestor repaints its own content beneath the glass; the walk
        // cannot see its rect at the glass's position (it is only emitted at
        // the ancestor's PostSubgraph), so the covering blanket must apply.
        wrapper.Background = Brushes.Bisque;
        s.RunJobs();

        Assert.False(cache.IsValid);
    }

    [Fact]
    public void Removing_A_Sibling_Beneath_Should_Invalidate_The_Cached_Result()
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

        // The vanished content painted beneath the glass; its old bounds only
        // surface as the parent's extra rect after the glass's position, so
        // the pending-extra blanket must classify it as changed beneath.
        s.Canvas.Children.Remove(behind);
        s.RunJobs();

        Assert.False(cache.IsValid);
    }

    [Fact]
    public void Deep_Glass_On_A_Clean_Spine_Keeps_The_Cached_Result_For_An_Overlay_Above()
    {
        using var s = new CompositorCanvas();

        // The glass sits two clean levels down; nothing on its spine is dirty
        // when the overlay changes, so only the damage-directed descent can
        // classify it exactly.
        var outer = new Canvas { Width = 200, Height = 200 };
        var inner = new Canvas { Width = 200, Height = 200 };
        outer.Children.Add(inner);
        s.Canvas.Children.Add(outer);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        inner.Children.Add(panel);

        var overlay = new Border
        {
            Background = Brushes.Red, Width = 20, Height = 20,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(overlay);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();

        overlay.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Resizing_The_Glass_Invalidates_And_Covers_Old_And_New_Bounds()
    {
        using var s = new CompositorCanvas();

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        s.Events.Rects.Clear();

        // The sample region itself moves: the retained result is stale and
        // both the vacated and the newly covered area must repaint.
        panel.Width = 70;
        s.RunJobs();

        Assert.False(cache.IsValid);
        AssertCovers(s, new Rect(50, 50, 40, 40));
        AssertCovers(s, new Rect(50, 50, 70, 40));
    }

    [Fact]
    public void Uncached_Glass_Expansion_Reaches_A_Later_Valid_Glass()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 55, [Canvas.TopProperty] = 55
        };
        s.Canvas.Children.Add(behind);
        var lower = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(lower);
        var upper = Frosted(60, 60, 40, 40, blur: 4);
        s.Canvas.Children.Add(upper);
        s.RunJobs();

        var lowerCache = ServerCacheOf(s, lower);
        var upperCache = ServerCacheOf(s, upper);
        lowerCache.IsValid = false;
        upperCache.IsValid = true;
        upperCache.RefreshRequested = false;
        s.Events.Rects.Clear();

        // The lower glass has no retained result, so its whole area repaints -
        // and that repaint includes its own changed output, which the upper
        // glass painted over it reads.
        behind.Background = Brushes.Blue;
        s.RunJobs();

        Assert.False(upperCache.IsValid);
        Assert.True(upperCache.RefreshRequested);
    }

    [Fact]
    public void Chained_Uncached_Glasses_Converge_To_Both_Areas()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 55, [Canvas.TopProperty] = 55
        };
        s.Canvas.Children.Add(behind);
        var lower = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(lower);
        var upper = Frosted(80, 80, 40, 40, blur: 4);
        s.Canvas.Children.Add(upper);
        s.RunJobs();
        s.Events.Rects.Clear();

        // Neither glass has a retained result. The change touches only the
        // lower one, whose expansion reaches into the upper one's area; the
        // convergence loop must settle with both whole areas covered.
        behind.Background = Brushes.Blue;

        var padding = new ImmutableBlurEffect(4).GetEffectOutputPadding();
        AssertCovers(s, new Rect(50, 50, 40, 40).Inflate(padding));
        AssertCovers(s, new Rect(80, 80, 40, 40).Inflate(padding));
    }

    [Fact]
    public void Uncached_Glass_Above_Keeps_A_Valid_Glass_Below()
    {
        using var s = new CompositorCanvas();

        var lower = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(lower);
        var upper = Frosted(80, 60, 40, 40, blur: 4);
        s.Canvas.Children.Add(upper);
        var overlay = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 100, [Canvas.TopProperty] = 70
        };
        s.Canvas.Children.Add(overlay);
        s.RunJobs();

        var lowerCache = ServerCacheOf(s, lower);
        var upperCache = ServerCacheOf(s, upper);
        lowerCache.IsValid = true;
        lowerCache.RefreshRequested = false;
        upperCache.IsValid = false;
        s.Events.Rects.Clear();

        // The overlay touches only the upper glass, whose whole-area repaint
        // overlaps the lower one - but the upper glass paints above the lower
        // one's sample point, so the lower retained result stays usable.
        overlay.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(lowerCache.IsValid);
        Assert.False(lowerCache.RefreshRequested);
    }

    [Fact]
    public void Zero_Size_Glass_Adds_No_Region()
    {
        using var s = new CompositorCanvas();

        var behind = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            [Canvas.LeftProperty] = 60, [Canvas.TopProperty] = 60
        };
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 0, 0, blur: 4);
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        // A zero-size glass samples nothing: no crash, no expansion.
        behind.Background = Brushes.Blue;

        AssertCovers(s, new Rect(60, 60, 10, 10));
        AssertDoesNotCover(s, new Rect(80, 80, 15, 15));
    }

    [Fact]
    public void Backdrop_Should_Be_Invalidated_And_Granted_A_Refresh_When_Content_Behind_It_Changes()
    {
        using var s = new CompositorCanvas();

        // Large enough that a partial refresh stops paying off: most of the
        // filter's input changed, so the whole retained result is stale.
        var behind = new Border
        {
            Background = Brushes.Red, Width = 30, Height = 30,
            [Canvas.LeftProperty] = 55, [Canvas.TopProperty] = 55
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

        // The content the filter reads changed almost everywhere: the retained
        // result is stale, and because this frame's region now covers the whole
        // input area it is the safe moment for the backend to capture a fresh
        // one.
        Assert.False(cache.IsValid);
        Assert.True(cache.RefreshRequested);
    }
}
