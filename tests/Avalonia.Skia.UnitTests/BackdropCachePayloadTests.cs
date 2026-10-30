using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests;

/// <summary>
/// The backend half of the <see cref="BackdropLayerCache"/> handshake, driven
/// frame by frame the way the compositor does: a partial frame clips to its
/// dirty region and leaves the rest of the surface as the previous frame left
/// it. Every retained payload must produce the pixels the live, uncached
/// backdrop produces from a full repaint of the same scene.
/// </summary>
public class BackdropCachePayloadTests
{
    private const int Width = 200;
    private const int Height = 160;
    private static readonly Rect Box = new(40, 40, 120, 80);
    private static readonly IEffect Blur = new ImmutableBlurEffect(10);
    private static readonly IBrush Wash = new ImmutableSolidColorBrush(Colors.White, 0.25);

    private static SKSurface CreateSurface() =>
        SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

    /// <summary>
    /// Busy content beneath the glass; <paramref name="spot"/> recolors one
    /// small square so a frame can change a little of what the glass reads.
    /// </summary>
    private static void DrawScene(DrawingContextImpl ctx, IBrush spot)
    {
        ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
        ctx.DrawRectangle(Brushes.Crimson, null, new Rect(20, 30, 70, 60));
        ctx.DrawRectangle(Brushes.DodgerBlue, null, new Rect(100, 70, 80, 70));
        for (var x = 0; x < Width; x += 16)
            ctx.DrawRectangle(Brushes.Black, null, new Rect(x, 0, 4, Height));
        ctx.DrawRectangle(spot, null, new Rect(64, 64, 8, 8));
    }

    /// <summary>
    /// One compositor frame: repaint beneath within <paramref name="dirty"/>
    /// (everything when null), apply the backdrop, then the glass's own wash.
    /// </summary>
    private static void Frame(SKSurface surface, IBrush spot, BackdropLayerCache? cache, Rect? dirty = null,
        Matrix? transform = null)
    {
        using var ctx = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
        {
            Surface = surface,
            Canvas = surface.Canvas,
            Dpi = new Vector(96, 96)
        });

        if (dirty is { } clip)
            ctx.PushClip(clip);

        DrawScene(ctx, spot);

        var saved = ctx.Transform;
        if (transform is { } t)
            ctx.Transform = t * saved;
        ctx.PushLayer(new LayerOptions { Bounds = Box, BackdropEffect = Blur, BackdropCache = cache });
        ctx.PopLayer();
        ctx.DrawRectangle(Wash, null, Box);
        ctx.Transform = saved;

        if (dirty != null)
            ctx.PopClip();
    }

    private static SKSurface LiveRender(IBrush spot, Matrix? transform = null)
    {
        var surface = CreateSurface();
        Frame(surface, spot, cache: null, transform: transform);
        return surface;
    }

    private static int MaxDifference(SKSurface a, SKSurface b, PixelRect? area = null)
    {
        using var ia = a.Snapshot();
        using var ib = b.Snapshot();
        using var pa = SKBitmap.FromImage(ia);
        using var pb = SKBitmap.FromImage(ib);

        var r = area ?? new PixelRect(0, 0, Width, Height);
        var worst = 0;
        for (var y = r.Y; y < r.Bottom; y++)
        for (var x = r.X; x < r.Right; x++)
        {
            var ca = pa.GetPixel(x, y);
            var cb = pb.GetPixel(x, y);
            worst = Math.Max(worst, Math.Abs(ca.Red - cb.Red));
            worst = Math.Max(worst, Math.Abs(ca.Green - cb.Green));
            worst = Math.Max(worst, Math.Abs(ca.Blue - cb.Blue));
            worst = Math.Max(worst, Math.Abs(ca.Alpha - cb.Alpha));
        }

        return worst;
    }

    private static BackdropCachePayload PayloadOf(bool input) =>
        input ? BackdropCachePayload.Input : BackdropCachePayload.Output;

    private static BackdropLayerCache FullGrant(BackdropCachePayload payload) =>
        new() { Payload = payload, RefreshRequested = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_Capture_Matches_The_Live_Backdrop(bool input)
    {
        var payload = PayloadOf(input);
        using var reference = LiveRender(Brushes.Gold);
        using var surface = CreateSurface();
        using var cache = FullGrant(payload);

        Frame(surface, Brushes.Gold, cache);

        Assert.True(cache.IsValid);
        Assert.NotNull(cache.PlatformState);
        Assert.True(MaxDifference(surface, reference) <= 1);
    }

    [Fact]
    public void Input_Capture_Matches_The_Output_Capture_Under_Rotation_And_Scale()
    {
        var center = Box.Center;
        var transform = Matrix.CreateTranslation(-center.X, -center.Y)
                        * Matrix.CreateRotation(Math.PI / 9)
                        * Matrix.CreateScale(0.8, 0.8)
                        * Matrix.CreateTranslation(center.X, center.Y);

        using var output = CreateSurface();
        using var outputCache = FullGrant(BackdropCachePayload.Output);
        Frame(output, Brushes.Gold, outputCache, transform: transform);

        using var input = CreateSurface();
        using var inputCache = FullGrant(BackdropCachePayload.Input);
        Frame(input, Brushes.Gold, inputCache, transform: transform);

        // The retained input reaches past the rotated box by the scaled
        // reach, so it filters exactly what the retained output filtered.
        // Both retained paths stay within a few levels of the live backdrop,
        // which takes a different route through Skia's layers under rotation.
        using var reference = LiveRender(Brushes.Gold, transform);
        Assert.True(inputCache.IsValid);
        var betweenPayloads = MaxDifference(input, output);
        var fromLive = MaxDifference(input, reference);
        Assert.True(betweenPayloads <= 1, $"Input and output captures differed by {betweenPayloads}");
        Assert.True(fromLive <= 4, $"Input capture differed from the live backdrop by {fromLive}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Front_Only_Frame_Draws_From_The_Retained_Payload(bool input)
    {
        var payload = PayloadOf(input);
        using var reference = LiveRender(Brushes.Gold);
        using var surface = CreateSurface();
        using var cache = FullGrant(payload);
        Frame(surface, Brushes.Gold, cache);

        // Content above the glass changed inside this clip, so the frame
        // repaints beneath there too. Painting a different spot beneath while
        // no refresh is granted proves the glass shows its retained payload,
        // not a fresh sample of the surface; the clip edges cut right through
        // the blur, so a filter that only saw the clipped input would show it.
        var dirty = new Rect(58, 58, 30, 24);
        Frame(surface, Brushes.Green, cache, dirty);

        Assert.True(cache.IsValid);
        Assert.True(MaxDifference(surface, reference, new PixelRect(58, 58, 30, 24)) <= 1);
    }

    [Fact]
    public void Input_Partial_Ingest_Matches_A_Fresh_Render()
    {
        using var surface = CreateSurface();
        using var cache = FullGrant(BackdropCachePayload.Input);
        Frame(surface, Brushes.Gold, cache);

        // The spot (64,64,8,8) changed beneath the glass. Blur 10 reaches 11,
        // so the output changes within O = (53,53,30,30) n box. The retained
        // input only needs the spot copied in; nothing else beneath the glass
        // is repainted, and the ring around O still holds last frame's
        // composited glass.
        cache.IsValid = true;
        cache.RefreshRequested = true;
        cache.IngestRects.Add(new Rect(64, 64, 8, 8));
        Frame(surface, Brushes.Green, cache, new Rect(53, 53, 30, 30));

        using var reference = LiveRender(Brushes.Green);
        Assert.True(cache.IsValid);
        Assert.Empty(cache.IngestRects);
        Assert.True(MaxDifference(surface, reference) <= 1);
    }

    [Fact]
    public void No_Cache_Payload_Releases_The_Retained_Payload_And_Draws_Live()
    {
        using var reference = LiveRender(Brushes.Gold);
        using var surface = CreateSurface();
        using var cache = FullGrant(BackdropCachePayload.Output);
        Frame(surface, Brushes.Gold, cache);
        Assert.NotNull(cache.PlatformState);

        cache.Payload = BackdropCachePayload.None;
        Frame(surface, Brushes.Gold, cache);

        Assert.False(cache.IsValid);
        Assert.Null(cache.PlatformState);
        Assert.True(MaxDifference(surface, reference) <= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retained_Pixels_Of_Another_Payload_Are_Never_Drawn(bool capturedInput)
    {
        var captured = PayloadOf(capturedInput);
        var current = PayloadOf(!capturedInput);
        using var surface = CreateSurface();
        using var cache = FullGrant(captured);
        Frame(surface, Brushes.Gold, cache);

        // A valid flag over the other payload's pixels must not be trusted:
        // the frame falls back to the live backdrop and drops the validity.
        cache.Payload = current;
        Frame(surface, Brushes.Green, cache);

        using var reference = LiveRender(Brushes.Green);
        Assert.False(cache.IsValid);
        Assert.True(MaxDifference(surface, reference) <= 1);
    }
}
