using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A filter whose output pixel depends only on the input pixel beneath it (a
/// colour matrix, say) cannot smear: re-filtering a redrawn slice reads only
/// what that slice repainted. Such a backdrop needs neither a retained payload,
/// since re-running it costs about as much as drawing a cached copy, nor any
/// widening of the dirty region beyond the change itself.
/// </summary>
public class BackdropPointwiseTests : CompositorTestsBase
{
    private static readonly double[] Grayscale =
    {
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0.2126, 0.7152, 0.0722, 0, 0,
        0, 0, 0, 1, 0
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

    private static Border Square(double left, double top, double size) =>
        new()
        {
            Background = Brushes.Red, Width = size, Height = size,
            [Canvas.LeftProperty] = left, [Canvas.TopProperty] = top
        };

    private static BackdropLayerCache ServerCacheOf(CompositorTestServices s, Visual visual)
    {
        s.RunJobs();
        var composition = ElementComposition.GetElementVisual(visual)!;
        return ((ServerCompositionVisual)composition.Server!).BackdropCache!;
    }

    [Fact]
    public void Pointwise_Backdrop_Retains_Nothing_Under_The_Default_Hint()
    {
        using var s = new CompositorCanvas();
        var panel = Glass(new ImmutableColorMatrixEffect(Grayscale));
        s.Canvas.Children.Add(panel);

        var cache = ServerCacheOf(s, panel);
        s.RunJobs();

        Assert.Equal(BackdropCachePayload.None, cache.Payload);
    }

    [Fact]
    public void Pointwise_Backdrop_With_The_Retained_Hint_Keeps_A_Payload()
    {
        using var s = new CompositorCanvas();
        var panel = Glass(new ImmutableColorMatrixEffect(Grayscale));
        panel.BackdropEffectCache = new RetainedBackdropEffectCache();
        s.Canvas.Children.Add(panel);

        var cache = ServerCacheOf(s, panel);
        s.RunJobs();

        Assert.Equal(BackdropCachePayload.Output, cache.Payload);
    }

    [Fact]
    public void Pointwise_Backdrop_Repaints_Only_The_Change_Beneath_It()
    {
        using var s = new CompositorCanvas();
        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Glass(new ImmutableColorMatrixEffect(Grayscale));
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        s.AssertRects(new Rect(60, 60, 10, 10));
    }

    [Fact]
    public void Blurred_Backdrop_Still_Repaints_Its_Whole_Area_When_It_Retains_Nothing()
    {
        using var s = new CompositorCanvas();
        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Glass(new ImmutableBlurEffect(4));
        panel.BackdropEffectCache = new VolatileBackdropEffectCache();
        s.Canvas.Children.Add(panel);
        s.RunJobs();
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;
        s.RunJobs();

        var union = default(Rect?);
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;
        Assert.True(union is { } covered && covered.Contains(new Rect(45, 45, 50, 50)), $"Invalidated {union}");
    }
}
