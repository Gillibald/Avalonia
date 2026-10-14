using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Reactive;
using Avalonia.Rendering.SceneGraph;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Text whose transform changes every frame draws stretched from an earlier raster, and draws
    /// sharp again on the first frame that repeats its transform. The compositor draws only what
    /// changed, so when the motion stops it renders the stretched text once more by itself, and
    /// then stops rendering.
    /// </summary>
    [Collection(MotionBudgetCollection.Name)]
    public class SettleFrameTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";
        private const int Width = 400;
        private const int Height = 240;
        private const int TextHeight = 200;
        private const int MotionFrames = 8;

        public enum Target
        {
            Raster,
            SoftwareGl,
            HardwareGl,
            HardwareAngle,
            SoftwareMetal,
            HardwareMetal,
        }

        public enum Motion
        {
            Rotation,
            Zoom,
        }

        public static IEnumerable<object[]> Motions()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                foreach (var motion in Enum.GetValues<Motion>())
                {
                    yield return new object[] { target, motion };
                }
            }
        }

        public static IEnumerable<object[]> GpuMotions()
            => Motions().Where(m => (Target)m[0] != Target.Raster);

        private static Matrix At(Motion motion, int frame) => motion == Motion.Rotation
            ? Matrix.CreateRotation(Math.PI * (5 + frame * 1.7) / 180) * Matrix.CreateTranslation(60.4, 40.7)
            : Matrix.CreateScale(1 + 0.02 * frame, 1 + 0.02 * frame) * Matrix.CreateTranslation(10.25, 12.5);

        [Theory]
        [MemberData(nameof(Motions))]
        public void Text_Left_Alone_After_A_Motion_Is_Drawn_Sharp_And_Rendering_Goes_Idle(Target target, Motion motion)
        {
            using var environment = new Environment(target);

            var final = At(motion, MotionFrames - 1);
            var expected = environment.RenderStatic(final, null);

            using var run = environment.CreateRun();
            using var harness = environment.CreateHarness();

            var text = harness.AddRun(run);

            for (var frame = 0; frame < MotionFrames; frame++)
            {
                text.RenderTransform = new MatrixTransform(At(motion, frame));
                harness.Services.RunJobs();
            }

            var rendered = harness.Renders;

            harness.TickUntilIdle();

            Assert.InRange(harness.Renders - rendered, 0, 1);
            TransformedAtlasTests.AssertEqual(expected, harness.Read(), $"{target} {motion}");
            harness.AssertIdle();
        }

        public static IEnumerable<object[]> ZoomTargets()
            => Enum.GetValues<Target>().Select(target => new object[] { target });

        [Theory]
        [MemberData(nameof(ZoomTargets))]
        public void Text_Left_Alone_After_Sharp_Unhinted_Zoom_Frames_Is_Drawn_Grid_Fitted(Target target)
        {
            // Every zoom frame rasterizes its glyphs unhinted, sharp but not what a static frame
            // draws, so the motion still ends with one frame that draws the text grid-fitted.
            using var environment = new Environment(target);

            var final = At(Motion.Zoom, MotionFrames - 1);
            var expected = environment.RenderStatic(final, null);
            var budget = MotionRasterBudget.BudgetOverride;
            var glyphCost = MotionRasterBudget.GlyphCostOverride;
            var nonText = MotionRasterBudget.NonTextOverride;
            var zoomRasterPixelCost = MaskGlyphRunRenderer.ZoomRasterPixelCost;

            // The frames' own render times would leave a cold GPU context no headroom.
            MotionRasterBudget.BudgetOverride = 1e9;
            MotionRasterBudget.GlyphCostOverride = 1;
            MotionRasterBudget.NonTextOverride = 0;
            MaskGlyphRunRenderer.ZoomRasterPixelCost = 0;

            try
            {
                using var run = environment.CreateRun();
                using var harness = environment.CreateHarness();

                var text = harness.AddRun(run);
                var stretched = MaskGlyphRunRenderer.StretchedDraws;

                for (var frame = 0; frame < MotionFrames; frame++)
                {
                    text.RenderTransform = new MatrixTransform(At(Motion.Zoom, frame));
                    harness.Services.RunJobs();
                }

                Assert.Equal(stretched, MaskGlyphRunRenderer.StretchedDraws);

                var rendered = harness.Renders;

                harness.TickUntilIdle();

                Assert.Equal(rendered + 1, harness.Renders);
                TransformedAtlasTests.AssertEqual(expected, harness.Read(), target.ToString());
                harness.AssertIdle();
            }
            finally
            {
                MotionRasterBudget.BudgetOverride = budget;
                MotionRasterBudget.GlyphCostOverride = glyphCost;
                MotionRasterBudget.NonTextOverride = nonText;
                MaskGlyphRunRenderer.ZoomRasterPixelCost = zoomRasterPixelCost;
            }
        }

        [Theory]
        [MemberData(nameof(GpuMotions))]
        public void Text_Stopped_While_Other_Content_Keeps_Changing_Is_Drawn_Sharp(Target target, Motion motion)
        {
            // Frames go on for the other content, but only its region redraws; the text left
            // stretched outside that region is drawn again by itself.
            using var environment = new Environment(target);

            var colors = new[] { Colors.Red, Colors.Blue };
            const int changingFrames = 12;
            var final = At(motion, MotionFrames - 1);
            var lastColor = colors[(MotionFrames + changingFrames - 1) % 2];
            var expected = environment.RenderStatic(final, lastColor);

            using var run = environment.CreateRun();
            using var harness = environment.CreateHarness();

            var text = harness.AddRun(run);
            var other = harness.AddBorder();

            for (var frame = 0; frame < MotionFrames + changingFrames; frame++)
            {
                if (frame < MotionFrames)
                {
                    text.RenderTransform = new MatrixTransform(At(motion, frame));
                }

                other.Background = new ImmutableSolidColorBrush(colors[frame % 2]);
                harness.Services.RunJobs();
            }

            harness.TickUntilIdle();

            TransformedAtlasTests.AssertEqual(expected, harness.Read(), $"{target} {motion}");
            harness.AssertIdle();
        }

        [Theory]
        [InlineData(Target.HardwareGl)]
        [InlineData(Target.HardwareAngle)]
        public void A_Run_Still_Stretched_On_Its_Settle_Frame_Requests_No_Further_Frame(Target target)
        {
            // One run drawn under two transforms in every frame never repeats its transform from
            // one draw to the next, so it keeps stretching; once the motion stops, rendering must
            // still go idle after one more frame.
            using var environment = new Environment(target);
            using var run = environment.CreateRun();
            using var harness = environment.CreateHarness();

            var first = harness.AddRun(run);
            var second = harness.AddRun(run);

            for (var frame = 0; frame < MotionFrames; frame++)
            {
                first.RenderTransform = new MatrixTransform(At(Motion.Rotation, frame));
                second.RenderTransform = new MatrixTransform(At(Motion.Zoom, frame));
                harness.Services.RunJobs();
            }

            var rendered = harness.Renders;

            harness.TickUntilIdle();

            Assert.InRange(harness.Renders - rendered, 0, 1);
            harness.AssertIdle();
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void Text_Moving_Inside_A_Bitmap_Cache_Is_Drawn_Sharp_After_The_Motion_Stops(Target target, Motion motion)
        {
            // A bitmap cache renders again only what changed inside it, so the frame that draws the
            // whole target once the motion stops has to render the cached text again too.
            using var environment = new Environment(target);

            var final = At(motion, MotionFrames - 1);
            var expected = environment.RenderStaticCached(final, moveCache: false);

            using var run = environment.CreateRun();
            using var harness = environment.CreateHarness();

            var text = harness.AddCachedRun(run, out _);
            var stretched = MaskGlyphRunRenderer.UnsettledDraws;

            for (var frame = 0; frame < MotionFrames; frame++)
            {
                text.RenderTransform = new MatrixTransform(At(motion, frame));
                harness.Services.RunJobs();
            }

            // The cache renders into an offscreen surface, where an upright zoom stretches on every
            // target here; a rotation there draws sharp in every frame.
            if (motion == Motion.Zoom)
            {
                Assert.NotEqual(stretched, MaskGlyphRunRenderer.UnsettledDraws);
            }

            var rendered = harness.Renders;

            harness.TickUntilIdle();

            Assert.InRange(harness.Renders - rendered, 0, 1);
            TransformedAtlasTests.AssertEqual(expected, harness.Read(), $"{target} {motion}");
            harness.AssertIdle();
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void Text_Inside_A_Bitmap_Cache_Moved_By_The_Cache_Never_Stretches(Target target, Motion motion)
        {
            // The cache renders its content in its own coordinates and the motion only places the
            // cached bitmap, so the text inside never draws under a changing transform.
            using var environment = new Environment(target);

            var final = At(motion, MotionFrames - 1);
            var expected = environment.RenderStaticCached(final, moveCache: true);

            using var run = environment.CreateRun();
            using var harness = environment.CreateHarness();

            harness.AddCachedRun(run, out var cache);

            var stretched = MaskGlyphRunRenderer.UnsettledDraws;

            for (var frame = 0; frame < MotionFrames; frame++)
            {
                cache.RenderTransform = new MatrixTransform(At(motion, frame));
                harness.Services.RunJobs();
            }

            harness.TickUntilIdle();

            Assert.Equal(stretched, MaskGlyphRunRenderer.UnsettledDraws);
            TransformedAtlasTests.AssertEqual(expected, harness.Read(), $"{target} {motion}");
            harness.AssertIdle();
        }

        /// <summary>The typeface and, on GPU targets, the GPU context every harness of a test shares.</summary>
        private sealed class Environment : IDisposable
        {
            private readonly IDisposable _scope;
            private readonly GpuTestContext? _gpu;
            private readonly double _zoomRasterPixelCost;

            public Environment(Target target)
            {
                Target = target;
                _scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                Typeface = typeface;
                _zoomRasterPixelCost = MaskGlyphRunRenderer.ZoomRasterPixelCost;

                // A CPU surface stretches an upright zoom only where rasterizing costs more.
                MaskGlyphRunRenderer.ZoomRasterPixelCost = 1e6;

                if (target != Target.Raster)
                {
                    _gpu = TransformedAtlasTests.CreateGpu(
                        target switch
                        {
                            Target.HardwareAngle => GpuBackend.Angle,
                            Target.SoftwareMetal or Target.HardwareMetal => GpuBackend.Metal,
                            _ => GpuBackend.NativeGl,
                        },
                        software: target is Target.SoftwareGl or Target.SoftwareMetal);
                }
            }

            public Target Target { get; }

            public GlyphTypeface Typeface { get; }

            public ManagedGlyphRunImpl CreateRun()
                => WideRunMaskTests.CreateRun(Typeface, Text, 18, new Point(8.37, 32.61));

            public Harness CreateHarness() => new(_gpu);

            /// <summary>
            /// Renders a run drawn once under <paramref name="transform"/>, and the other content
            /// in <paramref name="otherColor"/>, and checks that the scene then stops rendering.
            /// </summary>
            public byte[] RenderStatic(Matrix transform, Color? otherColor)
            {
                using var run = CreateRun();
                using var harness = CreateHarness();

                harness.AddRun(run).RenderTransform = new MatrixTransform(transform);

                if (otherColor is { } color)
                {
                    harness.AddBorder().Background = new ImmutableSolidColorBrush(color);
                }

                harness.Services.RunJobs();

                var rendered = harness.Renders;

                harness.TickUntilIdle();

                Assert.Equal(rendered, harness.Renders);
                harness.AssertIdle();

                var pixels = harness.Read();

                Assert.Contains(pixels, b => b != 0);

                return pixels;
            }

            /// <summary>
            /// Renders a run inside a bitmap cache, drawn once under <paramref name="transform"/>,
            /// which applies to the cache when <paramref name="moveCache"/> and to the run inside it
            /// otherwise, and checks that the scene then stops rendering.
            /// </summary>
            public byte[] RenderStaticCached(Matrix transform, bool moveCache)
            {
                using var run = CreateRun();
                using var harness = CreateHarness();

                var text = harness.AddCachedRun(run, out var cache);

                (moveCache ? (Visual)cache : text).RenderTransform = new MatrixTransform(transform);
                harness.Services.RunJobs();

                var rendered = harness.Renders;

                harness.TickUntilIdle();

                Assert.Equal(rendered, harness.Renders);
                harness.AssertIdle();

                var pixels = harness.Read();

                Assert.Contains(pixels, b => b != 0);

                return pixels;
            }

            public void Dispose()
            {
                _gpu?.GrContext.Flush();
                _gpu?.Dispose();
                MaskGlyphRunRenderer.ZoomRasterPixelCost = _zoomRasterPixelCost;
                _scope.Dispose();
            }
        }

        /// <summary>A compositor rendering into a surface the test reads, driven tick by tick.</summary>
        private sealed class Harness : IDisposable
        {
            private readonly GpuTestContext? _gpu;
            private readonly GpuSurface? _gpuSurface;
            private readonly FramebufferSurface? _framebuffer;
            private readonly Canvas _canvas = new() { Width = Width, Height = Height };

            public Harness(GpuTestContext? gpu)
            {
                _gpu = gpu;

                IPlatformRenderSurface surface;
                IPlatformGraphics? graphics = null;

                if (gpu is null)
                {
                    surface = _framebuffer = new FramebufferSurface();
                }
                else
                {
                    var skSurface = SKSurface.Create(gpu.GrContext, true,
                        new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));

                    Assert.SkipWhen(skSurface is null, "GPU surface creation failed.");

                    surface = _gpuSurface = new GpuSurface(skSurface!);
                    graphics = new TestGraphics(new TestSkiaGpu(gpu.GrContext));
                }

                Services = new CompositorTestServices(new Size(Width, Height), new PlatformRenderInterface(), graphics,
                    surface);
                Services.TopLevel.Content = _canvas;
                Services.RunJobs();
            }

            public CompositorTestServices Services { get; }

            /// <summary>The frames the compositor has rendered into the surface.</summary>
            public int Renders => _gpuSurface?.Renders ?? _framebuffer!.Renders;

            public RunControl AddRun(ManagedGlyphRunImpl run)
            {
                var control = new RunControl(run) { RenderTransformOrigin = RelativePoint.TopLeft };

                _canvas.Children.Add(control);

                return control;
            }

            /// <summary>Adds a run inside a panel whose content draws through a bitmap cache.</summary>
            public RunControl AddCachedRun(ManagedGlyphRunImpl run, out Panel cache)
            {
                var control = new RunControl(run) { RenderTransformOrigin = RelativePoint.TopLeft };

                cache = new Panel
                {
                    Width = Width, Height = TextHeight, CacheMode = new BitmapCache(),
                    RenderTransformOrigin = RelativePoint.TopLeft
                };
                cache.Children.Add(control);
                _canvas.Children.Add(cache);

                return control;
            }

            public Border AddBorder()
            {
                var border = new Border { Width = Width, Height = Height - TextHeight };

                Canvas.SetTop(border, TextHeight);
                _canvas.Children.Add(border);

                return border;
            }

            /// <summary>Ticks the render loop until it stops, or fails after a bound.</summary>
            public void TickUntilIdle()
            {
                for (var tick = 0; tick < 100 && Services.Timer.Tick is not null; tick++)
                {
                    Services.RunJobs();
                }

                Assert.Null(Services.Timer.Tick);
            }

            /// <summary>Asserts the render loop stays stopped and renders nothing more.</summary>
            public void AssertIdle()
            {
                var rendered = Renders;

                for (var tick = 0; tick < 20; tick++)
                {
                    Services.RunJobs();
                }

                Assert.Null(Services.Timer.Tick);
                Assert.Equal(rendered, Renders);
            }

            public byte[] Read() => _gpuSurface?.Read(_gpu!) ?? _framebuffer!.Read();

            public void Dispose()
            {
                Services.Dispose();
                _gpu?.GrContext.Flush();
                _gpuSurface?.Surface.Dispose();
                _framebuffer?.Dispose();
            }
        }

        private sealed class RunControl : Control
        {
            private readonly ManagedGlyphRunImpl _run;

            public RunControl(ManagedGlyphRunImpl run)
            {
                _run = run;
                Width = SettleFrameTests.Width;
                Height = TextHeight;
            }

            public override void Render(DrawingContext context)
                => context.Custom(new RunOperation(_run, new Rect(0, 0, SettleFrameTests.Width, TextHeight)));
        }

        private sealed class RunOperation : ICustomDrawOperation
        {
            private readonly ManagedGlyphRunImpl _run;

            public RunOperation(ManagedGlyphRunImpl run, Rect bounds)
            {
                _run = run;
                Bounds = bounds;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation? other) => other is RunOperation o && o._run == _run;

            public void Render(ImmediateDrawingContext context)
                => context.PlatformImpl.DrawGlyphRun(Brushes.Black, _run);

            public void Dispose()
            {
            }
        }

        private sealed class FramebufferSurface : IFramebufferPlatformSurface, IDisposable
        {
            private const int Stride = Width * 4;
            private readonly IntPtr _pixels = Marshal.AllocHGlobal(Stride * Height);

            public FramebufferSurface()
            {
                unsafe
                {
                    new Span<byte>((void*)_pixels, Stride * Height).Clear();
                }
            }

            public int Renders { get; private set; }

            public ILockedFramebuffer Lock()
            {
                Renders++;

                return new LockedFramebuffer(_pixels, new PixelSize(Width, Height), Stride, new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul, null);
            }

            public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new FuncFramebufferRenderTarget(Lock);

            public byte[] Read()
            {
                var pixels = new byte[Stride * Height];

                Marshal.Copy(_pixels, pixels, 0, pixels.Length);

                return pixels;
            }

            public void Dispose() => Marshal.FreeHGlobal(_pixels);
        }

        private sealed class GpuSurface : IPlatformRenderSurface
        {
            public GpuSurface(SKSurface surface) => Surface = surface;

            public SKSurface Surface { get; }

            public int Renders { get; set; }

            public unsafe byte[] Read(GpuTestContext gpu)
            {
                gpu.GrContext.Flush();

                var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                fixed (byte* p = pixels)
                {
                    Assert.True(Surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }

                return pixels;
            }
        }

        private sealed class TestGraphics : IPlatformGraphics
        {
            private readonly TestSkiaGpu _gpu;

            public TestGraphics(TestSkiaGpu gpu) => _gpu = gpu;

            public bool UsesSharedContext => true;

            public IPlatformGraphicsContext CreateContext() => throw new NotSupportedException();

            public IPlatformGraphicsContext GetSharedContext() => _gpu;
        }

        /// <summary>A Skia GPU over a test context, rendering into the <see cref="GpuSurface"/> it is given.</summary>
        private sealed class TestSkiaGpu : ISkiaGpu
        {
            private readonly GRContext _grContext;

            public TestSkiaGpu(GRContext grContext) => _grContext = grContext;

            public bool IsLost => false;

            public IPlatformGraphicsContext? PlatformGraphicsContext => null;

            public IDisposable EnsureCurrent() => Disposable.Empty;

            public object? TryGetFeature(Type featureType) => null;

            public ISkiaGpuRenderTarget? TryCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
                => surfaces.OfType<GpuSurface>().FirstOrDefault() is { } surface
                    ? new GpuRenderTarget(_grContext, surface)
                    : null;

            public bool IsReadyToCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces) => true;

            public ISkiaSurface? TryCreateSurface(PixelSize size, ISkiaGpuRenderSession? session) => null;

            public IScopedResource<GRContext>? TryGetGrContext() => null;

            public void Dispose()
            {
            }
        }

        private sealed class GpuRenderTarget : ISkiaGpuRenderTarget
        {
            private readonly GRContext _grContext;
            private readonly GpuSurface _surface;

            public GpuRenderTarget(GRContext grContext, GpuSurface surface)
            {
                _grContext = grContext;
                _surface = surface;
            }

            public ISkiaGpuRenderSession BeginRenderingSession(IRenderTarget.RenderTargetSceneInfo sceneInfo)
            {
                _surface.Renders++;

                return new Session(_grContext, _surface.Surface);
            }

            public void Dispose()
            {
            }
        }

        private sealed class Session : ISkiaGpuRenderSession
        {
            public Session(GRContext grContext, SKSurface surface)
            {
                GrContext = grContext;
                SkSurface = surface;
            }

            public GRContext GrContext { get; }

            public SKSurface SkSurface { get; }

            public double ScaleFactor => 1;

            public GRSurfaceOrigin SurfaceOrigin => GRSurfaceOrigin.TopLeft;

            public void Dispose()
            {
            }
        }
    }
}
