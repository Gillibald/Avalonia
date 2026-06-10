using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Xunit;

#if AVALONIA_SKIA
namespace Avalonia.Skia.RenderTests;

public class DrawingRecordingTests : TestBase
{
    public DrawingRecordingTests() : base(@"Media\DrawingRecording")
    {
    }

    [Fact]
    public async Task Replay_Immutable_Recording()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(20, 20, 60, 60));
            ctx.DrawEllipse(Brushes.Blue, null, new Rect(60, 60, 60, 60));
        });

        var target = new RecordingRenderer(recording)
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task DrawRecording_With_Matrix_Translates_Content()
    {
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Green, null, new Rect(0, 0, 40, 40));
        });

        var target = new RecordingRenderer((control, context) =>
        {
            context.DrawRecording(recording, Matrix.CreateTranslation(20, 20));
            context.DrawRecording(recording, Matrix.CreateTranslation(80, 80));
        })
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task DrawRecording_Nested()
    {
        var inner = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Yellow, null, new Rect(0, 0, 20, 20));
            ctx.DrawEllipse(Brushes.Magenta, null, new Rect(20, 20, 20, 20));
        });
        var outer = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRecording(inner, Matrix.CreateTranslation(10, 10));
            ctx.DrawRecording(inner, Matrix.CreateTranslation(60, 60));
        });

        var target = new RecordingRenderer(outer)
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task DrawingRecordingBrush_Tiles_Recording()
    {
        var tile = DrawingRecording.Create(ctx =>
        {
            ctx.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
            ctx.DrawRectangle(Brushes.Blue, null, new Rect(10, 10, 10, 10));
        });

        var brush = new DrawingRecordingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, 20, 20, RelativeUnit.Absolute),
            SourceRect = new RelativeRect(0, 0, 20, 20, RelativeUnit.Absolute)
        };

        var target = new Border
        {
            Width = 150, Height = 150,
            Background = brush
        };

        await RenderToFile(target);
        CompareImages();
    }

    [Theory]
    [InlineData(TileMode.None)]
    [InlineData(TileMode.FlipX)]
    [InlineData(TileMode.FlipY)]
    [InlineData(TileMode.FlipXY)]
    public async Task DrawingRecordingBrush_TileMode(TileMode mode)
    {
        // Asymmetric tile (red square top-left, blue top-right, lime bottom-left)
        // so every flip mode produces a distinct pattern.
        var tile = DrawingRecording.Create(ctx =>
        {
            ctx.FillRectangle(Brushes.Red, new Rect(0, 0, 12, 12));
            ctx.FillRectangle(Brushes.Blue, new Rect(12, 0, 8, 8));
            ctx.FillRectangle(Brushes.Lime, new Rect(0, 12, 8, 8));
        });

        var brush = new DrawingRecordingBrush(tile)
        {
            TileMode = mode,
            Stretch = Stretch.None,
            SourceRect = new RelativeRect(0, 0, 20, 20, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 20, 20, RelativeUnit.Absolute)
        };

        // The brush is captured by an immutable recording — this exercises the
        // record-time content snapshot that replays on the render thread.
        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, 150, 150));
            ctx.FillRectangle(brush, new Rect(5, 5, 140, 140));
        });

        var target = new RecordingRenderer(recording)
        {
            Width = 150, Height = 150
        };

        var testName = "DrawingRecordingBrush_TileMode_" + mode;
        await RenderToFile(target, testName);
        CompareImages(testName);
    }

    [Fact]
    public async Task DrawingRecordingBrush_SourceRect_Selects_Region()
    {
        // Source recording is a 2x2 colour grid; SourceRect selects only the left
        // column (red over lime), DestinationRect stretches it onto 30x30 tiles.
        // Blue and yellow must not appear in the output.
        var grid = DrawingRecording.Create(ctx =>
        {
            ctx.FillRectangle(Brushes.Red, new Rect(0, 0, 10, 10));
            ctx.FillRectangle(Brushes.Blue, new Rect(10, 0, 10, 10));
            ctx.FillRectangle(Brushes.Lime, new Rect(0, 10, 10, 10));
            ctx.FillRectangle(Brushes.Yellow, new Rect(10, 10, 10, 10));
        });

        var brush = new DrawingRecordingBrush(grid)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            SourceRect = new RelativeRect(0, 0, 10, 20, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 30, 30, RelativeUnit.Absolute)
        };

        var recording = DrawingRecording.Create(ctx =>
        {
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, 150, 150));
            ctx.FillRectangle(brush, new Rect(5, 5, 140, 140));
        });

        var target = new RecordingRenderer(recording)
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages();
    }

    [Fact]
    public async Task PushOpacityMask_Luminance_Differs_From_Alpha()
    {
        // A red square masked by a horizontal black-to-white gradient.
        // Alpha mode: mask alpha is constant 1, so the red passes through unchanged.
        // Luminance mode: mask RGB→alpha goes from black (0) to white (1), so the
        // result fades from transparent on the left to opaque red on the right.
        var mask = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0, Colors.Black),
                new ImmutableGradientStop(1, Colors.White)
            },
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
            endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));

        var target = new RecordingRenderer((control, context) =>
        {
            // White background so the difference is visible.
            context.FillRectangle(Brushes.White, new Rect(0, 0, 200, 100));

            using (context.PushOpacityMask(mask, new Rect(10, 10, 80, 80), MaskType.Luminance))
                context.FillRectangle(Brushes.Red, new Rect(10, 10, 80, 80));

            using (context.PushOpacityMask(mask, new Rect(110, 10, 80, 80), MaskType.Alpha))
                context.FillRectangle(Brushes.Red, new Rect(110, 10, 80, 80));
        })
        {
            Width = 200, Height = 100
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task PushLayer_BlendMode_Multiply()
    {
        var target = new RecordingRenderer((control, context) =>
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, 150, 150));

            // Magenta over yellow with multiply: result = red.
            context.FillRectangle(Brushes.Yellow, new Rect(20, 20, 80, 80));

            using (context.PushLayer(new LayerOptions
            {
                BlendMode = BitmapBlendingMode.Multiply
            }))
            {
                context.FillRectangle(Brushes.Magenta, new Rect(50, 50, 80, 80));
            }
        })
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task PushLayer_Group_Opacity_Differs_From_Per_Primitive()
    {
        // Two overlapping semi-transparent disks.
        // Per-primitive opacity (PushOpacity): each disk blends with backdrop
        // independently; overlap region is double-blended (darker).
        // Group opacity (PushLayer.Opacity): both disks compose into a layer
        // first; layer is then blended with backdrop once.
        var target = new RecordingRenderer((control, context) =>
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, 200, 100));

            // Left: per-primitive opacity 0.5 around two opaque disks.
            using (context.PushOpacity(0.5))
            {
                context.DrawEllipse(Brushes.Red, null, new Rect(20, 20, 50, 50));
                context.DrawEllipse(Brushes.Red, null, new Rect(50, 30, 50, 50));
            }

            // Right: group opacity 0.5 (PushLayer) around the same two disks.
            using (context.PushLayer(new LayerOptions { Opacity = 0.5 }))
            {
                context.DrawEllipse(Brushes.Red, null, new Rect(120, 20, 50, 50));
                context.DrawEllipse(Brushes.Red, null, new Rect(150, 30, 50, 50));
            }
        })
        {
            Width = 200, Height = 100
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task PushLayer_Isolation_Bounds_Blend_Modes()
    {
        var target = new RecordingRenderer((control, context) =>
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, 200, 100));

            // Left: multiply blends with the yellow backdrop (red intersection).
            context.FillRectangle(Brushes.Yellow, new Rect(10, 20, 80, 40));
            using (context.PushLayer(new LayerOptions { BlendMode = BitmapBlendingMode.Multiply }))
                context.FillRectangle(Brushes.Magenta, new Rect(30, 40, 50, 40));

            // Right: the isolated group bounds the blend — multiply sees only the
            // group's transparent backdrop, so the magenta stays magenta.
            context.FillRectangle(Brushes.Yellow, new Rect(110, 20, 80, 40));
            using (context.PushLayer(new LayerOptions { Isolate = true }))
            using (context.PushLayer(new LayerOptions { BlendMode = BitmapBlendingMode.Multiply }))
                context.FillRectangle(Brushes.Magenta, new Rect(130, 40, 50, 40));
        })
        {
            Width = 200, Height = 100
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task PushLayer_With_Blur_Effect()
    {
        var target = new RecordingRenderer((control, context) =>
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, 150, 150));

            using (context.PushLayer(new LayerOptions
            {
                Effect = new ImmutableBlurEffect(8)
            }))
            {
                context.FillRectangle(Brushes.Black, new Rect(40, 40, 70, 70));
            }
        })
        {
            Width = 150, Height = 150
        };

        await RenderToFile(target);
        CompareImages(skipImmediate: true);
    }

    /// <summary>
    /// A control that renders content via a delegate or by replaying a
    /// pre-recorded <see cref="DrawingRecording"/>.
    /// </summary>
    private sealed class RecordingRenderer : Control
    {
        private readonly DrawingRecording? _recording;
        private readonly Action<RecordingRenderer, DrawingContext>? _render;

        public RecordingRenderer(DrawingRecording recording) => _recording = recording;

        public RecordingRenderer(Action<RecordingRenderer, DrawingContext> render) => _render = render;

        public override void Render(DrawingContext context)
        {
            if (_recording != null)
                context.DrawRecording(_recording);
            else
                _render!(this, context);
        }
    }
}
#endif
