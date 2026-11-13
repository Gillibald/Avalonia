using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Server;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

/// <summary>
/// A retained backdrop holds GPU resources created on the compositor's graphics
/// context. Releasing them is only safe with that context current, including when
/// the release happens outside the render pass: the glass losing its effect or
/// leaving the tree is applied while the compositor processes a batch.
/// </summary>
public class BackdropCacheLifetimeTests : CompositorTestsBase
{
    private sealed class TrackingContext : IPlatformGraphicsContext
    {
        public int CurrentDepth { get; private set; }
        public bool IsLost => false;

        public IDisposable EnsureCurrent()
        {
            CurrentDepth++;
            return Disposable.Create(() => CurrentDepth--);
        }

        public object? TryGetFeature(Type featureType) => null;
        public void Dispose() { }
    }

    private sealed class TrackingGraphics : IPlatformGraphics
    {
        public TrackingContext Context { get; } = new();
        public bool UsesSharedContext => true;
        public IPlatformGraphicsContext CreateContext() => throw new NotSupportedException();
        public IPlatformGraphicsContext GetSharedContext() => Context;
    }

    private sealed class ContextBoundState : IDisposable
    {
        private readonly TrackingContext _context;
        public ContextBoundState(TrackingContext context) => _context = context;
        public bool Disposed { get; private set; }
        public bool DisposedWithContextCurrent { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            DisposedWithContextCurrent = _context.CurrentDepth > 0;
        }
    }

    private static (Border Panel, ContextBoundState State) RetainedGlass(CompositorTestServices s,
        TrackingGraphics graphics, Canvas canvas)
    {
        var panel = new Border
        {
            Background = new ImmutableSolidColorBrush(Colors.White, 0.25),
            Width = 100,
            Height = 100,
            BackdropEffect = new ImmutableBlurEffect(8)
        };
        canvas.Children.Add(panel);
        s.RunJobs();

        var server = (ServerCompositionVisual)ElementComposition.GetElementVisual(panel)!.Server!;
        var state = new ContextBoundState(graphics.Context);
        server.BackdropCache!.PlatformState = state;
        return (panel, state);
    }

    [Fact]
    public void Clearing_The_Effect_Releases_The_Retained_State_With_The_Context_Current()
    {
        var graphics = new TrackingGraphics();
        using var s = new CompositorTestServices(new Size(200, 200), graphics: graphics);
        var canvas = new Canvas();
        s.TopLevel.Content = canvas;
        var (panel, state) = RetainedGlass(s, graphics, canvas);

        panel.BackdropEffect = null;
        s.RunJobs();

        Assert.True(state.Disposed);
        Assert.True(state.DisposedWithContextCurrent);
    }

    [Fact]
    public void Removing_The_Glass_Releases_The_Retained_State_With_The_Context_Current()
    {
        var graphics = new TrackingGraphics();
        using var s = new CompositorTestServices(new Size(200, 200), graphics: graphics);
        var canvas = new Canvas();
        s.TopLevel.Content = canvas;
        var (panel, state) = RetainedGlass(s, graphics, canvas);

        canvas.Children.Remove(panel);
        s.RunJobs();

        Assert.True(state.Disposed);
        Assert.True(state.DisposedWithContextCurrent);
    }
}
