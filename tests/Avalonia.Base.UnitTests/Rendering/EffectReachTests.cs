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
/// Output padding says how far an effect's output extends past its input. It
/// is not how far a changed input pixel moves the output, nor how far an
/// output pixel reads: erosion adds no padding yet spreads a change over its
/// radius, and an offset reads from the side opposite the one it pads. A
/// partial redraw sized by padding leaves stale pixels inside what the filter
/// reads, and re-filtering them smears.
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

    private static Border Square(double left, double top, double size) =>
        new()
        {
            Background = Brushes.Red, Width = size, Height = size,
            [Canvas.LeftProperty] = left, [Canvas.TopProperty] = top
        };

    private static Border Glass(IEffect effect) =>
        new()
        {
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            Width = 40,
            Height = 40,
            BackdropEffect = effect,
            [Canvas.LeftProperty] = 50.0,
            [Canvas.TopProperty] = 50.0
        };

    private static BackdropLayerCache ValidOutputCache(CompositorTestServices s, Visual glass)
    {
        s.RunJobs();
        var cache = ((ServerCompositionVisual)ElementComposition.GetElementVisual(glass)!.Server!).BackdropCache!;
        cache.FixedPayload = BackdropCachePayload.Output;
        s.RunJobs();
        cache.IsValid = true;
        cache.RefreshRequested = false;
        cache.RefreshRects.Clear();
        s.Events.Rects.Clear();
        return cache;
    }

    [Fact]
    public void Read_Reach_Mirrors_How_Far_A_Change_Spreads()
    {
        Assert.Equal(default, Erode.GetEffectOutputPadding());
        Assert.Equal(new Thickness(3), Erode.GetEffectInfluence());
        Assert.Equal(new Thickness(3), Erode.GetEffectReadReach());

        Assert.Equal(new Thickness(0, 0, 5, 0), ShiftRight.GetEffectInfluence());
        Assert.Equal(new Thickness(5, 0, 0, 0), ShiftRight.GetEffectReadReach());

        var blur = new ImmutableBlurEffect(4);
        Assert.Equal(blur.GetEffectOutputPadding(), blur.GetEffectInfluence());
        Assert.Equal(blur.GetEffectOutputPadding(), blur.GetEffectReadReach());
    }

    [Fact]
    public void Partial_Refresh_Of_An_Eroding_Backdrop_Repaints_What_The_Refilter_Reads()
    {
        using var s = new CompositorCanvas();
        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var glass = Glass(Erode);
        s.Canvas.Children.Add(glass);
        var cache = ValidOutputCache(s, glass);

        // D = (60,60,10,10) changes the output within O = D + 3; re-filtering
        // O reads O + 3, all of which has to be painted fresh.
        behind.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        Assert.True(union is { } u && u.Contains(new Rect(54, 54, 22, 22)), $"Invalidated {union}");
        Assert.Equal(new[] { new Rect(7, 7, 16, 16) }, cache.RefreshRects);
    }

    [Fact]
    public void Partial_Refresh_Of_An_Offset_Backdrop_Repaints_The_Side_It_Reads_From()
    {
        using var s = new CompositorCanvas();
        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var glass = Glass(ShiftRight);
        s.Canvas.Children.Add(glass);
        ValidOutputCache(s, glass);

        // Shifted right by 5, D changes the output within (60,60)-(75,70),
        // and those output pixels read input from 5 further left.
        behind.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        Assert.True(union is { } u && u.Contains(new Rect(55, 60, 20, 10)), $"Invalidated {union}");
    }

    [Fact]
    public void Change_Left_Of_An_Offset_Backdrop_Within_Its_Read_Reach_Feeds_It()
    {
        using var s = new CompositorCanvas();
        // Outside the box, but the glass's left edge shows what is 5 to its left.
        var behind = Square(46, 60, 3);
        s.Canvas.Children.Add(behind);
        var glass = Glass(ShiftRight);
        glass.BackdropEffectCache = new VolatileBackdropEffectCache();
        s.Canvas.Children.Add(glass);
        s.RunJobs();
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;
        var union = DirtyUnion(s);

        Assert.True(union is { } u && u.Contains(new Rect(50, 50, 40, 40)), $"Invalidated {union}");
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
