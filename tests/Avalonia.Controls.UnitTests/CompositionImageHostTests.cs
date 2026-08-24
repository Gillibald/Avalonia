using System;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Controls.UnitTests;

/// <summary>
/// Exercises the composition-image hosting path an <see cref="Image"/> runs when its
/// source implements <see cref="ICompositionImage"/> — the source renders as a
/// render-thread-animating child composition visual instead of flat draw ops. The
/// fake below is a minimal, non-SVG <see cref="ICompositionImage"/>: its instance
/// hosts a compositor-bound <see cref="DrawingRecording"/> in a
/// <see cref="CompositionRecordingVisual"/>, the same stack SvgImage uses.
/// </summary>
public class CompositionImageHostTests : ScopedTestBase
{
    private readonly CompositorTestServices _services = new();

    public override void Dispose()
    {
        _services.Dispose();
        base.Dispose();
    }

    // The host attaches from the layout pass and finishes from the render pass
    // (which posts the attach back to the UI thread), so a couple of full
    // job/render cycles settle it deterministically.
    private void Settle()
    {
        _services.RunJobs();
        _services.RunJobs();
    }

    private Image ShowImage(FakeCompositionImage source)
    {
        var image = new Image { Source = source, Width = 80, Height = 80, Stretch = Stretch.Fill };
        _services.TopLevel.Content = image;
        Settle();
        return image;
    }

    [Fact]
    public void Hosts_The_Instance_Visual_As_The_Image_Child_Visual()
    {
        var source = new FakeCompositionImage();
        var image = ShowImage(source);

        Assert.NotNull(source.LastInstance);
        Assert.Same(source.LastInstance!.Visual, ElementComposition.GetElementChildVisual(image));
    }

    [Fact]
    public void Falls_Back_When_CreateInstance_Returns_Null()
    {
        var source = new FakeCompositionImage { ProducesInstance = false };
        var image = ShowImage(source);

        // Nothing to host — the control keeps drawing the source statically.
        Assert.Null(ElementComposition.GetElementChildVisual(image));
    }

    [Fact]
    public void Releases_And_Disposes_The_Instance_On_Detach()
    {
        var source = new FakeCompositionImage();
        var image = ShowImage(source);
        var instance = source.LastInstance!;

        _services.TopLevel.Content = null;
        Settle();

        Assert.True(instance.IsDisposed);
        Assert.Null(ElementComposition.GetElementChildVisual(image));
    }

    [Fact]
    public void Rebuilds_The_Instance_When_The_Source_Invalidates()
    {
        var source = new FakeCompositionImage();
        var image = ShowImage(source);
        var first = source.LastInstance!;

        source.RaiseInvalidated();
        Settle();
        var second = source.LastInstance!;

        Assert.True(first.IsDisposed);
        Assert.NotSame(first, second);
        Assert.Same(second.Visual, ElementComposition.GetElementChildVisual(image));
    }

    [Fact]
    public void Tears_Down_When_The_Source_Is_Swapped_For_A_Non_Composition_Image()
    {
        var source = new FakeCompositionImage();
        var image = ShowImage(source);
        var instance = source.LastInstance!;

        image.Source = new StaticImage();
        Settle();

        Assert.True(instance.IsDisposed);
        Assert.Null(ElementComposition.GetElementChildVisual(image));
    }

    [Fact]
    public void Pumps_The_Clock_While_The_Instance_Needs_One()
    {
        var source = new FakeCompositionImage { InstanceNeedsClock = true };
        ShowImage(source);
        var instance = source.LastInstance!;

        // Give the UI-thread animation-frame clock a few cycles to fire. The host
        // only requests frames because the instance reports NeedsClock; a
        // non-clocked instance never ticks (see the companion test).
        for (var i = 0; i < 4; i++)
            _services.RunJobs();

        Assert.True(instance.ClockTicks > 0,
            $"expected the clock to be pumped while NeedsClock is true, got {instance.ClockTicks}");
    }

    [Fact]
    public void Does_Not_Pump_The_Clock_When_The_Instance_Does_Not_Need_One()
    {
        var source = new FakeCompositionImage { InstanceNeedsClock = false };
        var image = ShowImage(source);
        var instance = source.LastInstance!;

        _services.RunJobs();
        _services.RunJobs();

        Assert.Equal(0, instance.ClockTicks);
    }

    [Fact]
    public void Applies_The_Stretch_Transform_To_The_Instance()
    {
        var source = new FakeCompositionImage();
        ShowImage(source);
        var instance = source.LastInstance!;

        Assert.True(instance.StretchUpdates > 0);
        // Stretch.Fill of an 80x80 source into an 80x80 image is the identity map.
        Assert.Equal(Matrix.Identity, instance.LastStretch);
    }

    private sealed class FakeCompositionImage : IImage, ICompositionImage
    {
        private EventHandler? _invalidated;

        public Size Size { get; set; } = new(80, 80);

        /// <summary>When false, <see cref="CreateInstance"/> returns null so the host falls back to <see cref="Draw"/>.</summary>
        public bool ProducesInstance { get; set; } = true;

        /// <summary>Seeds <see cref="ICompositionImageInstance.NeedsClock"/> on the instances this image builds.</summary>
        public bool InstanceNeedsClock { get; set; }

        public FakeInstance? LastInstance { get; private set; }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        event EventHandler? ICompositionImage.Invalidated
        {
            add => _invalidated += value;
            remove => _invalidated -= value;
        }

        public void RaiseInvalidated() => _invalidated?.Invoke(this, EventArgs.Empty);

        ICompositionImageInstance? ICompositionImage.CreateInstance(Compositor compositor)
        {
            if (!ProducesInstance)
                return null;

            return LastInstance = new FakeInstance(compositor) { NeedsClock = InstanceNeedsClock };
        }
    }

    private sealed class FakeInstance : ICompositionImageInstance
    {
        private readonly DrawingRecording _recording;

        public FakeInstance(Compositor compositor)
        {
            // The real stack the host is built for: a compositor-bound recording
            // carried to the render thread inside a recording visual.
            _recording = DrawingRecording.Create(compositor, ctx =>
                ctx.DrawRectangle(Brushes.MediumPurple, null, new Rect(0, 0, 80, 80)));
            var visual = compositor.CreateRecordingVisual();
            visual.Recording = _recording;
            Visual = visual;
        }

        public CompositionVisual Visual { get; }
        public bool NeedsClock { get; init; }
        public int StretchUpdates { get; private set; }
        public Matrix LastStretch { get; private set; }
        public int ClockTicks { get; private set; }
        public bool IsDisposed { get; private set; }

        public void SetStretchTransform(Matrix transform)
        {
            StretchUpdates++;
            LastStretch = transform;
        }

        public void OnClock(TimeSpan elapsed) => ClockTicks++;

        public void Dispose()
        {
            IsDisposed = true;
            _recording.Dispose();
        }
    }

    // A plain non-composition image, used to prove the host tears down when the
    // source is swapped away from an ICompositionImage.
    private sealed class StaticImage : IImage
    {
        public Size Size => new(10, 10);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
