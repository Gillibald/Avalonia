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
/// Composition brushes animate on the render thread; their ticks surface as
/// ordinary content dirt on the visuals showing them, so the backdrop's
/// positional classification applies unmodified: a tick beneath the glass
/// refreshes the cached filtered result, a tick on the glass keeps it.
/// </summary>
public class BackdropCompositionBrushTests : CompositorTestsBase
{
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

    private static BackdropLayerCache ServerCacheOf(CompositorTestServices s, Visual visual)
    {
        s.RunJobs();
        var composition = ElementComposition.GetElementVisual(visual)!;
        return ((ServerCompositionVisual)composition.Server!).BackdropCache!;
    }

    /// <summary>
    /// Hosts a recording drawing <paramref name="rect"/> with <paramref name="brush"/>
    /// as a child visual of a new control, which the caller places in the tree.
    /// </summary>
    private static Control HostRecording(
        CompositorTestServices s, CompositionSolidColorBrush brush, Rect rect)
    {
        var recording = DrawingRecording.Create(s.Compositor, ctx =>
            ctx.DrawRectangle(brush, null, rect));
        var visual = s.Compositor.CreateRecordingVisual();
        visual.Recording = recording;

        var host = new Control { Width = rect.Width, Height = rect.Height };
        host.AttachedToVisualTree += (_, _) =>
            ElementComposition.SetElementChildVisual(host, visual);
        return host;
    }

    /// <summary>
    /// Simulates one animation tick on the render thread: the evaluator writes
    /// the field and raises NotifyAnimatedValueChanged.
    /// </summary>
    private static void Tick(CompositionSolidColorBrush brush, Color color)
    {
        var server = (ServerCompositionSolidColorBrush)brush.Server;
        server.Color = color;
        server.NotifyAnimatedValueChanged(ServerCompositionSolidColorBrush.s_IdOfColorProperty);
    }

    [Fact]
    public void Composition_Brush_Tick_Beneath_The_Glass_Should_Refresh_The_Backdrop()
    {
        using var s = new CompositorCanvas();

        var brush = s.Compositor.CreateSolidColorBrush(Colors.Red);
        var behind = HostRecording(s, brush, new Rect(0, 0, 30, 30));
        behind[Canvas.LeftProperty] = 55d;
        behind[Canvas.TopProperty] = 55d;
        s.Canvas.Children.Add(behind);

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        cache.RefreshRects.Clear();
        s.Events.Rects.Clear();

        // The brush ticks on the render thread; its dirt classifies beneath the
        // sample point, so the filter input changed: the cached result must be
        // dropped, a refresh granted, and the whole input area repainted.
        Tick(brush, Colors.Blue);

        var padding = new ImmutableBlurEffect(4).GetEffectOutputPadding();
        AssertCovers(s, new Rect(50, 50, 40, 40).Inflate(padding));
        Assert.False(cache.IsValid);
        Assert.True(cache.RefreshRequested);
    }

    [Fact]
    public void Composition_Brush_Tick_On_The_Glass_Should_Keep_The_Cached_Backdrop()
    {
        using var s = new CompositorCanvas();

        var brush = s.Compositor.CreateSolidColorBrush(Colors.Red);
        var badge = HostRecording(s, brush, new Rect(0, 0, 10, 10));
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Bottom;

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        panel.Child = badge;
        s.Canvas.Children.Add(panel);
        s.RunJobs();

        var cache = ServerCacheOf(s, panel);
        cache.IsValid = true;
        cache.RefreshRequested = false;
        cache.RefreshRects.Clear();
        s.Events.Rects.Clear();

        // The badge paints inside the backdrop layer, above the sample point:
        // its render-thread ticks cannot change what the filter reads, so the
        // cached result stays valid and nothing beneath the panel repaints.
        Tick(brush, Colors.Blue);

        AssertCovers(s, new Rect(80, 80, 10, 10));
        AssertDoesNotCover(s, new Rect(45, 45, 25, 50));
        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Fact]
    public void Composition_Brush_As_An_Opacity_Mask_Should_Invalidate_On_Tick()
    {
        using var s = new CompositorCanvas();

        var mask = s.Compositor.CreateSolidColorBrush(Color.FromArgb(128, 0, 0, 0));
        var recording = DrawingRecording.Create(s.Compositor, ctx =>
        {
            using (ctx.PushOpacityMask(mask, new Rect(0, 0, 30, 30)))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 30, 30));
        });
        var visual = s.Compositor.CreateRecordingVisual();
        visual.Recording = recording;

        var host = new Control { Width = 30, Height = 30 };
        host[Canvas.LeftProperty] = 20d;
        host[Canvas.TopProperty] = 20d;
        s.Canvas.Children.Add(host);
        s.RunJobs();
        ElementComposition.SetElementChildVisual(host, visual);
        s.RunJobs();
        s.Events.Rects.Clear();

        // The mask is captured like any brush, so its render-thread ticks
        // repaint the content it masks.
        Tick(mask, Color.FromArgb(64, 0, 0, 0));

        AssertCovers(s, new Rect(20, 20, 30, 30));
        recording.Dispose();
    }
}
