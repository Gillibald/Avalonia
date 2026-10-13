using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// A GPU draws a frame of a run whose transform keeps changing sharp, as a static frame at
    /// that transform, while the frame's rasterization budget affords it, and stretches an
    /// earlier raster otherwise. The runs of a frame share one budget, and a frame draws sharp
    /// only when the text that moved, or was about to move, in the frame before it was estimated
    /// at less than twice the budget. A frame that runs out of budget stretches every run for a
    /// while, or until the motion stops, rather than switching between sharp and stretched
    /// frames, and the frame after that while tries again; a try that runs out of budget again
    /// doubles the next hold. Costs and budgets are injected, so every decision here is
    /// independent of the machine.
    /// </summary>
    public class MotionRasterBudgetTests
    {
        private const string Text = "Hamburgefonstiv 0123456789";
        private const int Width = 520;
        private const int Height = 420;

        /// <summary>The injected cost of one glyph; a run of <see cref="Text"/> is estimated at this times 26.</summary>
        private const double GlyphCost = 1;

        private static readonly double s_runCost = Text.Length * GlyphCost;

        public enum Target
        {
            SoftwareGl,
            HardwareGl,
            HardwareAngle,
        }

        public enum Motion
        {
            Rotation,
            Zoom,
            ZoomRotation,
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

        public static IEnumerable<object[]> Targets()
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                yield return new object[] { target };
            }
        }

        private static Matrix At(Motion motion, int frame, double y = 0) => motion switch
        {
            Motion.Rotation => Matrix.CreateRotation(Math.PI * (5 + frame * 1.7) / 180) *
                               Matrix.CreateTranslation(160.4, 120.7 + y),
            Motion.Zoom => Matrix.CreateScale(1 + 0.03 * frame, 1 + 0.03 * frame) *
                           Matrix.CreateTranslation(10.25, 12.5 + y),
            _ => Matrix.CreateScale(1 + 0.03 * frame, 1 + 0.03 * frame) * Matrix.CreateRotation(Math.PI * 15 / 180) *
                 Matrix.CreateTranslation(60.4, 40.7 + y),
        };

        [Theory]
        [MemberData(nameof(Motions))]
        public void Motion_The_Budget_Affords_Draws_Every_Frame_Like_A_Static_Draw(Target target, Motion motion)
        {
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1e9, glyphCost: GlyphCost);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            for (var frame = 0; frame < 16; frame++)
            {
                var unsettled = MaskGlyphRunRenderer.UnsettledDraws;
                var drawn = output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                // A sharp frame is what a static frame at the transform draws, so nothing needs
                // to be drawn again when the motion stops.
                Assert.True(unsettled == MaskGlyphRunRenderer.UnsettledDraws, $"{target} {motion} frame {frame} stretched");

                using var fresh = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

                var expected = output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    context.DrawGlyphRun(Brushes.Black, fresh);
                });

                Assert.True(Array.Exists(expected, b => b != 0));
                TransformedAtlasTests.AssertEqual(expected, drawn, $"{target} {motion} frame {frame}");
            }
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void Motion_The_Budget_Cannot_Afford_Stretches(Target target, Motion motion)
        {
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: s_runCost / 2, glyphCost: GlyphCost);
            using var animated = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));

            var stretched = new List<int>();
            var expected = new List<int>();

            for (var frame = 0; frame < 16; frame++)
            {
                var before = MaskGlyphRunRenderer.UnsettledDraws;

                output.Frame(context =>
                {
                    context.Transform = At(motion, frame);
                    context.DrawGlyphRun(Brushes.Black, animated);
                });

                if (MaskGlyphRunRenderer.UnsettledDraws != before)
                {
                    stretched.Add(frame);
                }

                if (frame >= TransformChurnGuard.Threshold && !CrossesBand(target, motion, frame))
                {
                    expected.Add(frame);
                }
            }

            Assert.Equal(expected, stretched);
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void The_Runs_Of_A_Frame_Share_One_Budget(Target target, Motion motion)
        {
            // Room for three runs a frame: all three draw sharp, in every frame. Ten frames keep
            // the zooms within the stretch band.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 3.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.All(stretched, frames => Assert.Empty(frames));
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void The_Runs_After_The_Budget_Is_Spent_Stretch_And_So_Do_The_Frames_After(Target target,
            Motion motion)
        {
            // Room for two runs a frame: the third run of the first moving frame stretches, and
            // so does every run of the frames after it.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.Equal(Range(moving + 1, 10), stretched[0]);
            Assert.Equal(Range(moving + 1, 10), stretched[1]);
            Assert.Equal(Range(moving, 10), stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Frame_Past_The_Budget_Stretches_Every_Run_For_The_Hold(Target target)
        {
            // In the first moving frame a third run takes the room the last run would have had,
            // and then leaves. Every run stretches for the hold although the budget would afford
            // the two left at once, and they draw sharp after it.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int end = moving + MotionRasterBudget.HoldFrames + 1;
            var stretched = Animate(output, runs.All, end + 4, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving);

            Assert.Equal(Range(moving + 1, end), stretched[0]);
            Assert.Equal(Range(moving, end), stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Motions))]
        public void A_Scene_Estimated_Past_Twice_The_Budget_Stretches_From_Its_First_Moving_Frame(Target target,
            Motion motion)
        {
            // Three runs and room for one. The frames before the motion is recognized draw the
            // runs under changing transforms and show the scene at more than twice the budget, so
            // the first moving frame stretches every run instead of trying the budget.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1.2 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            var stretched = Animate(output, runs.All, 10, motion, (_, _) => true);

            Assert.All(stretched, frames => Assert.Equal(Range(moving, 10), frames));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_Estimated_Past_Twice_The_Budget_Is_Never_Tried(Target target)
        {
            // The same scene keeps stretching every run through what would be several holds.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 1.2 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int frames = moving + 3 * (MotionRasterBudget.HoldFrames + 1) + 4;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation, (_, _) => true);

            Assert.All(stretched, run => Assert.Equal(Range(moving, frames), run));
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Scene_That_Keeps_Running_Out_Of_Budget_Is_Tried_Again_After_Doubling_Holds(Target target)
        {
            // Three runs and room for two: every try runs out of budget on the third run, so each
            // hold lasts twice as long as the one before.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            var tried = new[] { moving, moving + hold + 1, moving + hold + 1 + 2 * hold + 1 };
            var frames = tried[2] + 4;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation, (_, _) => true);
            var expected = Range(moving, frames).FindAll(frame => Array.IndexOf(tried, frame) < 0);

            Assert.Equal(expected, stretched[0]);
            Assert.Equal(expected, stretched[1]);
            Assert.Equal(Range(moving, frames), stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void A_Try_That_Fits_The_Budget_Ends_The_Doubling(Target target)
        {
            // The first try runs out of budget; the third run then leaves, the try after the hold
            // fits, and when the third run comes back and the budget runs out again the hold is
            // the first one's length again.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int hold = MotionRasterBudget.HoldFrames;
            const int back = moving + hold + 7;
            const int frames = back + hold + 5;
            var stretched = Animate(output, runs.All, frames, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving || frame >= back);

            // The first run is sharp on the first try, after the first hold, until the third run
            // comes back, and on the try after the second hold.
            var sharp = new List<int> { moving, back + hold + 1 };

            for (var frame = moving + hold + 1; frame <= back; frame++)
            {
                sharp.Add(frame);
            }

            Assert.Equal(Range(moving, frames).FindAll(frame => !sharp.Contains(frame)), stretched[0]);
            Assert.Equal(Range(moving, frames).FindAll(frame => frame < moving + hold + 1 || frame >= back),
                stretched[2]);
        }

        [Theory]
        [MemberData(nameof(Targets))]
        public void Motion_After_A_Stop_Is_Weighed_Anew(Target target)
        {
            // A held frame ends the motion, and with it the hold: the next motion draws sharp
            // when the moving text of the frame before it fit the budget.
            using var output = new FrameTarget(target);
            using var scope = WideRunMaskTests.CreateEnvironment(out var typeface);
            using var budget = new InjectedBudget(budget: 2.5 * s_runCost, glyphCost: GlyphCost);
            using var runs = new Runs(typeface, 3);

            const int moving = TransformChurnGuard.Threshold;
            const int held = moving + 4;

            // The rotation holds for one frame and moves on.
            var stretched = Animate(output, runs.All, held + 8, Motion.Rotation,
                (run, frame) => run != 1 || frame <= moving,
                frame => frame <= held ? frame : frame - 1);

            Assert.Equal(Range(moving + 1, held + 1), stretched[0]);
            Assert.Equal(Range(moving, held + 1), stretched[2]);
        }

        /// <summary>The frames from <paramref name="from"/> up to, not including, <paramref name="to"/>.</summary>
        private static List<int> Range(int from, int to)
        {
            var frames = new List<int>();

            for (var frame = from; frame < to; frame++)
            {
                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>
        /// Draws <paramref name="frames"/> frames of <paramref name="runs"/>, run i 40 px further
        /// down per index and only where <paramref name="drawn"/> says so, at the transform of the
        /// frame's <paramref name="step"/>; returns per run the frames it stretched in.
        /// </summary>
        private static List<int>[] Animate(FrameTarget output, ManagedGlyphRunImpl[] runs, int frames, Motion motion,
            Func<int, int, bool> drawn, Func<int, int>? step = null)
        {
            var stretched = new List<int>[runs.Length];

            for (var i = 0; i < runs.Length; i++)
            {
                stretched[i] = new List<int>();
            }

            for (var frame = 0; frame < frames; frame++)
            {
                var at = step?.Invoke(frame) ?? frame;
                var current = frame;

                output.Frame(context =>
                {
                    for (var i = 0; i < runs.Length; i++)
                    {
                        if (!drawn(i, current))
                        {
                            continue;
                        }

                        var before = MaskGlyphRunRenderer.UnsettledDraws;

                        context.Transform = At(motion, at, 40 * i);
                        context.DrawGlyphRun(Brushes.Black, runs[i]);

                        if (MaskGlyphRunRenderer.UnsettledDraws != before)
                        {
                            stretched[i].Add(current);
                        }
                    }
                });
            }

            return stretched;
        }

        /// <summary>
        /// Whether a stretching run's zoom at <paramref name="frame"/> leaves the band around the
        /// scale it last rasterized at: the last frame before the motion was recognized, or the
        /// last crossing. A software GPU stretches an upright zoom without bound.
        /// </summary>
        private static bool CrossesBand(Target target, Motion motion, int frame)
        {
            if (motion == Motion.Rotation || frame < TransformChurnGuard.Threshold ||
                motion == Motion.Zoom && target == Target.SoftwareGl)
            {
                return false;
            }

            static double Scale(int f) => 1 + 0.03 * f;

            var anchor = Scale(TransformChurnGuard.Threshold - 1);

            for (var f = TransformChurnGuard.Threshold; f <= frame; f++)
            {
                if (Scale(f) / anchor > MaskGlyphRunRenderer.MaxStretchScale)
                {
                    if (f == frame)
                    {
                        return true;
                    }

                    anchor = Scale(f);
                }
            }

            return false;
        }

        /// <summary>Runs of <see cref="Text"/> at 18 px, disposed together.</summary>
        private sealed class Runs : IDisposable
        {
            public Runs(GlyphTypeface typeface, int count)
            {
                All = new ManagedGlyphRunImpl[count];

                for (var i = 0; i < count; i++)
                {
                    All[i] = WideRunMaskTests.CreateRun(typeface, Text, 18, new Point(8.37, 32.61));
                }
            }

            public ManagedGlyphRunImpl[] All { get; }

            public void Dispose()
            {
                foreach (var run in All)
                {
                    run.Dispose();
                }
            }
        }

        /// <summary>Sets the frame budget and the cost per glyph for the scope.</summary>
        private sealed class InjectedBudget : IDisposable
        {
            private readonly double? _budget = MotionRasterBudget.BudgetOverride;
            private readonly double? _glyphCost = MotionRasterBudget.GlyphCostOverride;

            public InjectedBudget(double budget, double glyphCost)
            {
                MotionRasterBudget.BudgetOverride = budget;
                MotionRasterBudget.GlyphCostOverride = glyphCost;
            }

            public void Dispose()
            {
                MotionRasterBudget.BudgetOverride = _budget;
                MotionRasterBudget.GlyphCostOverride = _glyphCost;
            }
        }

        /// <summary>
        /// A GPU surface drawn by a new context per frame, as a window's render passes draw it,
        /// so every frame has a budget of its own.
        /// </summary>
        private sealed class FrameTarget : IDisposable
        {
            private readonly GpuTestContext _gpu;
            private readonly SKSurface _surface;

            public FrameTarget(Target target)
            {
                _gpu = TransformedAtlasTests.CreateGpu(target == Target.HardwareAngle ? GpuBackend.Angle : GpuBackend.NativeGl,
                    software: target == Target.SoftwareGl);

                var surface = SKSurface.Create(_gpu.GrContext, true,
                    new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));

                if (surface is null)
                {
                    _gpu.Dispose();
                    Assert.Skip("GPU surface creation failed.");
                }

                _surface = surface;
            }

            public unsafe byte[] Frame(Action<DrawingContextImpl> draw)
            {
                _surface.Canvas.Clear(SKColors.Transparent);

                using (var context = TransformedAtlasTests.CreateContext(_gpu, _surface))
                {
                    draw(context);
                }

                _gpu.GrContext.Flush();

                var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new byte[info.BytesSize];

                fixed (byte* p = pixels)
                {
                    Assert.True(_surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0));
                }

                return pixels;
            }

            public void Dispose()
            {
                _surface.Dispose();
                _gpu.GrContext.Flush();
                _gpu.Dispose();
            }
        }
    }
}
