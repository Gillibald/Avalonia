using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// Output padding says how far an effect's output extends past its input. It
/// is not how far a changed input pixel moves the output: erosion adds no
/// padding yet spreads a change over its radius, and an offset moves it only
/// toward the side it pads. A partial redraw sized by padding leaves stale
/// output pixels next to the change.
/// </summary>
public class EffectReachTests : CompositorTestsBase
{
    private static readonly IEffect Erode = new ImmutableMorphologyEffect(3, 3, dilate: false, input: null);
    private static readonly IEffect ShiftRight = new ImmutableOffsetEffect(5, 0);

    private static Rect? DirtyUnion(CompositorTestServices s)
    {
        s.RunJobs();
        Rect? union = null;
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;
        s.Events.Rects.Clear();
        return union;
    }

    [Fact]
    public void Influence_Is_How_Far_A_Change_Spreads()
    {
        Assert.Equal(default, Erode.GetEffectOutputPadding());
        Assert.Equal(new Thickness(3), Erode.GetEffectInfluence());

        Assert.Equal(new Thickness(0, 0, 5, 0), ShiftRight.GetEffectInfluence());

        var blur = new ImmutableBlurEffect(4);
        Assert.Equal(blur.GetEffectOutputPadding(), blur.GetEffectInfluence());
    }

    [Fact]
    public void Child_Change_Under_An_Eroding_Effect_Repaints_The_Spread_Of_The_Change()
    {
        using var s = new CompositorCanvas();
        var child = new Border
        {
            Background = Brushes.Red, Width = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        s.Canvas.Children.Add(new Border
        {
            Background = Brushes.LightGray, Width = 60, Height = 60,
            Effect = Erode,
            Child = child,
            [Canvas.LeftProperty] = 40.0,
            [Canvas.TopProperty] = 40.0
        });
        s.RunJobs();
        s.Events.Rects.Clear();

        // The child sits at (65,65,10,10); erosion moves the output within 3
        // of any changed input pixel.
        child.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        Assert.True(union is { } u && u.Contains(new Rect(62, 62, 16, 16)), $"Invalidated {union}");
    }
}
