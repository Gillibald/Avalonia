using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Utilities;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The render-thread state a glyph run builds as it is drawn (composed run masks, sprite
    /// sets, the decision of its last atlas draw) belongs to what draws the run: render data and
    /// recordings. It is released, and its glyph cache budget charges credited, once nothing
    /// draws the run any more and no frame that drew it is still open, without waiting for the
    /// collector. These tests measure process-wide budget bytes, so they share the collection of
    /// the other budget tests, which runs alone.
    /// </summary>
    [Collection(nameof(GlyphCacheLimitTests))]
    public class GlyphRunReleaseTests
    {
        private const int Width = 400;
        private const int Height = 120;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Run_No_Render_Data_Draws_Any_More_Releases_Its_State_At_The_Next_Frame(bool rotated) =>
            OnOwnThread(() => ReleaseWhenRenderDataGoes(rotated));

        private static void ReleaseWhenRenderDataGoes(bool rotated)
        {
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var surface = CreateSurface();
            var budget = GlyphRasterServices.Current.Budget;
            var run = WideRunMaskTests.CreateRun(typeface, "Render data owns what a run draws", 18, new Point(8.3, 40.2));
            using var reference = RefCountable.Create<IGlyphRunImpl>(run);

            DropEarlierFrames();

            var before = RunStateBytes(budget);
            var stream = Record(reference);

            Replay(surface, stream, rotated);

            Assert.True(RunStateBytes(budget) > before, "the draw charged nothing");
            Assert.True(StateCount(run, rotated) > 0, "the draw kept no state");

            // The visual is gone: its render data releases the run, while the run itself stays
            // alive with the text layout that made it.
            stream.DisposeResources();
            stream.Dispose();

            using (budget.BeginFrame())
            {
            }

            Assert.True(reference.IsAlive);
            Assert.Equal(before, RunStateBytes(budget));
            Assert.Equal(0, StateCount(run, rotated));
        }

        [Theory]
        [MemberData(nameof(GlyphCacheLimitTests.HardwareContexts), MemberType = typeof(GlyphCacheLimitTests))]
        public void A_Run_No_Render_Data_Draws_Any_More_Releases_Its_Atlas_Sprites_On_A_Hardware_Gpu(GpuBackend backend) =>
            OnOwnThread(() =>
            {
                using var gpu = TransformedAtlasTests.CreateGpu(backend, false);
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = SKSurface.Create(gpu.GrContext, true,
                    new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                var budget = GlyphRasterServices.Current.Budget;
                var run = WideRunMaskTests.CreateRun(typeface, "Upright sprites from the atlas", 18, new Point(8.3, 40.2));
                using var reference = RefCountable.Create<IGlyphRunImpl>(run);

                DropEarlierFrames();

                var before = RunStateBytes(budget);
                var stream = Record(reference);

                using (var context = TransformedAtlasTests.CreateContext(gpu, surface))
                {
                    stream.Replay(context);
                    context.FlushGlyphBatch();
                }

                Assert.NotNull(run.UprightDecision);
                Assert.True(RunStateBytes(budget) > before, "the draw charged nothing");

                stream.DisposeResources();
                stream.Dispose();

                using (budget.BeginFrame())
                {
                }

                Assert.Null(run.UprightDecision);
                Assert.Equal(0, run.TransformedSprites.Count);
                Assert.Equal(before, RunStateBytes(budget));
            });

        [Fact]
        public void A_Run_Drawn_By_Other_Render_Data_Keeps_Its_State() =>
            OnOwnThread(() =>
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = CreateSurface();
                var budget = GlyphRasterServices.Current.Budget;
                var run = WideRunMaskTests.CreateRun(typeface, "Recorded again before the old goes", 18, new Point(8.3, 40.2));
                using var reference = RefCountable.Create<IGlyphRunImpl>(run);

                DropEarlierFrames();

                var first = Record(reference);

                Replay(surface, first, true);

                var held = RunStateBytes(budget);

                // A redraw of the visual records new render data before the old is released.
                var second = Record(reference);

                first.DisposeResources();
                first.Dispose();

                using (budget.BeginFrame())
                {
                }

                Assert.Equal(held, RunStateBytes(budget));
                Assert.True(StateCount(run, true) > 0);

                second.DisposeResources();
                second.Dispose();
            });

        [Fact]
        public void A_Disposed_Run_Keeps_Its_State_Until_The_Frame_That_Drew_It_Ends() =>
            OnOwnThread(() =>
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = CreateSurface();
                var budget = GlyphRasterServices.Current.Budget;
                var run = WideRunMaskTests.CreateRun(typeface, "Disposed while its frame is open", 18, new Point(8.3, 40.2));

                DropEarlierFrames();

                var before = RunStateBytes(budget);
                TransformedRunState state;

                using (var context = CreateContext(surface))
                {
                    context.Transform = Matrix.CreateRotation(0.3);
                    context.DrawGlyphRun(Brushes.Black, run);
                    state = run.TransformedSprites;

                    // A text layout drawn into this frame and disposed before the frame ends: the
                    // frame's pending draws may still hold the sprites.
                    run.Dispose();

                    Assert.True(state.Count > 0, "the sprites went while the frame drawing them was open");
                }

                using (budget.BeginFrame())
                {
                }

                Assert.Equal(0, state.Count);
                Assert.Equal(before, RunStateBytes(budget));
            });

        [Fact]
        public void A_Run_Disposed_On_Another_Thread_Keeps_Its_State_Until_The_Drawing_Frame_Ends() =>
            OnOwnThread(() =>
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = CreateSurface();
                var budget = GlyphRasterServices.Current.Budget;
                var run = WideRunMaskTests.CreateRun(typeface, "Disposed by the user interface", 18, new Point(8.3, 40.2));

                DropEarlierFrames();

                var before = RunStateBytes(budget);
                TransformedRunState state;

                using (var context = CreateContext(surface))
                {
                    context.Transform = Matrix.CreateRotation(0.3);
                    context.DrawGlyphRun(Brushes.Black, run);
                    state = run.TransformedSprites;

                    var disposer = new Thread(run.Dispose);

                    disposer.Start();
                    disposer.Join();

                    Assert.True(state.Count > 0, "another thread released the sprites of an open frame");
                }

                using (budget.BeginFrame())
                {
                }

                Assert.Equal(0, state.Count);
                Assert.Equal(before, RunStateBytes(budget));
            });

        [Fact]
        public void A_Run_Released_By_Its_Render_Data_And_Then_Disposed_Credits_Its_Bytes_Once() =>
            OnOwnThread(() =>
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = CreateSurface();
                var budget = GlyphRasterServices.Current.Budget;
                var run = WideRunMaskTests.CreateRun(typeface, "Released once, whoever goes last", 18, new Point(8.3, 40.2));
                var reference = RefCountable.Create<IGlyphRunImpl>(run);

                DropEarlierFrames();

                var before = RunStateBytes(budget);
                var stream = Record(reference);

                Replay(surface, stream, false);
                Replay(surface, stream, true);

                stream.DisposeResources();
                stream.Dispose();

                using (budget.BeginFrame())
                {
                }

                reference.Dispose();
                run.Dispose();

                using (budget.BeginFrame())
                {
                }

                Assert.Equal(before, RunStateBytes(budget));
            });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_Run_Drawn_Again_After_Its_State_Was_Released_Draws_The_Same_Pixels(bool rotated) =>
            OnOwnThread(() =>
            {
                using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
                using var surface = CreateSurface();
                var run = WideRunMaskTests.CreateRun(typeface, "The same text after a tab switch", 18, new Point(8.3, 40.2));
                using var reference = RefCountable.Create<IGlyphRunImpl>(run);

                DropEarlierFrames();

                var first = Record(reference);

                Replay(surface, first, rotated);

                var expected = Snapshot(surface);

                first.DisposeResources();
                first.Dispose();

                using (GlyphRasterServices.Current.Budget.BeginFrame())
                {
                }

                var second = Record(reference);

                Replay(surface, second, rotated);

                Assert.Equal(expected, Snapshot(surface));

                second.DisposeResources();
                second.Dispose();
            });

        private static RenderDataStream Record(IRef<IGlyphRunImpl> reference)
        {
            var stream = new RenderDataStream();

            stream.DrawGlyphRun(Brushes.Black, reference.Clone());

            return stream;
        }

        private static void Replay(SKSurface surface, RenderDataStream stream, bool rotated)
        {
            surface.Canvas.Clear(SKColors.White);

            using var context = CreateContext(surface);

            context.Transform = rotated ? Matrix.CreateRotation(0.3) : Matrix.Identity;
            stream.Replay(context);
        }

        private static long RunStateBytes(GlyphCacheBudget budget)
            => budget.BytesOf(GlyphCachePoolKind.RunMasks) + budget.BytesOf(GlyphCachePoolKind.SpriteSets);

        // An upright draw on a raster surface keeps a run mask, a rotated one a sprite set.
        private static int StateCount(ManagedGlyphRunImpl run, bool rotated)
            => rotated ? run.TransformedSprites.Count : run.RunMasks.Count;

        private static SKSurface CreateSurface()
            => SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        private static DrawingContextImpl CreateContext(SKSurface surface)
            => new(new DrawingContextImpl.CreateInfo { Surface = surface, Dpi = new Vector(96, 96) });

        private static byte[] Snapshot(SKSurface surface)
        {
            using var image = surface.Snapshot();
            using var pixels = image.PeekPixels();

            return pixels.GetPixelSpan().ToArray();
        }

        /// <summary>
        /// Runs <paramref name="test"/> on a thread of its own: a pooled test thread may still hold
        /// a drawing context another test left open, in whose frame every context of the test
        /// would be drawn.
        /// </summary>
        private static void OnOwnThread(Action test)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    test();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        /// <summary>
        /// Lets frames an earlier test left open go stale, so they pin nothing this test draws.
        /// </summary>
        private static void DropEarlierFrames()
        {
            for (var i = 0; i <= GlyphCacheBudget.IdleFrames; i++)
            {
                using (GlyphRasterServices.Current.Budget.BeginFrame())
                {
                }
            }
        }
    }
}
