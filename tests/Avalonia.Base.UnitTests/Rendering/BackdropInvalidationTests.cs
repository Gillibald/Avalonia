using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

        s.AssertRects(new Rect(50, 50, 40, 40));
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
        s.AssertRects(new Rect(50, 50, 40, 40).Inflate(padding));
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

        s.AssertRects(new Rect(200, 200, 10, 10));
    }
}
