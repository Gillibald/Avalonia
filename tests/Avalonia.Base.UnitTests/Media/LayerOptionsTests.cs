using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.UnitTests;
using Moq;
using Xunit;

namespace Avalonia.Base.UnitTests.Media;

public class LayerOptionsTests : ScopedTestBase
{
    [Fact]
    public void A_Mask_Needs_Bounds_To_Map_Onto()
    {
        using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface);
        var group = new DrawingGroup();
        using var ctx = group.Open();

        Assert.Throws<ArgumentException>(() =>
            ctx.PushLayer(new LayerOptions { OpacityMask = Brushes.Black }));
    }

    [Fact]
    public void A_Mask_Makes_The_Layer_More_Than_A_Passthrough()
    {
        Assert.True(new LayerOptions().IsPassthrough);
        Assert.False(new LayerOptions { OpacityMask = Brushes.Black, Bounds = new Rect(0, 0, 10, 10) }.IsPassthrough);
    }

    [Fact]
    public void Recorded_Layer_Replays_Its_Mask_And_Backdrop_Region()
    {
        using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface);
        var clip = new EllipseGeometry(new Rect(10, 10, 40, 20));
        var mask = new ImmutableSolidColorBrush(Colors.Black, 0.5);

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions
                   {
                       Bounds = new Rect(0, 0, 100, 80),
                       OpacityMask = mask,
                       OpacityMaskType = MaskType.Luminance,
                       BackdropEffect = new ImmutableBlurEffect(4),
                       BackdropBounds = new Rect(10, 10, 40, 20),
                       BackdropClip = clip
                   }))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(60, 40, 10, 10));
            }
        });

        LayerOptions? replayed = null;
        var impl = new Mock<IDrawingContextImpl>();
        impl.As<IDrawingContextImplWithLayers>()
            .Setup(x => x.PushLayer(It.IsAny<LayerOptions>()))
            .Callback<LayerOptions>(options => replayed = options);

        recording.Render(impl.Object);

        Assert.True(replayed.HasValue);
        var options = replayed!.Value;
        Assert.Equal(new Rect(0, 0, 100, 80), options.Bounds);
        Assert.Equal(MaskType.Luminance, options.OpacityMaskType);
        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(options.OpacityMask).Color);
        Assert.Equal(new Rect(10, 10, 40, 20), options.BackdropBounds);
        Assert.NotNull(options.BackdropClipImpl);
        Assert.Equal(clip.PlatformImpl!.Bounds, options.BackdropClipImpl!.Bounds);
    }

    [Fact]
    public void Backdrop_Layer_Bounds_Cover_Its_Backdrop_Region_Over_Empty_Content()
    {
        using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface);

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions
                   {
                       Bounds = new Rect(0, 0, 100, 80),
                       BackdropEffect = new ImmutableBlurEffect(4),
                       BackdropBounds = new Rect(10, 10, 40, 20)
                   }))
            {
            }
        });

        // The backdrop paints its region even with nothing drawn into the layer.
        Assert.Equal(new Rect(10, 10, 40, 20), recording.Bounds);
    }
}
