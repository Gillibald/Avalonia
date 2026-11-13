using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// With <see cref="BackdropEffectBoundsSource.Subtree"/> the backdrop fills
/// everything its subtree paints rather than the visual's own box, so content
/// beneath an overflowing child, or beneath the children of a container with no
/// size of its own, feeds the filter and has to be tracked.
/// </summary>
public class BackdropBoundsSourceTests : CompositorTestsBase
{
    private static Rect? DirtyUnion(CompositorTestServices s)
    {
        s.RunJobs();
        Rect? union = null;
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;
        s.Events.Rects.Clear();
        return union;
    }

    private static Border Square(double left, double top, double size) =>
        new()
        {
            Background = Brushes.Red, Width = size, Height = size,
            [Canvas.LeftProperty] = left, [Canvas.TopProperty] = top
        };

    private static Border Panel(BackdropEffectBoundsSource source) =>
        new()
        {
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            Width = 40,
            Height = 40,
            BackdropEffect = new ImmutableBlurEffect(4),
            BackdropEffectBoundsSource = source,
            [Canvas.LeftProperty] = 50.0,
            [Canvas.TopProperty] = 50.0,
            // Overhangs the panel's right edge by 20.
            Child = new Border
            {
                Background = Brushes.Cyan, Width = 40, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = new TranslateTransform(20, 0)
            }
        };

    [Fact]
    public void Bounds_Source_Defaults_To_Layout()
    {
        Assert.Equal(BackdropEffectBoundsSource.Layout, new Border().BackdropEffectBoundsSource);
    }

    [Theory]
    [InlineData(BackdropEffectBoundsSource.Layout, false)]
    [InlineData(BackdropEffectBoundsSource.Subtree, true)]
    public void Change_Beneath_An_Overflowing_Child_Feeds_Only_A_Subtree_Backdrop(
        BackdropEffectBoundsSource source, bool feeds)
    {
        using var s = new CompositorCanvas();
        // Beneath the overhang, clear of the panel's own box and its blur reach.
        var behind = Square(100, 55, 6);
        s.Canvas.Children.Add(behind);
        s.Canvas.Children.Add(Panel(source));
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        // The subtree region spans the panel and its overhang, (50,50)-(110,90).
        var region = new Rect(50, 50, 60, 40);
        Assert.Equal(feeds, union is { } u && u.Contains(region));
    }

    [Fact]
    public void Subtree_Backdrop_On_A_Size_Less_Container_Tracks_Content_Beneath_Its_Children()
    {
        using var s = new CompositorCanvas();
        var behind = Square(62, 62, 6);
        s.Canvas.Children.Add(behind);

        var container = new Canvas
        {
            BackdropEffect = new ImmutableBlurEffect(4),
            BackdropEffectBoundsSource = BackdropEffectBoundsSource.Subtree,
            [Canvas.LeftProperty] = 50.0,
            [Canvas.TopProperty] = 50.0
        };
        container.Children.Add(Square(0, 0, 30));
        s.Canvas.Children.Add(container);
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        Assert.True(union is { } u && u.Contains(new Rect(50, 50, 30, 30)), $"Invalidated {union}");
    }

    [Fact]
    public void Subtree_Backdrop_Repaints_Its_Old_And_New_Extent_When_A_Child_Moves()
    {
        using var s = new CompositorCanvas();
        var panel = Panel(BackdropEffectBoundsSource.Subtree);
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        ((TranslateTransform)((Border)panel.Child!).RenderTransform!).X = 60;
        var union = DirtyUnion(s);

        // Before: (50,50)-(110,90). After: (50,50)-(150,90).
        Assert.True(union is { } u && u.Contains(new Rect(50, 50, 100, 40)), $"Invalidated {union}");
    }
}
