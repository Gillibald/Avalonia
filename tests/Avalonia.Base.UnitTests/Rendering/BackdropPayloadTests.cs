using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A retained backdrop keeps either its filtered output or its sampled input.
/// The two differ in what a change beneath the glass forces the frame to
/// repaint: re-filtering a retained output reads input around the changed
/// output, which has to be painted fresh, while a retained input only needs
/// the changed input copied in and the changed output repainted. These drive
/// the backend half of the <see cref="BackdropLayerCache"/> handshake by hand -
/// the mock render interface never captures anything.
/// </summary>
public class BackdropPayloadTests : CompositorTestsBase
{
    private static Rect? DirtyUnion(CompositorTestServices s)
    {
        s.RunJobs();

        Rect? union = null;
        foreach (var rect in s.Events.Rects)
            union = union is { } u ? u.Union(rect) : rect;
        return union;
    }

    private static void AssertDoesNotCover(CompositorTestServices s, Rect forbidden)
    {
        s.RunJobs();

        foreach (var rect in s.Events.Rects)
        {
            Assert.False(rect.Intersects(forbidden),
                $"Invalidated {rect}, which reaches into {forbidden}");
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

    /// <summary>
    /// Plays the backend for one frame: a granted refresh is taken, leaving a
    /// valid retained payload and no pending grant.
    /// </summary>
    private static void ConsumeGrant(BackdropLayerCache cache)
    {
        if (cache.RefreshRequested)
            cache.IsValid = true;
        cache.RefreshRequested = false;
        cache.RefreshRects.Clear();
        cache.IngestRects.Clear();
    }

    private static BackdropLayerCache PinnedValidCache(CompositorTestServices s, Visual panel,
        BackdropCachePayload payload)
    {
        var cache = ServerCacheOf(s, panel);
        cache.FixedPayload = payload;
        cache.Payload = payload;
        cache.IsValid = true;
        cache.RefreshRequested = false;
        s.Events.Rects.Clear();
        return cache;
    }

    [Fact]
    public void Input_Payload_Small_Behind_Change_Repaints_Only_The_Changed_Output()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var cache = PinnedValidCache(s, panel, BackdropCachePayload.Input);

        // D = (60,60,10,10), reach 5: the output changes within
        // O = (D + 5) n box = (55,55,20,20). A retained input only needs D
        // copied in, so unlike a retained output nothing past O - the strip
        // re-filtering O would otherwise read - is repainted.
        behind.Background = Brushes.Blue;

        var union = DirtyUnion(s);
        Assert.True(union is { } u && u.Contains(new Rect(55, 55, 20, 20)), $"Invalidated {union}");
        AssertDoesNotCover(s, new Rect(50, 50, 4, 40));
        Assert.True(cache.IsValid);
        Assert.True(cache.RefreshRequested);
        Assert.Empty(cache.RefreshRects);
        Assert.Equal(new[] { new Rect(10, 10, 10, 10) }, cache.IngestRects);
    }

    [Fact]
    public void Input_Payload_Overlay_Above_Keeps_The_Cache_Without_Ingest()
    {
        using var s = new CompositorCanvas();

        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var overlay = Square(60, 60, 20);
        s.Canvas.Children.Add(overlay);
        var cache = PinnedValidCache(s, panel, BackdropCachePayload.Input);

        // Painted above the sample point: the retained input is unchanged and
        // the backend re-filters it under the overlay's dirty clip.
        overlay.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.False(cache.RefreshRequested);
        Assert.Empty(cache.IngestRects);
    }

    [Fact]
    public void No_Cache_Payload_Repaints_The_Whole_Area_On_A_Small_Behind_Change()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var cache = PinnedValidCache(s, panel, BackdropCachePayload.None);
        s.RunJobs();
        s.Events.Rects.Clear();

        behind.Background = Brushes.Blue;

        var union = DirtyUnion(s);
        Assert.True(union is { } u && u.Contains(new Rect(45, 45, 50, 50)), $"Invalidated {union}");
        Assert.False(cache.IsValid);
        Assert.False(cache.RefreshRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_Grant_Is_Rebuilt_When_A_Later_Pass_Revisits_The_Glass(bool input)
    {
        var payload = input ? BackdropCachePayload.Input : BackdropCachePayload.Output;
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var cache = PinnedValidCache(s, panel, payload);

        // The grant's own repaint makes the fixpoint loop run another pass,
        // which revisits the glass with the same dirt. Stacking the grant
        // again would duplicate it, and enough duplicates exceed the rect cap
        // and degrade the frame to a full refresh.
        behind.Background = Brushes.Blue;
        s.RunJobs();

        Assert.True(cache.IsValid);
        Assert.Single(payload == BackdropCachePayload.Output ? cache.RefreshRects : cache.IngestRects);
    }

    [Fact]
    public void Repeated_Small_Behind_Changes_Switch_To_The_Input_Payload()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 4);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 120, 80, blur: 20);
        s.Canvas.Children.Add(panel);
        var cache = ServerCacheOf(s, panel);
        Assert.Equal(BackdropCachePayload.Output, cache.Payload);

        // A caret-sized spot blinking behind a strongly blurred glass: a
        // retained output repaints a ring twice the reach wide each time,
        // while a retained input only copies the spot.
        for (var i = 0; i < 8; i++)
        {
            ConsumeGrant(cache);
            behind.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
            s.RunJobs();
        }

        Assert.Equal(BackdropCachePayload.Input, cache.Payload);
    }

    [Fact]
    public void Behind_Changes_Covering_The_Glass_Drop_The_Retained_Payload()
    {
        using var s = new CompositorCanvas();

        var video = Square(40, 40, 60);
        s.Canvas.Children.Add(video);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var cache = ServerCacheOf(s, panel);

        // Content behind the whole glass changing every frame: any retained
        // payload is rebuilt each frame, so keeping one only costs memory and
        // an extra write.
        for (var i = 0; i < 8; i++)
        {
            ConsumeGrant(cache);
            video.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
            s.RunJobs();
        }

        Assert.Equal(BackdropCachePayload.None, cache.Payload);
    }

    [Fact]
    public void Front_Only_Touches_Return_To_The_Output_Payload()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 4);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 120, 80, blur: 20);
        s.Canvas.Children.Add(panel);
        var overlay = Square(100, 70, 10);
        s.Canvas.Children.Add(overlay);
        var cache = ServerCacheOf(s, panel);

        for (var i = 0; i < 8; i++)
        {
            ConsumeGrant(cache);
            behind.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
            s.RunJobs();
        }

        Assert.Equal(BackdropCachePayload.Input, cache.Payload);

        // The content behind settles and only the overlay keeps changing: the
        // output payload serves those frames without running the filter.
        for (var i = 0; i < BackdropLayerCache.ReturnToOutputAfterFrames + 4; i++)
        {
            ConsumeGrant(cache);
            overlay.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
            s.RunJobs();
        }

        Assert.Equal(BackdropCachePayload.Output, cache.Payload);
    }

    [Fact]
    public void A_Fixed_Payload_Is_Never_Switched()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 4);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 120, 80, blur: 20);
        s.Canvas.Children.Add(panel);
        var cache = PinnedValidCache(s, panel, BackdropCachePayload.Output);

        for (var i = 0; i < 8; i++)
        {
            ConsumeGrant(cache);
            behind.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
            s.RunJobs();
        }

        Assert.Equal(BackdropCachePayload.Output, cache.Payload);
    }

    [Fact]
    public void Switching_Payload_Forces_A_Full_Refresh()
    {
        using var s = new CompositorCanvas();

        var behind = Square(60, 60, 10);
        s.Canvas.Children.Add(behind);
        var panel = Frosted(50, 50, 40, 40, blur: 4);
        s.Canvas.Children.Add(panel);
        var cache = PinnedValidCache(s, panel, BackdropCachePayload.Output);

        // The retained pixels belong to the old payload, so the first touch
        // after a switch recaptures the whole input area from a fresh paint.
        cache.FixedPayload = BackdropCachePayload.Input;
        behind.Background = Brushes.Blue;

        var union = DirtyUnion(s);
        Assert.True(union is { } u && u.Contains(new Rect(45, 45, 50, 50)), $"Invalidated {union}");
        Assert.Equal(BackdropCachePayload.Input, cache.Payload);
        Assert.False(cache.IsValid);
        Assert.True(cache.RefreshRequested);
        Assert.Empty(cache.IngestRects);
    }
}
