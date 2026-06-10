using System;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Moq;
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
        // the per-item-AABB union is much tighter than the AABB of the unrotated
        // union — the rotated unioned-rect spans the full 144-unit diagonal width
        // while the per-item AABBs each stay near the rotation axis.
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(-1, -1, 2, 2));
            ctx.DrawRectangle(Brushes.Red, null, new Rect(99, 99, 2, 2));
        });

        var tight = recording.GetBounds(Matrix.CreateRotation(Math.PI / 4));
        var loose = recording.Bounds.TransformToAABB(Matrix.CreateRotation(Math.PI / 4));

        // Tight bounds should be dramatically smaller in area than the loose AABB.
        var tightArea = tight.Width * tight.Height;
        var looseArea = loose.Width * loose.Height;
        Assert.True(
            tightArea < looseArea / 10,
            $"tight area {tightArea:F1} should be at least 10x smaller than loose {looseArea:F1}");
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

    [Fact]
    public void PushOpacityMask_Default_Behaves_As_Alpha()
    {
        // The alpha overload forwards to the MaskType overload with
        // MaskType.Alpha — bounds and hit behavior must be unchanged.
        var mask = new ImmutableSolidColorBrush(Colors.White);

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushOpacityMask(mask, new Rect(0, 0, 100, 100)))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
            }
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
        Assert.True(recording.HitTest(new Point(20, 20)));
    }

    [Fact]
    public void PushOpacityMask_Luminance_Preserves_Bounds()
    {
        var mask = new ImmutableSolidColorBrush(Colors.White);

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushOpacityMask(mask, new Rect(0, 0, 100, 100), MaskType.Luminance))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
            }
        });

        // Mask type is purely compositional — geometric bounds are unaffected.
        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
        Assert.True(recording.HitTest(new Point(20, 20)));
    }

    [Fact]
    public void PushOpacityMask_Luminance_Hits_Unchanged_By_Type()
    {
        var mask = new ImmutableSolidColorBrush(Colors.White);

        using var alphaRec = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushOpacityMask(mask, new Rect(0, 0, 100, 100), MaskType.Alpha))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });
        using var lumaRec = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushOpacityMask(mask, new Rect(0, 0, 100, 100), MaskType.Luminance))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(alphaRec.Bounds, lumaRec.Bounds);
        Assert.Equal(alphaRec.HitTest(new Point(20, 20)), lumaRec.HitTest(new Point(20, 20)));
    }

    [Fact]
    public void PushLayer_Passthrough_Has_Unchanged_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions()))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
        Assert.True(recording.HitTest(new Point(20, 20)));
    }

    [Fact]
    public void PushLayer_With_Opacity_Preserves_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Opacity = 0.5 }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
    }

    [Fact]
    public void PushLayer_With_BlendMode_Preserves_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions
            {
                BlendMode = Avalonia.Media.Imaging.BitmapBlendingMode.Multiply
            }))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
            }
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
    }

    [Fact]
    public void PushLayer_With_Blur_Effect_Inflates_Bounds()
    {
        var blur = new ImmutableBlurEffect(5);
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = blur }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        // Blur radius expands bounds on every side.
        var bounds = recording.Bounds;
        Assert.True(bounds.Width > 30, $"bounds width {bounds.Width} should exceed 30");
        Assert.True(bounds.Height > 30, $"bounds height {bounds.Height} should exceed 30");
        Assert.True(bounds.X < 10, $"bounds X {bounds.X} should be < 10");
        Assert.True(bounds.Y < 10, $"bounds Y {bounds.Y} should be < 10");
    }

    [Fact]
    public void PushLayer_With_DropShadow_Effect_Expands_Bounds()
    {
        var drop = new ImmutableDropShadowEffect(10, 10, 0, Colors.Black, 1.0);
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = drop }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 30, 30));
        });

        // Drop shadow expands bounds to cover the offset shape.
        var bounds = recording.Bounds;
        Assert.True(bounds.Right >= 40, $"bounds.Right {bounds.Right} should cover offset shape");
        Assert.True(bounds.Bottom >= 40, $"bounds.Bottom {bounds.Bottom} should cover offset shape");
    }

    [Fact]
    public void PushLayer_Explicit_Bounds_Does_Not_Extend_Recording_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            // LayerOptions.Bounds is a backend hint for the compositor's offscreen
            // buffer extent; it does not produce visible pixels on its own.
            using (ctx.PushLayer(new LayerOptions { Bounds = new Rect(0, 0, 200, 200) }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
    }

    [Fact]
    public void PushLayer_With_Offset_Effect_Shifts_Bounds()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = new ImmutableOffsetEffect(20, 10) }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 30, 30));
        });

        // The output pads towards the offset direction.
        var bounds = recording.Bounds;
        Assert.Equal(0, bounds.X);
        Assert.Equal(0, bounds.Y);
        Assert.True(bounds.Right >= 50, $"bounds.Right {bounds.Right} should cover the shifted content");
        Assert.True(bounds.Bottom >= 40, $"bounds.Bottom {bounds.Bottom} should cover the shifted content");
    }

    [Fact]
    public void PushLayer_With_ColorMatrix_Effect_Preserves_Bounds()
    {
        var matrix = new double[20];
        matrix[0] = matrix[6] = matrix[12] = matrix[18] = 1; // identity

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = new ImmutableColorMatrixEffect(matrix) }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
    }

    [Fact]
    public void PushLayer_With_Composite_Effect_Accumulates_Bounds()
    {
        var composite = new ImmutableCompositeEffect(new IEffect[]
        {
            new ImmutableBlurEffect(4),
            new ImmutableOffsetEffect(15, 0),
        });

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = composite }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 30, 30));
        });

        // Blur padding plus the offset towards +x.
        var bounds = recording.Bounds;
        Assert.True(bounds.X < 0, $"bounds.X {bounds.X} should include blur padding");
        Assert.True(bounds.Right >= 45 + 4, $"bounds.Right {bounds.Right} should cover blur + offset");
    }

    [Fact]
    public void Composite_Effect_Equality_Is_Structural()
    {
        var a = new ImmutableCompositeEffect(new IEffect[] { new ImmutableBlurEffect(4), new ImmutableOffsetEffect(1, 2) });
        var b = new ImmutableCompositeEffect(new IEffect[] { new ImmutableBlurEffect(4), new ImmutableOffsetEffect(1, 2) });
        var c = new ImmutableCompositeEffect(new IEffect[] { new ImmutableBlurEffect(5), new ImmutableOffsetEffect(1, 2) });

        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void PushLayer_Isolate_Forces_A_Layer()
    {
        // Isolation must defeat the passthrough elision: an all-default layer is
        // elided, an isolated one is not.
        Assert.True(new LayerOptions().IsPassthrough);
        Assert.False(new LayerOptions { Isolate = true }.IsPassthrough);

        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Isolate = true }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
        Assert.True(recording.HitTest(new Point(20, 20)));
    }

    [Fact]
    public void PushLayer_Nested_Balances_Correctly()
    {
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Opacity = 0.5 }))
            using (ctx.PushLayer(new LayerOptions
            {
                BlendMode = Avalonia.Media.Imaging.BitmapBlendingMode.Multiply
            }))
            {
                ctx.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 30, 30));
            }
        });

        Assert.Equal(new Rect(10, 10, 30, 30), recording.Bounds);
    }

    private static IBrush? ReplayAndCaptureBrush(DrawingRecording recording)
    {
        var mockImpl = new Mock<IDrawingContextImpl>();
        mockImpl.Setup(x => x.Transform).Returns(Matrix.Identity);
        IBrush? captured = null;
        mockImpl.Setup(x => x.DrawRectangle(
                It.IsAny<IBrush?>(), It.IsAny<IPen?>(), It.IsAny<RoundedRect>(), It.IsAny<BoxShadows>()))
            .Callback<IBrush?, IPen?, RoundedRect, BoxShadows>((b, _, _, _) => captured = b);

        using (var platformCtx = new PlatformDrawingContext(mockImpl.Object, false))
            platformCtx.DrawRecording(recording);

        return captured;
    }

    [Fact]
    public void Immutable_Create_Snapshots_Mutable_Brush()
    {
        var brush = new SolidColorBrush(Colors.Red);
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(brush, null, new Rect(0, 0, 100, 50));
        });

        // Later mutations must not leak into the immutable recording.
        brush.Color = Colors.Blue;

        var captured = ReplayAndCaptureBrush(recording);
        var immutable = Assert.IsType<ImmutableSolidColorBrush>(captured);
        Assert.Equal(Colors.Red, immutable.Color);
    }

    [Fact]
    public void Immutable_Create_Snapshots_Mutable_Pen()
    {
        var pen = new Pen(new SolidColorBrush(Colors.Red), 4);
        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawLine(pen, new Point(0, 50), new Point(100, 50));
        });

        var boundsBefore = recording.Bounds;

        // Mutating the live pen must not affect the recording's bounds.
        pen.Thickness = 40;

        Assert.Equal(boundsBefore, recording.Bounds);
    }

    [Fact]
    public void Immutable_Create_Snapshots_Scene_Brush()
    {
        using var source = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Lime, null, new Rect(0, 0, 8, 8));
        });
        var pattern = new DrawingRecordingBrush(source) { TileMode = TileMode.Tile };

        using var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(pattern, null, new Rect(0, 0, 100, 100));
        });

        // The live scene brush is an AvaloniaObject and cannot be touched at
        // replay time; the recording must capture an immutable content snapshot.
        var captured = ReplayAndCaptureBrush(recording);
        Assert.NotNull(captured);
        Assert.NotSame(pattern, captured);
        Assert.IsAssignableFrom<IImmutableBrush>(captured);
    }

    private sealed class FakeBrush : IBrush
    {
        public double Opacity => 1;
        public ITransform? Transform => null;
        public RelativePoint TransformOrigin => default;
    }

    [Fact]
    public void Immutable_Create_Throws_On_Brush_That_Cannot_Be_Snapshotted()
    {
        Assert.Throws<InvalidOperationException>(() => DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(new FakeBrush(), null, new Rect(0, 0, 10, 10));
        }));
    }

    [Fact]
    public void Owned_Children_Disposed_When_Record_Delegate_Throws()
    {
        var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        });

        Assert.Throws<InvalidOperationException>(() => DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, DrawingRecordingOwnership.Owned);
            throw new InvalidOperationException("record failed");
        }));

        // Ownership transferred at the DrawRecording call; the failed recording
        // must not leak the child.
        Assert.True(child.IsDisposed);
    }

    [Fact]
    public void PushLayer_Snapshots_Mutable_Effect()
    {
        var blur = new BlurEffect { Radius = 10 };
        using var recording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = blur }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(20, 20, 30, 30));
        });

        var boundsBefore = recording.Bounds;

        // Mutating the live effect must not affect the recorded layer.
        blur.Radius = 100;

        Assert.Equal(boundsBefore, recording.Bounds);
    }

    private sealed class FakeEffect : IEffect
    {
    }

    [Fact]
    public void PushLayer_Throws_On_Effect_That_Cannot_Be_Snapshotted()
    {
        Assert.Throws<InvalidOperationException>(() => DrawingRecording.Create(ctx =>
        {
            using (ctx.PushLayer(new LayerOptions { Effect = new FakeEffect() }))
                ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
        }));
    }

    [Fact]
    public void DrawRecording_With_Rotation_Has_Per_Item_Tight_Bounds()
    {
        // Two small far-apart squares: per-item transformed bounds stay narrow,
        // while transforming the child's overall AABB would be ~10x wider. This
        // verifies the matrix overload fuses into the recording node instead of
        // wrapping the child's united bounds in a transform.
        using var child = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
            ctx.DrawRectangle(Brushes.Red, null, new Rect(90, 90, 10, 10));
        });

        using var parent = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(child, Matrix.CreateRotation(Math.PI / 4));
        });

        Assert.Equal(new Rect(-8, 0, 16, 142), parent.Bounds);
    }
}
