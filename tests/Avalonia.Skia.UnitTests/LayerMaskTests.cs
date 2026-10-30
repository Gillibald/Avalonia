using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests;

/// <summary>
/// A layer's backdrop fills <see cref="LayerOptions.BackdropBounds"/> shaped by
/// the backdrop clip while content drawn into the layer stays unclipped, and
/// <see cref="LayerOptions.OpacityMask"/> masks the composited layer as a whole -
/// backdrop and content together.
/// </summary>
public class LayerMaskTests
{
    private const int Width = 200;
    private const int Height = 160;
    private static readonly Rect Box = new(40, 40, 120, 80);
    private static readonly IEffect Blur = new ImmutableBlurEffect(10);

    // Opaque at the box's left edge, transparent at its right edge.
    private static readonly IBrush FadeRight = new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, Colors.White), new ImmutableGradientStop(1, Colors.Transparent) },
        1, null, null, GradientSpreadMethod.Pad,
        new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative));

    // Bright on the left, dark on the right: the same fade read through luminance.
    private static readonly IBrush LumaFadeRight = new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, Colors.White), new ImmutableGradientStop(1, Colors.Black) },
        1, null, null, GradientSpreadMethod.Pad,
        new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative));

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

    private static SKBitmap Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static int MaxDifference(SKBitmap a, SKBitmap b, PixelRect area)
    {
        var worst = 0;
        for (var y = area.Y; y < area.Bottom; y++)
        for (var x = area.X; x < area.Right; x++)
        {
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            worst = Math.Max(worst, Math.Abs(p.Red - q.Red));
            worst = Math.Max(worst, Math.Abs(p.Green - q.Green));
            worst = Math.Max(worst, Math.Abs(p.Blue - q.Blue));
        }

        return worst;
    }

    /// <summary>The scene with an unmasked, full-strength backdrop over the box.</summary>
    private static SKSurface FullGlass()
    {
        var surface = CreateSurface();
        using var ctx = CreateContext(surface);
        DrawScene(ctx);
        ctx.PushLayer(new LayerOptions { Bounds = Box, BackdropEffect = Blur, BackdropBounds = Box });
        ctx.PopLayer();
        return surface;
    }

    private static SKSurface PlainScene()
    {
        var surface = CreateSurface();
        using var ctx = CreateContext(surface);
        DrawScene(ctx);
        return surface;
    }

    [Fact]
    public void Backdrop_Fills_Its_Bounds_And_Content_Stays_Unclipped()
    {
        using var surface = CreateSurface();
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            ctx.PushLayer(new LayerOptions
            {
                Bounds = new Rect(10, 10, 180, 140), BackdropEffect = Blur, BackdropBounds = Box
            });
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(14, 14, 12, 12));
            ctx.PopLayer();
        }

        using var full = FullGlass();
        using var plain = PlainScene();
        using var pixels = Pixels(surface);
        using var fullPixels = Pixels(full);
        using var plainPixels = Pixels(plain);

        Assert.Equal(new SKColor(0, 0, 255), pixels.GetPixel(20, 20));
        Assert.True(MaxDifference(pixels, fullPixels, new PixelRect(40, 40, 120, 80)) <= 1);
        // Inside the layer, outside the backdrop and the content: untouched.
        Assert.True(MaxDifference(pixels, plainPixels, new PixelRect(12, 100, 26, 40)) <= 1);
    }

    [Fact]
    public void Backdrop_Clip_Shapes_The_Backdrop()
    {
        using var surface = CreateSurface();
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            ctx.PushLayer(new LayerOptions
            {
                Bounds = Box, BackdropEffect = Blur, BackdropBounds = Box,
                BackdropClipImpl = new EllipseGeometryImpl(Box)
            });
            ctx.PopLayer();
        }

        using var full = FullGlass();
        using var plain = PlainScene();
        using var pixels = Pixels(surface);
        using var fullPixels = Pixels(full);
        using var plainPixels = Pixels(plain);

        // The box's corner lies outside the ellipse; its centre inside.
        Assert.True(MaxDifference(pixels, plainPixels, new PixelRect(41, 41, 8, 8)) <= 1);
        Assert.True(MaxDifference(pixels, fullPixels, new PixelRect(90, 70, 20, 20)) <= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layer_Mask_Matches_PushOpacityMask(bool luminance)
    {
        var mask = luminance ? LumaFadeRight : FadeRight;
        var type = luminance ? MaskType.Luminance : MaskType.Alpha;

        void Content(DrawingContextImpl ctx)
        {
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(40, 40, 80, 50));
            ctx.DrawRectangle(Brushes.Gold, null, new Rect(90, 70, 70, 50));
        }

        using var viaLayer = CreateSurface();
        using (var ctx = CreateContext(viaLayer))
        {
            DrawScene(ctx);
            ctx.PushLayer(new LayerOptions { Bounds = Box, OpacityMask = mask, OpacityMaskType = type });
            Content(ctx);
            ctx.PopLayer();
        }

        using var viaMask = CreateSurface();
        using (var ctx = CreateContext(viaMask))
        {
            DrawScene(ctx);
            ctx.PushOpacityMask(mask, Box, type);
            Content(ctx);
            ctx.PopOpacityMask();
        }

        using var a = Pixels(viaLayer);
        using var b = Pixels(viaMask);
        Assert.True(MaxDifference(a, b, new PixelRect(0, 0, Width, Height)) <= 1);
    }

    [Fact]
    public void Layer_Mask_Applies_After_The_Layer_Effect()
    {
        // The mask covers what the effect produced, so a drop shadow cast to
        // the right fades with the content above it.
        var shadow = new ImmutableDropShadowEffect(16, 8, 4, Colors.Black, 1);

        void Content(DrawingContextImpl ctx) =>
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(50, 50, 80, 40));

        using var combined = CreateSurface();
        using (var ctx = CreateContext(combined))
        {
            DrawScene(ctx);
            ctx.PushLayer(new LayerOptions { Bounds = Box, Effect = shadow, OpacityMask = FadeRight });
            Content(ctx);
            ctx.PopLayer();
        }

        using var nested = CreateSurface();
        using (var ctx = CreateContext(nested))
        {
            DrawScene(ctx);
            ctx.PushOpacityMask(FadeRight, Box);
            ctx.PushLayer(new LayerOptions { Bounds = Box, Effect = shadow });
            Content(ctx);
            ctx.PopLayer();
            ctx.PopOpacityMask();
        }

        using var a = Pixels(combined);
        using var b = Pixels(nested);
        Assert.True(MaxDifference(a, b, new PixelRect(0, 0, Width, Height)) <= 1);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void Layer_Mask_Fades_Backdrop_And_Content_Together(double opacity)
    {
        using var surface = CreateSurface();
        using var cache = new BackdropLayerCache { Payload = BackdropCachePayload.Output, RefreshRequested = true };
        using (var ctx = CreateContext(surface))
        {
            DrawScene(ctx);
            ctx.PushLayer(new LayerOptions
            {
                Bounds = Box, BackdropEffect = Blur, BackdropBounds = Box, BackdropCache = cache,
                OpacityMask = FadeRight, Opacity = opacity
            });
            ctx.PopLayer();
        }

        // A masked layer composites through its own offscreen buffer, so it
        // samples live rather than drawing a retained payload.
        Assert.False(cache.IsValid);

        using var full = FullGlass();
        using var plain = PlainScene();
        using var pixels = Pixels(surface);
        using var fullPixels = Pixels(full);
        using var plainPixels = Pixels(plain);

        var worst = 0;
        for (var y = (int)Box.Y; y < Box.Bottom; y++)
        for (var x = (int)Box.X; x < Box.Right; x++)
        {
            // The gradient's coverage at this pixel centre, times the layer opacity.
            var m = opacity * Math.Clamp(1 - (x + 0.5 - Box.X) / Box.Width, 0, 1);
            var c = pixels.GetPixel(x, y);
            var a = fullPixels.GetPixel(x, y);
            var b = plainPixels.GetPixel(x, y);
            int Expected(byte u, byte v) => (int)Math.Round(m * u + (1 - m) * v);
            worst = Math.Max(worst, Math.Abs(c.Red - Expected(a.Red, b.Red)));
            worst = Math.Max(worst, Math.Abs(c.Green - Expected(a.Green, b.Green)));
            worst = Math.Max(worst, Math.Abs(c.Blue - Expected(a.Blue, b.Blue)));
        }

        Assert.True(worst <= 3, $"Differed by {worst} from the masked composite");
    }
}
