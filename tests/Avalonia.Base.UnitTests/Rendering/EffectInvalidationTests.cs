using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// An effect whose output at a point depends only on input near that point (a
/// drop shadow, a blur) does not need its whole subtree repainted when one
/// child changes: the changed content plus the effect's reach bounds
/// everything the output can differ in. Effects that replicate input globally
/// (a tile) and the visual's own changes must keep the whole-bounds behavior.
/// </summary>
public class EffectInvalidationTests : CompositorTestsBase
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

    // The shadowed panel and the small child sitting in its bottom-right
    // corner. The shadow's padding is asymmetric (offset 6,6 plus blur 8
    // gives 3 left/top and 15 right/bottom), so the child's reach extends
    // mostly rightward and downward, leaving the panel's left side clearly
    // beyond any input the child can influence.
    private static readonly Rect PanelRect = new(40, 40, 120, 80);
    private static readonly Rect ChildRect = new(150, 110, 10, 10);
    private static readonly ImmutableDropShadowEffect Shadow = new(6, 6, 8, Colors.Black, 1);

    private static (Border Panel, Border Child) BuildPanel(IEffect? effect)
    {
        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var panel = new Border
        {
            Background = Brushes.LightGray,
            Width = PanelRect.Width,
            Height = PanelRect.Height,
            Effect = effect,
            Child = child,
            [Canvas.LeftProperty] = PanelRect.X,
            [Canvas.TopProperty] = PanelRect.Y
        };
        return (panel, child);
    }

    [Fact]
    public void Local_Effect_Child_Change_Should_Invalidate_Only_The_Childs_Reach()
    {
        using var s = new CompositorCanvas();

        var (panel, child) = BuildPanel(Shadow);
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        // A paint-only child change. The shadow's output can differ only
        // within the child's rect plus the effect's reach; the panel's left
        // side is input the child cannot influence and must not repaint.
        child.Background = Brushes.Blue;

        AssertCovers(s, ChildRect.Inflate(Shadow.GetEffectOutputPadding()));
        AssertDoesNotCover(s, new Rect(45, 45, 60, 50));
    }

    [Fact]
    public void Tile_Effect_Child_Change_Should_Keep_Whole_Bounds()
    {
        using var s = new CompositorCanvas();

        // A tile replicates a source region across the whole destination, so
        // any input change can move output anywhere: the localization must
        // reject it and keep repainting the panel's whole bounds.
        var (panel, child) = BuildPanel(
            new ImmutableTileEffect(new Rect(0, 0, 20, 20), new Rect(0, 0, 120, 80), null));
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        child.Background = Brushes.Blue;

        AssertCovers(s, PanelRect);
    }

    [Fact]
    public void Self_Dirty_Effect_Panel_Should_Keep_Whole_Bounds()
    {
        using var s = new CompositorCanvas();

        var (panel, _) = BuildPanel(Shadow);
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.Events.Rects.Clear();

        // The panel's own content changed: every filtered pixel may differ,
        // so the whole padded bounds repaint.
        panel.Background = Brushes.Blue;

        AssertCovers(s, PanelRect.Inflate(Shadow.GetEffectOutputPadding()));
    }
}
