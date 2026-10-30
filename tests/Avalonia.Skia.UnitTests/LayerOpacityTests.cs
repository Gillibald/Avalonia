using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests;

/// <summary>
/// A layer is composited as a whole, so it takes the opacity in effect when it
/// is pushed - the layer's own <see cref="LayerOptions.Opacity"/> times whatever
/// <see cref="DrawingContextImpl.PushOpacity"/> has folded in - and applies it
/// once, at the composite. A backdrop layer is the case where this shows most:
/// its content is the filtered surface beneath, which no draw inside the layer
/// could fade.
/// </summary>
public class LayerOpacityTests
{
    private const int Width = 200;
    private const int Height = 160;
    private static readonly Rect Box = new(40, 40, 120, 80);
    private static readonly IEffect Blur = new ImmutableBlurEffect(10);

    private static SKSurface CreateSurface() =>
        SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static DrawingContextImpl CreateContext(SKSurface surface) =>
        new(new DrawingContextImpl.CreateInfo { Surface = surface, Canvas = surface.Canvas, Dpi = new Vector(96, 96) });

    private static void DrawScene(DrawingContextImpl ctx)
    {
        ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
        for (var x = 0; x < Width; x += 16)
            ctx.DrawRectangle(Brushes.Black, null, new Rect(x, 0, 6, Height));
        ctx.DrawRectangle(Brushes.Crimson, null, new Rect(60, 60, 50, 40));
    }

    private static BackdropLayerCache? CacheFor(int payload) => payload switch
    {
        1 => new BackdropLayerCache { Payload = BackdropCachePayload.Output, RefreshRequested = true },
        2 => new BackdropLayerCache { Payload = BackdropCachePayload.Input, RefreshRequested = true },
        _ => null
    };

    private static void DrawBackdrop(DrawingContextImpl ctx, double opacity, BackdropLayerCache? cache)
    {
        ctx.PushClip(Box);
        ctx.PushLayer(new LayerOptions { Bounds = Box, BackdropEffect = Blur, BackdropCache = cache, Opacity = opacity });
        ctx.PopLayer();
        ctx.PopClip();
    }

    private static SKBitmap Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>
    /// The largest channel difference inside the box between the frame and
    /// <c>alpha * full + (1 - alpha) * plain</c>: the full-strength glass
    /// composited at <paramref name="alpha"/> over what is beneath it.
    /// </summary>
    private static int DifferenceFromComposite(SKSurface frame, double alpha)
    {
        using var full = CreateSurface();
        using (var ctx = CreateContext(full))
        {
            DrawScene(ctx);
            DrawBackdrop(ctx, 1, null);
        }

        using var plain = CreateSurface();
        using (var ctx = CreateContext(plain))
            DrawScene(ctx);

        using var pf = Pixels(frame);
        using var pa = Pixels(full);
        using var pb = Pixels(plain);
        var worst = 0;
        for (var y = (int)Box.Y; y < Box.Bottom; y++)
        for (var x = (int)Box.X; x < Box.Right; x++)
        {
            var c = pf.GetPixel(x, y);
            var a = pa.GetPixel(x, y);
            var b = pb.GetPixel(x, y);
            int Expected(byte u, byte v) => (int)Math.Round(alpha * u + (1 - alpha) * v);
            worst = Math.Max(worst, Math.Abs(c.Red - Expected(a.Red, b.Red)));
            worst = Math.Max(worst, Math.Abs(c.Green - Expected(a.Green, b.Green)));
            worst = Math.Max(worst, Math.Abs(c.Blue - Expected(a.Blue, b.Blue)));
        }

        return worst;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Backdrop_Layer_Opacity_Fades_The_Filtered_Backdrop(int payload)
    {
        using var surface = CreateSurface();
        using var cache = CacheFor(payload);
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            DrawBackdrop(ctx, 0.5, cache);
        }

        Assert.True(cache == null || cache.IsValid);
        var difference = DifferenceFromComposite(surface, 0.5);
        Assert.True(difference <= 2, $"Differed by {difference}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Backdrop_Layer_Takes_The_Folded_Opacity(int payload)
    {
        using var surface = CreateSurface();
        using var cache = CacheFor(payload);
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            ctx.PushOpacity(0.5, Box);
            DrawBackdrop(ctx, 1, cache);
            ctx.PopOpacity();
        }

        Assert.True(cache == null || cache.IsValid);
        var difference = DifferenceFromComposite(surface, 0.5);
        Assert.True(difference <= 2, $"Differed by {difference}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Retained_Payload_Does_Not_Keep_The_Opacity_It_Was_Captured_At(int payload)
    {
        using var surface = CreateSurface();
        using var cache = CacheFor(payload)!;
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            DrawBackdrop(ctx, 0.5, cache);
        }

        // The next frame draws the same payload at full strength.
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            DrawBackdrop(ctx, 1, cache);
        }

        Assert.True(cache.IsValid);
        var difference = DifferenceFromComposite(surface, 1);
        Assert.True(difference <= 2, $"Differed by {difference}");
    }

    [Fact]
    public void Layer_Under_Folded_Opacity_Is_Composited_Once()
    {
        // Two overlapping opaque squares inside one layer: the layer is one
        // group, so the overlap fades exactly like the rest of it instead of
        // stacking two faded draws.
        using var surface = CreateSurface();
        using (var ctx = CreateContext(surface))
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            ctx.PushOpacity(0.5, null);
            ctx.PushLayer(new LayerOptions { Isolate = true });
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(20, 20, 60, 60));
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(50, 50, 60, 60));
            ctx.PopLayer();
            ctx.PopOpacity();
        }

        using var pixels = Pixels(surface);
        var single = pixels.GetPixel(30, 30);
        var overlap = pixels.GetPixel(65, 65);
        Assert.Equal(single, overlap);
        Assert.InRange(single.Red, 126, 129);
    }

    [Fact]
    public void Drop_Shadow_Layer_Under_Folded_Opacity_Fades_Once()
    {
        var shadow = new ImmutableDropShadowEffect(8, 8, 6, Colors.Black, 1);

        using var folded = CreateSurface();
        using (var ctx = CreateContext(folded))
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            ctx.PushOpacity(0.5, null);
            ctx.PushLayer(new LayerOptions { Effect = shadow });
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(40, 40, 60, 60));
            ctx.PopLayer();
            ctx.PopOpacity();
        }

        using var direct = CreateSurface();
        using (var ctx = CreateContext(direct))
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            ctx.PushLayer(new LayerOptions { Effect = shadow, Opacity = 0.5 });
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(40, 40, 60, 60));
            ctx.PopLayer();
        }

        using var pf = Pixels(folded);
        using var pd = Pixels(direct);
        // Inside the shadow, outside the square.
        var a = pf.GetPixel(104, 104);
        var b = pd.GetPixel(104, 104);
        Assert.InRange(a.Red - b.Red, -1, 1);
        Assert.InRange(a.Green - b.Green, -1, 1);
        Assert.InRange(a.Blue - b.Blue, -1, 1);
    }
}
