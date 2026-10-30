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
/// once, at the composite.
/// </summary>
public class LayerOpacityTests
{
    private const int Width = 200;
    private const int Height = 160;

    private static SKSurface CreateSurface() =>
        SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static DrawingContextImpl CreateContext(SKSurface surface) =>
        new(new DrawingContextImpl.CreateInfo { Surface = surface, Canvas = surface.Canvas, Dpi = new Vector(96, 96) });

    private static SKBitmap Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
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
