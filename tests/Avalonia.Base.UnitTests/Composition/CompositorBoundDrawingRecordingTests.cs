using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Drawing.Nodes;
using Avalonia.UnitTests;
using Moq;
using Xunit;

namespace Avalonia.Base.UnitTests.Composition;

public class CompositorBoundDrawingRecordingTests : ScopedTestBase
{
    private readonly CompositorTestServices _services = new();

    public override void Dispose()
    {
        _services.Dispose();
        base.Dispose();
    }

    private void ForceCommitAndRender()
    {
        _services.Compositor.Commit();
        _services.Compositor.Server.Render(false);
    }

    [Fact]
    public void Create_With_Compositor_Returns_NonNull()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        Assert.NotNull(recording);
        Assert.False(recording.IsDisposed);
        Assert.Same(_services.Compositor, recording.Compositor);
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_Bounds_Available_Before_Commit()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        // Bounds are computed from the client-side item list and are valid
        // as soon as the record delegate returns — no compositor commit required.
        var bounds = recording.Bounds;
        Assert.Equal(new Rect(10, 10, 100, 50), bounds);
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_Bounds_Unchanged_By_Commit()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        var boundsBefore = recording.Bounds;
        ForceCommitAndRender();
        var boundsAfter = recording.Bounds;

        Assert.Equal(boundsBefore, boundsAfter);
        Assert.Equal(new Rect(10, 10, 100, 50), boundsAfter);
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_Empty_Recording_Has_Default_Bounds()
    {
        var recording = DrawingRecording.Create(_services.Compositor, _ => { });

        Assert.Equal(default, recording.Bounds);
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_HitTest_Works()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        Assert.True(recording.HitTest(new Point(50, 30)));
        Assert.False(recording.HitTest(new Point(0, 0)));
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_Supports_Mutable_Brush()
    {
        var brush = new SolidColorBrush(Colors.Red);
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(brush, null, new Rect(0, 0, 100, 100));
        });

        Assert.NotNull(recording);
        recording.Dispose();
    }

    [Fact]
    public void Compositor_Bound_DrawRecording_Into_RenderDataContext()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 50, 50));
        });

        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(recording);
        var result = outerCtx.GetRenderResults();

        Assert.NotNull(result);
        recording.Dispose();
    }

    [Fact]
    public void Nested_Recording_Resources_Managed_By_Parent()
    {
        var brush = new SolidColorBrush(Colors.Red);

        var inner = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(brush, null, new Rect(0, 0, 50, 50));
        });

        // Draw the inner recording into an outer context — this should register
        // a CompositionRenderDataResourceRef on the outer CompositionRenderData.
        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(inner);
        var outerRenderData = outerCtx.GetRenderResults();

        Assert.NotNull(outerRenderData);

        // Disposing the inner recording should NOT fully release resources
        // because the outer context holds a ref via the resource wrapper.
        inner.Dispose();

        // The outer render data should still be functional.
        ForceCommitAndRender();

        // Now dispose the outer render data — this releases the wrapper's ref,
        // which should be the final ref, completing cleanup.
        outerRenderData!.Dispose();
    }

    [Fact]
    public void Nested_Recording_Survives_Recording_Disposal()
    {
        var inner = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Green, null, new Rect(5, 5, 40, 40));
        });

        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(inner);
        var outerRenderData = outerCtx.GetRenderResults()!;

        // Dispose inner first — outer still holds a ref.
        inner.Dispose();

        // Commit and render should succeed without threading issues.
        ForceCommitAndRender();

        var bounds = outerRenderData.Server.Bounds?.ToRect();
        Assert.NotNull(bounds);
        Assert.Equal(new Rect(5, 5, 40, 40), bounds);

        outerRenderData.Dispose();
    }

    [Fact]
    public void Same_Recording_Drawn_Multiple_Times_Increments_RefCount()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 10, 10));
        });

        // Draw the same recording twice into the outer context.
        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(recording);
        outerCtx.DrawRecording(recording);
        var outerRenderData = outerCtx.GetRenderResults()!;

        // Dispose the outer — two wrappers release two refs.
        outerRenderData.Dispose();

        // The original recording should still be alive (it holds the original ref).
        Assert.False(recording.IsDisposed);
        Assert.NotEqual(default, recording.Bounds);

        recording.Dispose();
    }

    [Fact]
    public void Nested_Recording_Node_Not_Disposable()
    {
        // Verifies that the node created for a compositor-bound recording
        // does not implement IDisposable, making it safe on the render thread.
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 50, 50));
        });

        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(recording);
        var outerRenderData = outerCtx.GetRenderResults()!;

        ForceCommitAndRender();

        // After commit, the server-side render data has items.
        // Re-committing with new data triggers Reset() which disposes old items.
        // This should not throw because the node is not IDisposable.
        using var outerCtx2 = new RenderDataDrawingContext(_services.Compositor);
        outerCtx2.DrawRecording(recording);
        var outerRenderData2 = outerCtx2.GetRenderResults()!;

        // This commit replaces old server items, triggering Reset() on render thread.
        ForceCommitAndRender();

        outerRenderData.Dispose();
        outerRenderData2.Dispose();
        recording.Dispose();
    }

    [Fact]
    public void Disposed_Compositor_Bound_Recording_Throws()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        recording.Dispose();
        Assert.True(recording.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => recording.Bounds);
    }

    [Fact]
    public void Immutable_Recording_Renders_Via_PlatformDrawingContext()
    {
        var mockImpl = new Mock<IDrawingContextImpl>();
        mockImpl.Setup(x => x.Transform).Returns(Matrix.Identity);

        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 50));
        });

        using var platformCtx = new PlatformDrawingContext(mockImpl.Object, false);
        platformCtx.DrawRecording(recording);

        mockImpl.Verify(x => x.DrawRectangle(
            It.IsAny<IBrush>(), It.IsAny<IPen>(),
            It.IsAny<RoundedRect>(), It.IsAny<BoxShadows>()), Times.Once);
    }

    [Fact]
    public void Compositor_Bound_Recording_Renders_Via_PlatformDrawingContext()
    {
        var mockImpl = new Mock<IDrawingContextImpl>();
        mockImpl.Setup(x => x.Transform).Returns(Matrix.Identity);

        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 50));
        });

        using var platformCtx = new PlatformDrawingContext(mockImpl.Object, false);
        platformCtx.DrawRecording(recording);

        mockImpl.Verify(x => x.DrawRectangle(
            It.IsAny<IBrush>(), It.IsAny<IPen>(),
            It.IsAny<RoundedRect>(), It.IsAny<BoxShadows>()), Times.Once);

        recording.Dispose();
    }

    [Fact]
    public void Immutable_Recording_Embeds_In_Compositor_Context()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 50, 50));
        });

        using var outerCtx = new RenderDataDrawingContext(_services.Compositor);
        outerCtx.DrawRecording(recording);
        var result = outerCtx.GetRenderResults();

        Assert.NotNull(result);
    }

    [Fact]
    public void Parent_Bounds_Include_Nested_Compositor_Bound_Child_Before_Commit()
    {
        var child = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        var parent = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRecording(child);
        });

        // Parent bounds must reflect the nested child's bounds without waiting
        // for a compositor commit.
        Assert.Equal(new Rect(10, 10, 100, 50), parent.Bounds);
        Assert.True(parent.HitTest(new Point(50, 30)));

        parent.Dispose();
        child.Dispose();
    }

    [Fact]
    public void Parent_Retains_Compositor_Bound_Child_After_External_Dispose()
    {
        var child = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        var parent = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRecording(child);
        });

        // External owner disposes the child before the parent commits.
        child.Dispose();

        // Parent must still report the correct bounds and hit-test through
        // its retained refcount on the child's render data.
        Assert.Equal(new Rect(10, 10, 100, 50), parent.Bounds);
        Assert.True(parent.HitTest(new Point(50, 30)));

        parent.Dispose();
    }

    [Fact]
    public void Compositor_Bound_Child_Shared_Across_Multiple_Parents()
    {
        var child = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        var parent1 = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRecording(child);
        });
        var parent2 = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRecording(child);
        });

        // Dispose child and one parent.
        child.Dispose();
        parent1.Dispose();

        // Remaining parent still has a live reference.
        Assert.Equal(new Rect(0, 0, 10, 10), parent2.Bounds);
        Assert.True(parent2.HitTest(new Point(5, 5)));

        parent2.Dispose();
    }

    [Fact]
    public void DrawRecording_Throws_On_Disposed_Recording()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        recording.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var _ = DrawingRecording.Create(_services.Compositor, ctx =>
            {
                ctx.DrawRecording(recording);
            });
        });
    }

    [Fact]
    public void DrawRecording_With_Matrix_Throws_On_Disposed_Recording()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        recording.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var _ = DrawingRecording.Create(_services.Compositor, ctx =>
            {
                ctx.DrawRecording(recording, Matrix.CreateTranslation(5, 5));
            });
        });
    }

    [Fact]
    public void Compositor_Bound_GetBounds_With_Matrix_Works_Before_Commit()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        Assert.Equal(
            new Rect(100, 200, 10, 10),
            recording.GetBounds(Matrix.CreateTranslation(100, 200)));

        recording.Dispose();
    }

    [Fact]
    public void BoundsChanged_Throws_On_Immutable_Recording()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            recording.BoundsChanged += (_, _) => { };
        });
    }

    [Fact]
    public void BoundsChanged_Throws_On_Disposed_Recording()
    {
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        recording.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            recording.BoundsChanged += (_, _) => { };
        });
    }

    [Fact]
    public void BoundsChanged_Does_Not_Fire_When_Bounds_Stable()
    {
        using var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        int fired = 0;
        recording.BoundsChanged += (_, _) => fired++;

        ForceCommitAndRender();
        ForceCommitAndRender();

        Assert.Equal(0, fired);
    }

    [Fact]
    public void BoundsChanged_Fires_When_Pen_Thickness_Animated()
    {
        var pen = new Pen(Brushes.Black, 2);
        using var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(null, pen, new Rect(10, 10, 100, 50));
        });

        var fired = new List<Rect>();
        recording.BoundsChanged += (_, bounds) => fired.Add(bounds);

        // Commit once with no changes — event should not fire.
        ForceCommitAndRender();
        Assert.Empty(fired);

        // Mutate the pen: thickness 2 → 20; bounds inflate by (20-2)/2 = 9 on each side.
        pen.Thickness = 20;
        ForceCommitAndRender();

        Assert.Single(fired);
    }

    [Fact]
    public void BoundsChanged_Unsubscribe_Stops_Notifications()
    {
        var pen = new Pen(Brushes.Black, 2);
        using var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(null, pen, new Rect(10, 10, 100, 50));
        });

        int fired = 0;
        EventHandler<Rect> handler = (_, _) => fired++;
        recording.BoundsChanged += handler;

        pen.Thickness = 20;
        ForceCommitAndRender();
        Assert.Equal(1, fired);

        recording.BoundsChanged -= handler;

        pen.Thickness = 30;
        ForceCommitAndRender();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void BoundsChanged_Dispose_Cleans_Up_Subscription()
    {
        var pen = new Pen(Brushes.Black, 2);
        var recording = DrawingRecording.Create(_services.Compositor, ctx =>
        {
            ctx.DrawRectangle(null, pen, new Rect(10, 10, 100, 50));
        });

        int fired = 0;
        recording.BoundsChanged += (_, _) => fired++;

        recording.Dispose();

        // Mutation + commit after dispose must not invoke the handler.
        pen.Thickness = 30;
        ForceCommitAndRender();

        Assert.Equal(0, fired);
    }
}
