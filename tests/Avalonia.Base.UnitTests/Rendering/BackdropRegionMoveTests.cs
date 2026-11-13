using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A glass's backdrop fills its whole box, even where the glass itself paints
/// nothing. When the box moves, the repaint has to cover the box it left and
/// the box it moved to, not just what the glass's content paints.
/// </summary>
public class BackdropRegionMoveTests : CompositorTestsBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moving_Glass_Repaints_Its_Old_And_New_Box_Even_Where_It_Paints_Nothing(bool viaTransform)
    {
        using var s = new CompositorCanvas();
        var glass = new Border
        {
            Width = 40,
            Height = 40,
            BackdropEffect = new ImmutableBlurEffect(4),
            Child = new Border
            {
                Background = Brushes.Red, Width = 6, Height = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            },
            RenderTransform = new TranslateTransform(),
            [Canvas.LeftProperty] = 50.0,
            [Canvas.TopProperty] = 50.0
        };
        s.Canvas.Children.Add(glass);
        s.RunJobs();
        s.Events.Rects.Clear();

        if (viaTransform)
            ((TranslateTransform)glass.RenderTransform!).X = 30;
        else
            Canvas.SetLeft(glass, 80);
        s.RunJobs();

        Rect? union = null;
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;

        // Old box (50,50)-(90,90), new box (80,50)-(120,90).
        Assert.True(union is { } covered && covered.Contains(new Rect(50, 50, 70, 40)),
            $"Invalidated {union}");
    }
}
