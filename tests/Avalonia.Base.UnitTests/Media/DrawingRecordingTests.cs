using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Xunit;

namespace Avalonia.Base.UnitTests.Media;

public class DrawingRecordingTests
{
    [Fact]
    public void Create_Returns_NonNull()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        Assert.NotNull(recording);
        Assert.False(recording.IsDisposed);
        Assert.Null(recording.Compositor);
        recording.Dispose();
    }

    [Fact]
    public void Empty_Recording_Returns_NonNull()
    {
        var recording = DrawingRecording.Create(_ => { });

        Assert.NotNull(recording);
        Assert.False(recording.IsDisposed);
        recording.Dispose();
    }

    [Fact]
    public void Empty_Recording_Has_Default_Bounds()
    {
        var recording = DrawingRecording.Create(_ => { });

        Assert.Equal(default, recording.Bounds);
        recording.Dispose();
    }

    [Fact]
    public void Bounds_Available_Immediately()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        var bounds = recording.Bounds;
        Assert.Equal(new Rect(10, 10, 100, 50), bounds);
        recording.Dispose();
    }

    [Fact]
    public void Multiple_Primitives_Union_Bounds()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 50, 50));
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(100, 100, 50, 50));
        });

        var bounds = recording.Bounds;
        Assert.Equal(new Rect(0, 0, 150, 150), bounds);
        recording.Dispose();
    }

    [Fact]
    public void Nested_Recordings_Work()
    {
        var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Green, null, new Rect(0, 0, 20, 20));
        });

        var outer = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner);
            ctx.DrawRectangle(Brushes.Red, null, new Rect(30, 30, 20, 20));
        });

        Assert.NotNull(outer);
        outer.Dispose();
        inner.Dispose();
    }

    [Fact]
    public void HitTest_Inside_Returns_True()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        Assert.True(recording.HitTest(new Point(50, 30)));
        recording.Dispose();
    }

    [Fact]
    public void HitTest_Outside_Returns_False()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        Assert.False(recording.HitTest(new Point(0, 0)));
        recording.Dispose();
    }

    [Fact]
    public void Disposed_Recording_Throws_On_Bounds()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        recording.Dispose();
        Assert.True(recording.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => recording.Bounds);
    }

    [Fact]
    public void Disposed_Recording_Throws_On_HitTest()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        recording.Dispose();
        Assert.Throws<ObjectDisposedException>(() => recording.HitTest(default));
    }

    [Fact]
    public void Recording_With_Transform_Has_Correct_Bounds()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(10, 10)))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 50, 50));
            }
        });

        var bounds = recording.Bounds;
        Assert.Equal(new Rect(10, 10, 50, 50), bounds);
        recording.Dispose();
    }

    [Fact]
    public void Recording_With_Clip_And_Opacity()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushOpacity(0.5))
            using (ctx.PushClip(new Rect(0, 0, 200, 200)))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 100));
            }
        });

        var bounds = recording.Bounds;
        Assert.Equal(new Rect(10, 10, 100, 100), bounds);
        recording.Dispose();
    }

    [Fact]
    public void Recording_With_Pen_Has_Inflated_Bounds()
    {
        var pen = new ImmutablePen(Brushes.Black, 2);
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(null, pen, new Rect(10, 10, 100, 50));
        });

        var bounds = recording.Bounds;
        Assert.True(bounds.Width >= 100);
        Assert.True(bounds.Height >= 50);
        recording.Dispose();
    }

    [Fact]
    public void DrawRecording_With_Matrix_Translates_Bounds()
    {
        using var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        using var outer = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner, Matrix.CreateTranslation(100, 100));
        });

        Assert.Equal(new Rect(100, 100, 10, 10), outer.Bounds);
    }

    [Fact]
    public void DrawRecording_With_Matrix_Scales_Bounds()
    {
        using var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        using var outer = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner, Matrix.CreateScale(3, 2));
        });

        Assert.Equal(new Rect(0, 0, 30, 20), outer.Bounds);
    }

    [Fact]
    public void DrawRecording_With_Identity_Matrix_Matches_Unmatrixed()
    {
        using var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 20, 30, 40));
        });

        using var withMatrix = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner, Matrix.Identity);
        });

        using var withoutMatrix = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner);
        });

        Assert.Equal(withoutMatrix.Bounds, withMatrix.Bounds);
    }

    [Fact]
    public void DrawRecording_With_Matrix_HitTest_Uses_Transformed_Bounds()
    {
        using var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        using var outer = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner, Matrix.CreateTranslation(100, 100));
        });

        Assert.True(outer.HitTest(new Point(105, 105)));
        Assert.False(outer.HitTest(new Point(5, 5)));
    }

    [Fact]
    public void DrawRecording_With_Matrix_Throws_OnNull()
    {
        using var outer = DrawingRecording.Create(_ => { });

        Assert.Throws<ArgumentNullException>(() =>
        {
            using var _ = DrawingRecording.Create(ctx =>
            {
                ctx.DrawRecording(null!, Matrix.CreateTranslation(10, 10));
            });
        });
    }

    [Fact]
    public void Immutable_Parent_Bounds_Include_Nested_Immutable_Child()
    {
        using var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50));
        });

        using var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child);
        });

        Assert.Equal(new Rect(10, 10, 100, 50), parent.Bounds);
        Assert.True(parent.HitTest(new Point(50, 30)));
    }

    [Fact]
    public void DrawRecording_Throws_On_Disposed_Immutable_Recording()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        child.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var _ = DrawingRecording.Create(ctx =>
            {
                ctx.DrawRecording(child);
            });
        });
    }

    [Fact]
    public void GetBounds_Identity_Matches_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 20, 30, 40));
        });

        Assert.Equal(recording.Bounds, recording.GetBounds(Matrix.Identity));
    }

    [Fact]
    public void GetBounds_Translate_Offsets_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        Assert.Equal(
            new Rect(100, 200, 10, 10),
            recording.GetBounds(Matrix.CreateTranslation(100, 200)));
    }

    [Fact]
    public void GetBounds_Scale_Scales_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 20, 30, 40));
        });

        Assert.Equal(
            new Rect(20, 60, 60, 120),
            recording.GetBounds(Matrix.CreateScale(2, 3)));
    }

    [Fact]
    public void GetBounds_Rotate_Gives_Tight_Union_Of_Per_Item_Aabb()
    {
        // Two small shapes at opposite corners. Under 45° rotation about origin,
        // the per-item-AABB union is tighter than the AABB of the unrotated union.
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(-1, -1, 2, 2));
            ctx.DrawRectangle(Brushes.Red, null, new Rect(99, 99, 2, 2));
        });

        var tight = recording.GetBounds(Matrix.CreateRotation(Math.PI / 4));
        var loose = recording.Bounds.TransformToAABB(Matrix.CreateRotation(Math.PI / 4));

        // Tight bounds should fit inside the loose AABB.
        Assert.True(loose.Contains(tight),
            $"tight {tight} should be contained in loose {loose}");
        // And be strictly smaller in area.
        Assert.True(
            tight.Width * tight.Height < loose.Width * loose.Height,
            $"tight area {tight.Width * tight.Height} should be < loose {loose.Width * loose.Height}");
    }

    [Fact]
    public void GetBounds_Empty_Recording_Returns_Default()
    {
        using var recording = DrawingRecording.Create(_ => { });

        Assert.Equal(default, recording.GetBounds(Matrix.CreateTranslation(100, 100)));
    }

    [Fact]
    public void GetBounds_Throws_On_Disposed()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        recording.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => recording.GetBounds(Matrix.Identity));
    }

    [Fact]
    public void Shared_Ownership_Does_Not_Dispose_Child()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, DrawingRecordingOwnership.Shared);
        });

        parent.Dispose();

        Assert.False(child.IsDisposed);
        child.Dispose();
    }

    [Fact]
    public void Owned_Ownership_Disposes_Child_With_Parent()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
        });

        Assert.False(child.IsDisposed);
        parent.Dispose();

        Assert.True(child.IsDisposed);
    }

    [Fact]
    public void Owned_Ownership_Disposes_Transitively()
    {
        var grandchild = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(grandchild, DrawingRecordingOwnership.Owned);
        });
        var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
        });

        parent.Dispose();

        Assert.True(child.IsDisposed);
        Assert.True(grandchild.IsDisposed);
    }

    [Fact]
    public void Owned_Ownership_Handles_Double_Reference()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
            // Referenced twice as Owned — dispose must be idempotent (not duplicated).
            ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
        });

        parent.Dispose();
        Assert.True(child.IsDisposed);
    }

    [Fact]
    public void Owned_Ownership_With_Matrix_Overload()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, Matrix.CreateTranslation(50, 50),
                DrawingRecordingOwnership.Owned);
        });

        parent.Dispose();
        Assert.True(child.IsDisposed);
    }

    [Fact]
    public void DrawRecording_Ownership_Throws_On_Null()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            using var _ = DrawingRecording.Create(ctx =>
            {
                ctx.DrawRecording(null!, DrawingRecordingOwnership.Owned);
            });
        });
    }

    [Fact]
    public void DrawRecording_Ownership_Throws_On_Disposed()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });
        child.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var _ = DrawingRecording.Create(ctx =>
            {
                ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
            });
        });
    }
}
